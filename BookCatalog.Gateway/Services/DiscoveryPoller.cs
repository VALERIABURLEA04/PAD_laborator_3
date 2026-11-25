using System.Net.Http.Json;

namespace BookCatalog.Gateway.Services;

public sealed class DiscoveryState
{
    public DateTimeOffset? LastPoll { get; set; }
    public bool Reachable { get; set; }
    public string? LastError { get; set; }
}

public sealed class DiscoveryPoller(
    IHttpClientFactory httpClientFactory,
    GatewayOptions options,
    BackendPool pool,
    DiscoveryState state,
    ILogger<DiscoveryPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient("control");
        var url = $"{options.DiscoveryUrl.TrimEnd('/')}/services?name={Uri.EscapeDataString(options.ServiceName)}";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var services = await http.GetFromJsonAsync<List<DiscoveredService>>(url, stoppingToken) ?? [];
                pool.Apply(services);
                if (!state.Reachable) logger.LogInformation("Service discovery reachable; {Count} instances.", services.Count);
                state.Reachable = true;
                state.LastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (state.Reachable) logger.LogWarning("Service discovery unreachable: {Message}", exception.Message);
                state.Reachable = false;
                state.LastError = exception.Message;
            }

            state.LastPoll = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(options.DiscoveryPollSeconds), stoppingToken);
        }
    }
}

public sealed class HealthChecker(
    IHttpClientFactory httpClientFactory,
    GatewayOptions options,
    BackendPool pool,
    ILogger<HealthChecker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient("control");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(pool.All.Select(backend => CheckAsync(http, backend, stoppingToken)));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.HealthCheckSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(HttpClient http, Backend backend, CancellationToken stoppingToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        var wasHealthy = backend.Healthy;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync($"{backend.Address}/health", timeout.Token);
            backend.Healthy = response.IsSuccessStatusCode;
            backend.LastHealthError = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            backend.Healthy = false;
            backend.LastHealthError = exception is OperationCanceledException ? "timeout" : exception.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        backend.LastHealthCheck = DateTimeOffset.UtcNow;
        backend.LastHealthCheckMs = TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
        if (wasHealthy != backend.Healthy)
        {
            logger.LogInformation("Backend {Id} is now {State}.", backend.Id, backend.Healthy ? "UP" : "DOWN");
        }
    }
}
