using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace BookCatalog.Gateway.Services;

public sealed class ProxyHandler(
    IHttpClientFactory httpClientFactory,
    GatewayOptions options,
    BackendPool pool,
    PriorityGate gate,
    ResponseCache cache,
    GatewayMetrics metrics,
    BalancingSettings balancing,
    ILogger<ProxyHandler> logger)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade",
        "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer"
    };

    private static readonly HashSet<string> ResponseHeadersSetByGateway = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Content-Type", "Date", "Server"
    };

    public async Task HandleAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var priority = PriorityGate.Parse(request.Headers["X-Priority"]);
        var stickyKey = StickyKey(context);
        context.Response.Headers["X-Priority"] = priority.ToString().ToUpperInvariant();
        context.Response.Headers["X-Balancing"] = stickyKey is null ? "round-robin" : "sticky";

        var cacheable = ResponseCache.IsCacheable(request);
        var cacheKey = cacheable ? ResponseCache.KeyFor(request) : null;
        var cacheStatus = "-";

        if (cacheKey is not null)
        {
            if (ResponseCache.ClientBypasses(request))
            {
                cache.CountBypass();
                cacheStatus = "BYPASS";
            }
            else
            {
                var (cached, remaining) = await cache.GetAsync(cacheKey);
                if (cached is not null)
                {
                    await WriteCachedAsync(context, cached, remaining);
                    Record(context, started, cached.StatusCode, cached.Instance ?? "cache", "HIT", priority, 0, stickyKey);
                    return;
                }
                cacheStatus = "MISS";
            }
            context.Response.Headers["X-Cache"] = cacheStatus;
        }

        var body = await ReadBodyAsync(request, context.RequestAborted);

        IDisposable lease;
        try
        {
            using var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            queueTimeout.CancelAfter(TimeSpan.FromSeconds(options.QueueTimeoutSeconds));
            lease = await gate.EnterAsync(priority, queueTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Priority queue timeout.");
            Record(context, started, StatusCodes.Status503ServiceUnavailable, "-", cacheStatus, priority, 0, stickyKey);
            return;
        }

        UpstreamResult? result;
        using (lease)
        {
            result = await ForwardWithRetriesAsync(context, body, stickyKey);
        }

        if (result is null)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "No healthy API instance is available.");
            Record(context, started, StatusCodes.Status503ServiceUnavailable, "-", cacheStatus, priority, 0, stickyKey);
            return;
        }

        if (cacheKey is not null && result.StatusCode == StatusCodes.Status200OK)
        {
            await cache.SetAsync(cacheKey, new CachedResponse(
                result.StatusCode, result.ContentType, result.Body, result.Backend));
        }

        if (ResponseCache.InvalidatesBooks(request) && result.StatusCode is >= 200 and < 300)
        {
            await cache.InvalidateBooksAsync();
            // The other databases receive the change through the sync service about a second later;
            // a second invalidation drops anything cached from a database that had not caught up yet.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(options.SyncPropagationSeconds));
                await cache.InvalidateBooksAsync();
            });
        }

        await WriteUpstreamAsync(context, result);
        Record(context, started, result.StatusCode, result.Backend, cacheStatus, priority, result.Attempts, stickyKey);
    }

    private string? StickyKey(HttpContext context)
    {
        var request = context.Request;
        var clientId = request.Headers["X-Client-Id"].ToString();
        if (!string.IsNullOrWhiteSpace(clientId)) return clientId;
        if (balancing.Mode != BalancingMode.Sticky) return null;

        if (request.Cookies.TryGetValue("bc_client", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            return cookie;

        var forwarded = request.Headers["X-Real-IP"].ToString();
        return string.IsNullOrWhiteSpace(forwarded)
            ? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous"
            : forwarded;
    }

    private async Task<UpstreamResult?> ForwardWithRetriesAsync(HttpContext context, byte[]? body, string? stickyKey)
    {
        var http = httpClientFactory.CreateClient("upstream");
        var method = context.Request.Method;
        var idempotent = !HttpMethods.IsPost(method) && !HttpMethods.IsPatch(method);
        var attempts = 0;
        UpstreamResult? lastFailure = null;

        foreach (var backend in pool.Order(stickyKey))
        {
            if (attempts >= options.MaxAttempts) break;
            if (!backend.Circuit.TryAcquire()) continue;
            attempts++;

            var started = Stopwatch.GetTimestamp();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.UpstreamTimeoutSeconds));
                using var upstreamRequest = CreateUpstreamRequest(context, backend, body);
                using var response = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var result = await UpstreamResult.FromAsync(response, backend.Id, attempts, timeout.Token);
                var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (result.StatusCode >= 500)
                {
                    backend.Circuit.RecordFailure();
                    backend.RecordResult(false, latency);
                    lastFailure = result;
                    if (idempotent && result.StatusCode is 502 or 503 or 504) continue;
                    return result;
                }

                backend.Circuit.RecordSuccess();
                backend.RecordResult(true, latency);
                return result;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                backend.Circuit.ReleaseTrial();
                return null;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                backend.Circuit.RecordFailure();
                backend.RecordResult(false, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                logger.LogWarning("Request {Method} {Path} to {Backend} failed: {Message}",
                    method, context.Request.Path, backend.Id, exception.Message);

                var connectFailed = exception is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                };
                if (!idempotent && !connectFailed)
                {
                    return UpstreamResult.Error(StatusCodes.Status502BadGateway,
                        $"{backend.Id} failed while processing the request.", backend.Id, attempts);
                }
            }
        }

        return lastFailure;
    }

    private static HttpRequestMessage CreateUpstreamRequest(HttpContext context, Backend backend, byte[]? body)
    {
        var request = context.Request;
        var message = new HttpRequestMessage(
            new HttpMethod(request.Method),
            $"{backend.Address}{request.Path}{request.QueryString}");

        if (body is not null) message.Content = new ByteArrayContent(body);

        foreach (var header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (remoteIp is not null)
            message.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteIp);
        return message;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or 0 && StringValues.IsNullOrEmpty(request.Headers.TransferEncoding)) return null;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task WriteUpstreamAsync(HttpContext context, UpstreamResult result)
    {
        var response = context.Response;
        response.StatusCode = result.StatusCode;
        foreach (var (name, values) in result.Headers)
        {
            if (HopByHopHeaders.Contains(name) || ResponseHeadersSetByGateway.Contains(name)) continue;
            response.Headers[name] = values;
        }
        response.Headers["X-Gateway-Backend"] = result.Backend;
        response.Headers["X-Gateway-Attempts"] = result.Attempts.ToString();
        if (result.ContentType is not null) response.ContentType = result.ContentType;
        if (result.Body.Length > 0) await response.Body.WriteAsync(result.Body, context.RequestAborted);
    }

    private static async Task WriteCachedAsync(HttpContext context, CachedResponse cached, TimeSpan? remaining)
    {
        var response = context.Response;
        response.StatusCode = cached.StatusCode;
        response.Headers["X-Cache"] = "HIT";
        if (remaining is not null) response.Headers["X-Cache-TTL"] = ((int)remaining.Value.TotalSeconds).ToString();
        response.Headers["X-Gateway-Backend"] = "cache";
        if (cached.Instance is not null) response.Headers["X-Api-Instance"] = cached.Instance;
        if (cached.ContentType is not null) response.ContentType = cached.ContentType;
        await response.Body.WriteAsync(cached.Body, context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { status = statusCode, error = message });
    }

    private void Record(HttpContext context, long started, int statusCode, string backend, string cacheStatus,
        RequestPriority priority, int attempts, string? stickyKey)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return;

        metrics.Record(new RequestRecord(
            DateTimeOffset.UtcNow,
            context.Request.Method,
            path + context.Request.QueryString,
            statusCode,
            backend,
            Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            cacheStatus,
            priority.ToString().ToUpperInvariant(),
            attempts,
            stickyKey is not null));
    }
}

