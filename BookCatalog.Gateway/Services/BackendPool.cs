using System.Collections.Concurrent;
using System.Text;

namespace BookCatalog.Gateway.Services;

public sealed class Backend(string id, string address, CircuitBreaker circuit)
{
    private long _requests;
    private long _failures;

    public string Id { get; } = id;
    public string Address { get; set; } = address;
    public CircuitBreaker Circuit { get; } = circuit;

    public string RegistryStatus { get; set; } = "up";
    public long MillisecondsSinceHeartbeat { get; set; }
    public long HeartbeatCount { get; set; }

    public volatile bool Healthy = true;
    public volatile bool FaultSimulated;
    public DateTimeOffset? LastHealthCheck { get; set; }
    public double LastHealthCheckMs { get; set; }
    public string? LastHealthError { get; set; }

    public double LastLatencyMs { get; set; }
    public long Requests => Interlocked.Read(ref _requests);
    public long Failures => Interlocked.Read(ref _failures);

    public bool IsRoutable =>
        RegistryStatus == "up" && Healthy && Circuit.State != CircuitState.Open;

    public void RecordResult(bool success, double latencyMs)
    {
        Interlocked.Increment(ref _requests);
        if (!success) Interlocked.Increment(ref _failures);
        LastLatencyMs = latencyMs;
    }
}

public sealed record DiscoveredService(
    string Id,
    string Name,
    string Address,
    string Status,
    long MillisecondsSinceHeartbeat,
    long HeartbeatCount);

public sealed class BackendPool(GatewayOptions options)
{
    private readonly ConcurrentDictionary<string, Backend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private long _roundRobin = -1;

    public IReadOnlyList<Backend> All =>
        _backends.Values.OrderBy(b => b.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public Backend? Find(string id) => _backends.GetValueOrDefault(id);

    public void Apply(IReadOnlyCollection<DiscoveredService> services)
    {
        foreach (var gone in _backends.Keys.Except(services.Select(s => s.Id), StringComparer.OrdinalIgnoreCase).ToList())
            _backends.TryRemove(gone, out _);

        foreach (var service in services)
        {
            var backend = _backends.GetOrAdd(service.Id, id => new Backend(
                id,
                service.Address,
                new CircuitBreaker(
                    options.CircuitFailureThreshold,
                    TimeSpan.FromSeconds(options.CircuitOpenSeconds))));

            if (backend.RegistryStatus != "up" && service.Status == "up")
            {
                backend.Healthy = true;
                backend.FaultSimulated = false;
            }

            backend.Address = service.Address;
            backend.RegistryStatus = service.Status;
            backend.MillisecondsSinceHeartbeat = service.MillisecondsSinceHeartbeat;
            backend.HeartbeatCount = service.HeartbeatCount;
        }
    }

    /// <summary>
    /// Returns routable backends in the order they should be tried.
    /// Round robin rotates the list on every call. Sticky uses rendezvous (highest random weight)
    /// hashing: every backend gets a score hash(key + id) and the highest score wins, so a client
    /// always lands on the same node and only moves when that node disappears.
    /// </summary>
    public IReadOnlyList<Backend> Order(string? stickyKey)
    {
        var routable = All.Where(b => b.IsRoutable).ToList();
        if (routable.Count == 0) return routable;

        if (stickyKey is not null)
        {
            return routable
                .OrderByDescending(b => Fnv1A($"{stickyKey}|{b.Id}"))
                .ToList();
        }

        var start = (int)((ulong)Interlocked.Increment(ref _roundRobin) % (ulong)routable.Count);
        return routable.Skip(start).Concat(routable.Take(start)).ToList();
    }

    private static uint Fnv1A(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}
