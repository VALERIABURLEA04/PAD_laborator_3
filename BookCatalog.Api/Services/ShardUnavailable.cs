using MongoDB.Driver;

namespace BookCatalog.Api.Services;

public static class ShardUnavailable
{
    public static bool Matches(Exception exception) =>
        exception is TimeoutException or MongoConnectionException or MongoNotPrimaryException ||
        exception.InnerException is TimeoutException or MongoConnectionException;

    /// <summary>
    /// A shard that cannot be reached is a data-tier outage, not a broken API instance. The response
    /// is marked with X-Shard-Unavailable so the gateway neither retries it on another instance nor
    /// counts it against the instance's circuit breaker.
    /// </summary>
    public static async Task HandleAsync(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception exception) when (Matches(exception) && !context.Response.HasStarted)
        {
            var shard = context.Response.Headers["X-Shard"].ToString();
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["X-Shard-Unavailable"] = string.IsNullOrEmpty(shard) ? "unknown" : shard;
            await context.Response.WriteAsJsonAsync(new
            {
                status = 503,
                error = string.IsNullOrEmpty(shard) ? "No shard is reachable." : $"Shard {shard} is unavailable.",
                shard
            });
        }
    }
}