public sealed class UpstreamResult
{
    public required int StatusCode { get; init; }
    public required string Backend { get; init; }
    public required int Attempts { get; init; }
    public required byte[] Body { get; init; }
    public string? ContentType { get; init; }
    public List<(string Name, string[] Values)> Headers { get; init; } = [];

    public static async Task<UpstreamResult> FromAsync(
        HttpResponseMessage response, string backend, int attempts, CancellationToken cancellationToken)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Select(h => (h.Key, h.Value.ToArray()))
            .ToList();
        return new UpstreamResult
        {
            StatusCode = (int)response.StatusCode,
            Backend = response.Headers.TryGetValues("X-Api-Instance", out var instance)
                ? instance.FirstOrDefault() ?? backend
                : backend,
            Attempts = attempts,
            Body = await response.Content.ReadAsByteArrayAsync(cancellationToken),
            ContentType = response.Content.Headers.ContentType?.ToString(),
            Headers = headers
        };
    }

    public static UpstreamResult Error(int statusCode, string message, string backend, int attempts) => new()
    {
        StatusCode = statusCode,
        Backend = backend,
        Attempts = attempts,
        Body = System.Text.Encoding.UTF8.GetBytes($"{{\"status\":{statusCode},\"error\":\"{message}\"}}"),
        ContentType = "application/json"
    };
}
