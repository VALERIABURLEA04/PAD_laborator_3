using BookCatalog.Api.Models;
using BookCatalog.Api.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("api/shards")]
[Produces("application/json")]
public sealed class ShardsController(ShardRouter router) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await Task.WhenAll(router.Shards.Select(shard => DescribeAsync(shard, cancellationToken)));
        return Ok(result);
    }

    private static async Task<object> DescribeAsync(Shard shard, CancellationToken cancellationToken)
    {
        var range = new
        {
            from = $"0x{shard.RangeStart:X8}",
            to = $"0x{shard.RangeEnd - 1:X8}",
            percent = Math.Round(100.0 * (shard.RangeEnd - shard.RangeStart) / (1UL << 32), 1)
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var count = await shard.Books.CountDocumentsAsync(FilterDefinition<Book>.Empty, cancellationToken: timeout.Token);
            var hello = await shard.Client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: timeout.Token);
            return new
            {
                name = shard.Name,
                database = shard.Database,
                range,
                books = count,
                status = "up",
                replicaSet = hello.GetValue("setName", "").AsString,
                primary = hello.GetValue("primary", "").AsString,
                members = hello.TryGetValue("hosts", out var hosts) ? hosts.AsBsonArray.Select(h => h.AsString) : Enumerable.Empty<string>()
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new { name = shard.Name, database = shard.Database, range, books = -1L, status = "down", error = exception.Message };
        }
    }
}
