using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("health")]
[Produces("application/json")]
public sealed class HealthController(ShardRouter router, IDistributedCache cache, InstanceInfo instance) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var shards = await Task.WhenAll(router.Shards.Select(async shard =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
                await shard.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1), cancellationToken: timeout.Token);
                return (shard.Name, Up: true);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return (shard.Name, Up: false);
            }
        }));

        bool redisUp;
        try
        {
            await cache.GetAsync("health:probe", cancellationToken);
            redisUp = true;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            redisUp = false;
        }

        var body = new
        {
            status = redisUp && shards.Any(s => s.Up) ? "healthy" : "unhealthy",
            instance = instance.Id,
            redis = redisUp ? "up" : "down",
            shards = shards.ToDictionary(s => s.Name, s => s.Up ? "up" : "down")
        };
        return body.status == "healthy" ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
