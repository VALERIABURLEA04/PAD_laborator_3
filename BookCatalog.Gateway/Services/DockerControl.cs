using System.Net.Sockets;
using System.Text.Json;

namespace BookCatalog.Gateway.Services;

/// <summary>Talks to the Docker Engine API through the mounted /var/run/docker.sock.</summary>
public sealed class DockerControl : IContainerControl, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly HttpClient _http;

    public DockerControl(GatewayOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.DockerSocket), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var filter = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["label"] = [$"com.docker.compose.project={_options.ComposeProject}"]
        });

        using var document = await GetJsonAsync($"containers/json?all=true&filters={Uri.EscapeDataString(filter)}",
            cancellationToken);
        var result = new List<ContainerInfo>();
        foreach (var container in document.RootElement.EnumerateArray())
        {
            if (!container.TryGetProperty("Labels", out var labels) ||
                !labels.TryGetProperty("com.docker.compose.service", out var serviceElement))
            {
                continue;
            }

            var service = serviceElement.GetString() ?? "?";
            if (_options.HiddenServicePrefixes.Any(prefix => service.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (labels.TryGetProperty("com.docker.compose.oneoff", out var oneOff) && oneOff.GetString() == "True")
                continue;

            result.Add(new ContainerInfo(
                service,
                container.GetProperty("Id").GetString() ?? "",
                container.GetProperty("State").GetString() ?? "unknown",
                container.GetProperty("Status").GetString() ?? "",
                IsProtected(service)));
        }

        return result.OrderBy(c => c.Service, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ContainerInfo> SetStateAsync(string service, string desiredState, CancellationToken cancellationToken)
    {
        if (IsProtected(service))
            throw new InvalidOperationException($"Service '{service}' is protected and cannot be changed from the dashboard.");

        var containers = await ListAsync(cancellationToken);
        var container = containers.FirstOrDefault(c => c.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Service '{service}' was not found.");

        var path = desiredState.ToLowerInvariant() switch
        {
            "stopped" or "killed" => $"containers/{container.ContainerId}/kill",
            "running" when container.State == "running" => $"containers/{container.ContainerId}/restart?t=2",
            "running" => $"containers/{container.ContainerId}/start",
            _ => throw new ArgumentException("State must be 'running' or 'stopped'.")
        };

        using var response = await SendAsync(HttpMethod.Post, path, cancellationToken);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 304)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Docker refused the request: {error}");
        }

        return (await ListAsync(cancellationToken))
            .First(c => c.Service.Equals(service, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsProtected(string service) =>
        _options.ProtectedServices.Contains(service, StringComparer.OrdinalIgnoreCase);

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(new HttpRequestMessage(method, path), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ContainerControlUnavailableException(
                $"Docker socket {_options.DockerSocket} is not reachable: {exception.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
