using System.Net;
using System.Net.Http.Json;

namespace BookCatalog.Api.Services;

public sealed record DiscoveryOptions(
    string Url,
    string ServiceName,
    string InstanceId,
    string Address,
    TimeSpan HeartbeatInterval);

public sealed class DiscoveryRegistration(
    HttpClient http,
    DiscoveryOptions options,
    ILogger<DiscoveryRegistration> logger) : BackgroundService
{
    private bool _registered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_registered)
                    await RegisterAsync(stoppingToken);
                else
                    await HeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _registered = false;
                logger.LogWarning("Service discovery unavailable: {Message}", exception.Message);
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{options.Url}/services", new
        {
            id = options.InstanceId,
            name = options.ServiceName,
            address = options.Address
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        _registered = true;
        logger.LogInformation("Registered {InstanceId} at {Address} in service discovery.",
            options.InstanceId, options.Address);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PutAsync(
            $"{options.Url}/services/{options.InstanceId}/heartbeat", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await RegisterAsync(cancellationToken);
            return;
        }
        response.EnsureSuccessStatusCode();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await http.DeleteAsync($"{options.Url}/services/{options.InstanceId}", timeout.Token);
                logger.LogInformation("Deregistered {InstanceId} from service discovery.", options.InstanceId);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Could not deregister: {Message}", exception.Message);
            }
        }
        await base.StopAsync(cancellationToken);
    }
}
