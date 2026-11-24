using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(IMongoClient mongo, IDistributedCache cache) : ControllerBase
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

            var instance = Environment.MachineName;
            Response.Headers["X-Api-Instance"] = instance;
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
