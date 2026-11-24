using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton(new BookSyncOptions(
    builder.Configuration.GetValue<bool>("Sync:Enabled"),
    builder.Configuration["Sync:InstanceId"] ?? Environment.MachineName));

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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();
