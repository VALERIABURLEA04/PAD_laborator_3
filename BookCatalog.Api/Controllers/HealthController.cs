using BookCatalog.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("health")]
[Produces("application/json")]
public sealed class HealthController(
    IMongoClient mongo,
    IDistributedCache cache,
    BookSyncOptions syncOptions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            var mongoPing = mongo.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            var redisPing = cache.GetAsync("health:probe", cancellationToken);
            await Task.WhenAll(mongoPing, redisPing);

            var instance = syncOptions.InstanceId;
            return Ok(new { status = "healthy", instance });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unhealthy" });
        }
    }
}
