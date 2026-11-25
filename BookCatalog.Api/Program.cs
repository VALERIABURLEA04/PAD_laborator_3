using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options => options.ReturnHttpNotAcceptable = true)
    .AddXmlSerializerFormatters();
builder.Services.AddOpenApi();

var instanceId = builder.Configuration["Sync:InstanceId"] ?? Environment.MachineName;
builder.Services.AddSingleton(new BookSyncOptions(
    builder.Configuration.GetValue<bool>("Sync:Enabled"),
    instanceId));

var redisConnectionString = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString is required.");
var cacheTtlSeconds = builder.Configuration.GetValue<int>("Redis:CacheTtlSeconds");
if (cacheTtlSeconds <= 0)
    throw new InvalidOperationException("Redis:CacheTtlSeconds must be positive.");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
    options.InstanceName = "BookCatalog:";
});
builder.Services.AddSingleton(new BookCacheOptions(TimeSpan.FromSeconds(cacheTtlSeconds)));
builder.Services.AddSingleton<BookCache>();

var connectionString = builder.Configuration["MongoDb:ConnectionString"]
    ?? throw new InvalidOperationException("MongoDb:ConnectionString is required.");
var databaseName = builder.Configuration["MongoDb:DatabaseName"]
    ?? throw new InvalidOperationException("MongoDb:DatabaseName is required.");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton<SyncVersionService>();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IMongoClient>()
        .GetDatabase(databaseName)
        .GetCollection<Book>("books"));
if (builder.Configuration.GetValue<bool>("Sync:WorkerEnabled"))
    builder.Services.AddHostedService<BookSyncWorker>();

var discoveryUrl = builder.Configuration["Discovery:Url"];
var discoveryAddress = builder.Configuration["Discovery:Address"];
if (!string.IsNullOrWhiteSpace(discoveryUrl) && !string.IsNullOrWhiteSpace(discoveryAddress))
{
    var discoveryOptions = new DiscoveryOptions(
        discoveryUrl.TrimEnd('/'),
        builder.Configuration["Discovery:ServiceName"] ?? "bookcatalog-api",
        instanceId,
        discoveryAddress.TrimEnd('/'),
        TimeSpan.FromSeconds(builder.Configuration.GetValue("Discovery:HeartbeatSeconds", 5)));
    builder.Services.AddHttpClient("discovery", client => client.Timeout = TimeSpan.FromSeconds(3));
    builder.Services.AddHostedService(serviceProvider => new DiscoveryRegistration(
        serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("discovery"),
        discoveryOptions,
        serviceProvider.GetRequiredService<ILogger<DiscoveryRegistration>>()));
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Api-Instance"] = instanceId;

    var format = context.Request.Query["format"].ToString();
    if (format.Equals("xml", StringComparison.OrdinalIgnoreCase))
        context.Request.Headers.Accept = "application/xml";
    else if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        context.Request.Headers.Accept = "application/json";

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
