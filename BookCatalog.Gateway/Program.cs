using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BookCatalog.Gateway.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new BalancingSettings(
    Enum.TryParse<BalancingMode>(options.BalancingMode, ignoreCase: true, out var mode) ? mode : BalancingMode.RoundRobin));

var redisConfiguration = ConfigurationOptions.Parse(
    builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379");
redisConfiguration.AbortOnConnectFail = false;
redisConfiguration.ConnectTimeout = 3000;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));

builder.Services.AddHttpClient("control", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("upstream", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    });
builder.Services.AddHttpClient("load-test", client => client.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddSingleton<BackendPool>();
builder.Services.AddSingleton<DiscoveryState>();
builder.Services.AddSingleton<PriorityGate>();
builder.Services.AddSingleton<ResponseCache>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<ProxyHandler>();
builder.Services.AddSingleton<DockerControl>();
builder.Services.AddSingleton<LoadTester>();
builder.Services.AddHostedService<DiscoveryPoller>();
builder.Services.AddHostedService<HealthChecker>();

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

var app = builder.Build();

app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/dashboard" });
app.UseStaticFiles(new StaticFileOptions { RequestPath = "/dashboard" });
app.UseRouting();

app.MapGet("/", () => Results.Redirect("/dashboard/"));
// Answered here so probes and browsers never reach an API and silently reset its circuit breaker.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", component = "gateway" }));
app.MapGet("/favicon.ico", () => Results.NoContent());

var gateway = app.MapGroup("/gateway");

gateway.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

gateway.MapGet("/stats", async (
    BackendPool pool,
    DiscoveryState discovery,
    PriorityGate gate,
    ResponseCache cache,
    GatewayMetrics metrics,
    BalancingSettings balancing) => Results.Ok(new
{
    time = DateTimeOffset.UtcNow,
    balancing = balancing.Mode,
    discovery = new { url = options.DiscoveryUrl, discovery.Reachable, discovery.LastPoll, discovery.LastError },
    backends = pool.All.Select(ToView),
    queue = gate.Snapshot(),
    cache = await cache.SnapshotAsync(),
    metrics = metrics.Snapshot()
}));

gateway.MapGet("/backends", (BackendPool pool) => Results.Ok(pool.All.Select(ToView)));

gateway.MapGet("/databases", async (BackendPool pool, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var http = httpClientFactory.CreateClient("control");
    var results = await Task.WhenAll(pool.All.Select(async backend =>
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var info = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"{backend.Address}/api/database", timeout.Token);
            return (object)new { backend = backend.Id, status = "up", info };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (object)new { backend = backend.Id, status = "down", error = exception.Message };
        }
    }));
    return Results.Ok(results);
});

gateway.MapPut("/backends/{id}/circuit", (string id, BackendPool pool) =>
{
    var backend = pool.Find(id);
    if (backend is null) return Results.NotFound();
    backend.Circuit.Reset();
    return Results.Ok(ToView(backend));
});

gateway.MapPut("/backends/{id}/fault", async (string id, FaultToggle toggle, BackendPool pool,
    IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var backend = pool.Find(id);
    if (backend is null) return Results.NotFound();
    try
    {
        var http = httpClientFactory.CreateClient("control");
        using var response = await http.PutAsJsonAsync($"{backend.Address}/api/fault",
            new { enabled = toggle.Enabled }, cancellationToken);
        response.EnsureSuccessStatusCode();
        backend.FaultSimulated = toggle.Enabled;
        return Results.Ok(ToView(backend));
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem($"{id} did not accept the change: {exception.Message}",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

gateway.MapPut("/balancing", (BalancingUpdate update, BalancingSettings balancing) =>
{
    if (!Enum.TryParse<BalancingMode>(update.Mode, ignoreCase: true, out var newMode))
        return Results.BadRequest("Mode must be 'RoundRobin' or 'Sticky'.");
    balancing.Mode = newMode;
    return Results.Ok(new { mode = balancing.Mode });
});

gateway.MapDelete("/cache", async (ResponseCache cache) =>
    Results.Ok(new { deleted = await cache.ClearAsync() }));

gateway.MapDelete("/statistics", (GatewayMetrics metrics, ResponseCache cache, PriorityGate gate) =>
{
    metrics.Reset();
    cache.ResetStatistics();
    gate.ResetStatistics();
    return Results.NoContent();
});

gateway.MapGet("/containers", async (DockerControl docker, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await docker.ListAsync(cancellationToken));
    }
    catch (DockerUnavailableException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

gateway.MapPut("/containers/{service}/state", async (
    string service, ContainerStateUpdate update, DockerControl docker, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await docker.SetStateAsync(service, update.State, cancellationToken));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status404NotFound);
    }
    catch (ArgumentException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (DockerUnavailableException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

gateway.MapPost("/load-tests", async (LoadTestRequest? request, LoadTester tester, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await tester.RunAsync(request ?? new LoadTestRequest(), cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

var proxy = app.Services.GetRequiredService<ProxyHandler>();
app.MapFallback("{**path}", (RequestDelegate)proxy.HandleAsync);

app.Run();

static object ToView(Backend backend) => new
{
    id = backend.Id,
    address = backend.Address,
    health = backend.Healthy ? "UP" : "DOWN",
    registry = backend.RegistryStatus.ToUpperInvariant(),
    routable = backend.IsRoutable,
    circuit = backend.Circuit.State,
    consecutiveFailures = backend.Circuit.ConsecutiveFailures,
    circuitOpenedTimes = backend.Circuit.TimesOpened,
    circuitOpenUntil = backend.Circuit.OpenUntil,
    requests = backend.Requests,
    failures = backend.Failures,
    lastLatencyMs = Math.Round(backend.LastLatencyMs, 1),
    lastHealthCheck = backend.LastHealthCheck,
    lastHealthCheckMs = Math.Round(backend.LastHealthCheckMs, 1),
    lastHealthError = backend.LastHealthError,
    millisecondsSinceHeartbeat = backend.MillisecondsSinceHeartbeat,
    heartbeatCount = backend.HeartbeatCount,
    faultSimulated = backend.FaultSimulated
};

record BalancingUpdate(string Mode);

record ContainerStateUpdate(string State);

record FaultToggle(bool Enabled);
