using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

var heartbeatTimeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Discovery:HeartbeatTimeoutSeconds", 15));
var purgeAfter = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Discovery:PurgeAfterSeconds", 120));

var app = builder.Build();
var services = new ConcurrentDictionary<string, ServiceInstance>(StringComparer.OrdinalIgnoreCase);

ServiceView ToView(ServiceInstance instance)
{
    var now = DateTimeOffset.UtcNow;
    var status = instance.Deregistered ? "down"
        : now - instance.LastHeartbeat > heartbeatTimeout ? "expired"
        : "up";
    return new ServiceView(
        instance.Id, instance.Name, instance.Address, status,
        instance.RegisteredAt, instance.LastHeartbeat,
        (long)(now - instance.LastHeartbeat).TotalMilliseconds,
        instance.HeartbeatCount,
        instance.Metadata);
}

app.MapGet("/services", (string? name) =>
{
    foreach (var stale in services.Values.Where(x => DateTimeOffset.UtcNow - x.LastHeartbeat > purgeAfter))
        services.TryRemove(stale.Id, out _);

    var result = services.Values
        .Where(x => name is null || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Select(ToView);
    return Results.Ok(result);
});

app.MapGet("/services/{id}", (string id) =>
    services.TryGetValue(id, out var instance) ? Results.Ok(ToView(instance)) : Results.NotFound());

app.MapPost("/services", (RegistrationRequest request, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) ||
        string.IsNullOrWhiteSpace(request.Name) ||
        !Uri.TryCreate(request.Address, UriKind.Absolute, out _))
    {
        return Results.BadRequest("Id, Name and an absolute Address are required.");
    }

    var now = DateTimeOffset.UtcNow;
    var metadata = request.Metadata ?? new Dictionary<string, string>();
    var instance = services.AddOrUpdate(request.Id,
        _ => new ServiceInstance(request.Id, request.Name, request.Address.TrimEnd('/'), now) { Metadata = metadata },
        (_, existing) => existing with
        {
            Name = request.Name,
            Metadata = metadata,
            Address = request.Address.TrimEnd('/'),
            RegisteredAt = now,
            LastHeartbeat = now,
            Deregistered = false
        });
    logger.LogInformation("Registered {Id} ({Name}) at {Address}.", instance.Id, instance.Name, instance.Address);
    return Results.Created($"/services/{instance.Id}", ToView(instance));
});

app.MapPut("/services/{id}/heartbeat", (string id) =>
{
    while (services.TryGetValue(id, out var existing))
    {
        if (existing.Deregistered) return Results.NotFound();
        var updated = existing with
        {
            LastHeartbeat = DateTimeOffset.UtcNow,
            HeartbeatCount = existing.HeartbeatCount + 1
        };
        if (services.TryUpdate(id, updated, existing)) return Results.Ok(ToView(updated));
    }
    return Results.NotFound();
});

app.MapDelete("/services/{id}", (string id, bool? permanent, ILogger<Program> logger) =>
{
    if (permanent == true)
    {
        if (!services.TryRemove(id, out _)) return Results.NotFound();
        logger.LogInformation("Removed {Id} permanently.", id);
        return Results.NoContent();
    }

    while (services.TryGetValue(id, out var existing))
    {
        if (services.TryUpdate(id, existing with { Deregistered = true }, existing))
        {
            logger.LogInformation("Deregistered {Id}.", id);
            return Results.NoContent();
        }
    }
    return Results.NotFound();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", registered = services.Count }));

app.Run();

record RegistrationRequest(string Id, string Name, string Address, Dictionary<string, string>? Metadata);

record ServiceInstance(string Id, string Name, string Address, DateTimeOffset RegisteredAt)
{
    public DateTimeOffset LastHeartbeat { get; init; } = RegisteredAt;
    public long HeartbeatCount { get; init; }
    public bool Deregistered { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

record ServiceView(
    string Id, string Name, string Address, string Status,
    DateTimeOffset RegisteredAt, DateTimeOffset LastHeartbeat,
    long MillisecondsSinceHeartbeat, long HeartbeatCount,
    Dictionary<string, string> Metadata);
