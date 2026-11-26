using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace BookCatalog.Gateway.Services;

/// <summary>
/// Kubernetes counterpart of <see cref="DockerControl"/>: lists the pods of the namespace and "kills"
/// one by deleting it, after which its Deployment or StatefulSet recreates it.
/// </summary>
public sealed class KubernetesControl : IContainerControl, IDisposable
{
    private const string AccountPath = "/var/run/secrets/kubernetes.io/serviceaccount";
    private readonly GatewayOptions _options;
    private readonly HttpClient _http;
    private readonly string _namespace;
    private readonly string? _setupError;

    public KubernetesControl(GatewayOptions options)
    {
        _options = options;
        _namespace = File.Exists($"{AccountPath}/namespace")
            ? File.ReadAllText($"{AccountPath}/namespace").Trim()
            : "default";

        var handler = new HttpClientHandler();
        X509Certificate2? authority = null;
        try
        {
            if (File.Exists($"{AccountPath}/ca.crt"))
                authority = X509CertificateLoader.LoadCertificateFromFile($"{AccountPath}/ca.crt");
        }
        catch (Exception exception)
        {
            _setupError = $"Cannot read the cluster CA certificate: {exception.Message}";
        }

        if (authority is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
        }

        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "kubernetes.default.svc";
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ListPodsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not ContainerControlUnavailableException and not OperationCanceledException)
        {
            throw new ContainerControlUnavailableException($"Cannot read the pod list: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<ContainerInfo>> ListPodsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/namespaces/{_namespace}/pods", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var result = new List<ContainerInfo>();
        foreach (var pod in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var metadata = pod.GetProperty("metadata");
            if (!metadata.TryGetProperty("labels", out var labels) || !labels.TryGetProperty("app", out var app))
                continue;

            var service = labels.TryGetProperty("statefulset.kubernetes.io/pod-name", out var podName)
                ? podName.GetString()!
                : app.GetString()!;
            if (_options.HiddenServicePrefixes.Any(prefix => service.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var status = pod.GetProperty("status");
            var phase = status.TryGetProperty("phase", out var phaseElement) ? phaseElement.GetString() ?? "" : "";
            if (phase == "Succeeded") continue;

            var ready = status.TryGetProperty("containerStatuses", out var containers) &&
                        containers.EnumerateArray().All(c => c.GetProperty("ready").GetBoolean());
            var restarts = status.TryGetProperty("containerStatuses", out containers)
                ? containers.EnumerateArray().Sum(c => c.GetProperty("restartCount").GetInt32())
                : 0;
            var state = metadata.TryGetProperty("deletionTimestamp", out _) ? "restarting"
                : phase == "Running" && ready ? "running"
                : phase == "Failed" ? "exited"
                : "restarting";

            result.Add(new ContainerInfo(
                service,
                metadata.GetProperty("name").GetString() ?? "",
                state,
                $"pod {metadata.GetProperty("name").GetString()} · {phase} · restarts {restarts}",
                IsProtected(service)));
        }

        return result.OrderBy(c => c.Service, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ContainerInfo> SetStateAsync(string service, string desiredState, CancellationToken cancellationToken)
    {
        if (IsProtected(service))
            throw new InvalidOperationException($"Service '{service}' is protected and cannot be changed from the dashboard.");

        var grace = desiredState.ToLowerInvariant() switch
        {
            "stopped" or "killed" => 0,
            "running" => 2,
            _ => throw new ArgumentException("State must be 'running' or 'stopped'.")
        };

        var pods = await ListAsync(cancellationToken);
        var pod = pods.FirstOrDefault(p => p.Service.Equals(service, StringComparison.OrdinalIgnoreCase) && p.State != "restarting")
            ?? pods.FirstOrDefault(p => p.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Service '{service}' was not found.");

        using var _ = await SendAsync(HttpMethod.Delete,
            $"api/v1/namespaces/{_namespace}/pods/{pod.ContainerId}?gracePeriodSeconds={grace}", cancellationToken);
        return pod with { State = "restarting", Status = $"pod {pod.ContainerId} deleted · Kubernetes recreates it" };
    }

    private bool IsProtected(string service) =>
        _options.ProtectedServices.Contains(service, StringComparer.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (_setupError is not null) throw new ContainerControlUnavailableException(_setupError);
        var tokenPath = $"{AccountPath}/token";
        if (!File.Exists(tokenPath))
            throw new ContainerControlUnavailableException("No Kubernetes service account token is mounted.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim());
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ContainerControlUnavailableException($"Kubernetes API is not reachable: {exception.Message}");
        }

        if (response.IsSuccessStatusCode) return response;
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new ContainerControlUnavailableException($"Kubernetes API refused the request ({(int)response.StatusCode}): {error}");
    }

    public void Dispose() => _http.Dispose();
}
