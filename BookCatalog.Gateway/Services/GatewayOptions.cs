namespace BookCatalog.Gateway.Services;

public sealed class GatewayOptions
{
    public string DiscoveryUrl { get; set; } = "http://discovery:8080";
    public string ServiceName { get; set; } = "bookcatalog-api";
    public int DiscoveryPollSeconds { get; set; } = 2;
    public int HealthCheckSeconds { get; set; } = 3;
    public int UpstreamTimeoutSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 3;
    public int CircuitFailureThreshold { get; set; } = 3;
    public int CircuitOpenSeconds { get; set; } = 15;
    public int MaxConcurrency { get; set; } = 4;
    public int QueueTimeoutSeconds { get; set; } = 30;
    public int CacheTtlSeconds { get; set; } = 30;
    public int SyncPropagationSeconds { get; set; } = 3;
    public string BalancingMode { get; set; } = "RoundRobin";
    public string Orchestrator { get; set; } = "docker";
    public string DockerSocket { get; set; } = "/var/run/docker.sock";
    public string ComposeProject { get; set; } = "bookcatalog";
    public string[] ProtectedServices { get; set; } = [];
    public string[] HiddenServicePrefixes { get; set; } = [];
    public string SelfBaseUrl { get; set; } = "http://localhost:8080";
}

public enum BalancingMode
{
    RoundRobin,
    Sticky
}

public sealed class BalancingSettings(BalancingMode initial)
{
    private volatile int _mode = (int)initial;

    public BalancingMode Mode
    {
        get => (BalancingMode)_mode;
        set => _mode = (int)value;
    }
}
