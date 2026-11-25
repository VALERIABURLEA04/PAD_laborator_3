using System.Diagnostics;

namespace BookCatalog.Gateway.Services;

public sealed record LoadTestRequest(
    int Total = 120,
    int HighPercent = 20,
    int LowPercent = 50,
    string Path = "/api/books",
    bool BypassCache = true);

public sealed class LoadTester(IHttpClientFactory httpClientFactory, GatewayOptions options)
{
    private int _running;

    public async Task<object> RunAsync(LoadTestRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("A load test is already running.");

        try
        {
            var total = Math.Clamp(request.Total, 1, 2000);
            var high = total * Math.Clamp(request.HighPercent, 0, 100) / 100;
            var low = Math.Min(total - high, total * Math.Clamp(request.LowPercent, 0, 100) / 100);
            var normal = total - high - low;

            // LOW requests are sent first and HIGH last, so a lower average wait for HIGH
            // can only come from the priority queue, not from arrival order.
            var plan = Enumerable.Repeat(RequestPriority.Low, low)
                .Concat(Enumerable.Repeat(RequestPriority.Normal, normal))
                .Concat(Enumerable.Repeat(RequestPriority.High, high))
                .ToList();

            var http = httpClientFactory.CreateClient("load-test");
            var started = Stopwatch.GetTimestamp();
            var results = await Task.WhenAll(plan.Select(priority => SendAsync(http, request, priority, cancellationToken)));

            return new
            {
                total,
                durationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                maxConcurrency = options.MaxConcurrency,
                byPriority = results
                    .GroupBy(r => r.Priority)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        priority = g.Key.ToString().ToUpperInvariant(),
                        count = g.Count(),
                        averageMs = Math.Round(g.Average(r => r.LatencyMs), 1),
                        maxMs = Math.Round(g.Max(r => r.LatencyMs), 1)
                    }),
                byBackend = results
                    .GroupBy(r => r.Backend)
                    .OrderBy(g => g.Key)
                    .Select(g => new { backend = g.Key, count = g.Count() }),
                byStatus = results
                    .GroupBy(r => r.StatusCode)
                    .OrderBy(g => g.Key)
                    .Select(g => new { status = g.Key, count = g.Count() })
            };
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<(RequestPriority Priority, double LatencyMs, int StatusCode, string Backend)> SendAsync(
        HttpClient http, LoadTestRequest request, RequestPriority priority, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{options.SelfBaseUrl.TrimEnd('/')}{request.Path}");
        message.Headers.TryAddWithoutValidation("X-Priority", priority.ToString().ToLowerInvariant());
        if (request.BypassCache) message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var backend = response.Headers.TryGetValues("X-Gateway-Backend", out var values)
                ? values.FirstOrDefault() ?? "?"
                : "?";
            return (priority, Stopwatch.GetElapsedTime(started).TotalMilliseconds, (int)response.StatusCode, backend);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (priority, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, "error");
        }
    }
}
