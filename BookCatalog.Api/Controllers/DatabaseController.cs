using BookCatalog.Api.Models;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Controllers;

[ApiController]
[Route("api/database")]
[Produces("application/json")]
public sealed class DatabaseController(
    IMongoClient mongo,
    IMongoCollection<Book> books,
    BookSyncOptions syncOptions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var active = await books.CountDocumentsAsync(x => !x.IsDeleted, cancellationToken: cancellationToken);
        var deleted = await books.CountDocumentsAsync(x => x.IsDeleted, cancellationToken: cancellationToken);
        var newest = await books.Find(FilterDefinition<Book>.Empty)
            .SortByDescending(x => x.SyncVersion)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);
        var hello = await mongo.GetDatabase("admin")
            .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);

        return Ok(new
        {
            instance = syncOptions.InstanceId,
            database = books.CollectionNamespace.DatabaseNamespace.DatabaseName,
            books = active,
            deletedMarkers = deleted,
            latestSyncVersion = newest?.SyncVersion ?? 0,
            replicaSet = hello.GetValue("setName", "").AsString,
            primary = hello.GetValue("primary", "").AsString,
            members = hello.TryGetValue("hosts", out var hosts)
                ? hosts.AsBsonArray.Select(h => h.AsString)
                : Enumerable.Empty<string>()
        });
    }
}
