using BookCatalog.Api.Models;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Driver;

namespace BookCatalog.Api.Services;

public sealed class BookSyncWorker(
    IMongoClient mongo,
    IDistributedCache cache,
    IConfiguration configuration,
    ILogger<BookSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var databaseNames = new[]
        {
            configuration["Sync:Database1"],
            configuration["Sync:Database2"],
            configuration["Sync:Database3"]
        };
        if (databaseNames.Any(string.IsNullOrWhiteSpace) ||
            databaseNames.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new InvalidOperationException("Three distinct Sync:Database names are required.");
        }

        var connectionStrings = new[]
        {
            configuration["Sync:Connection1"],
            configuration["Sync:Connection2"],
            configuration["Sync:Connection3"]
        };
        if (connectionStrings.Any(value => !string.IsNullOrWhiteSpace(value)) &&
            connectionStrings.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "All three Sync:Connection values are required when using separate replica sets.");
        }

        IMongoClient[] clients = connectionStrings.All(string.IsNullOrWhiteSpace)
            ? [mongo, mongo, mongo]
            : connectionStrings.Select(value => (IMongoClient)new MongoClient(value!)).ToArray();
        var collections = databaseNames
            .Select((name, index) => clients[index].GetDatabase(name!)
                .GetCollection<Book>("books"))
            .ToArray();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(collections, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Book synchronization failed; retrying.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ReconcileAsync(
        IMongoCollection<Book>[] collections,
        CancellationToken cancellationToken)
    {
        var snapshots = await Task.WhenAll(collections.Select(collection =>
            collection.Find(FilterDefinition<Book>.Empty).ToListAsync(cancellationToken)));

        var latest = new Dictionary<string, Book>(StringComparer.Ordinal);
        foreach (var books in snapshots)
        {
            foreach (var book in books)
            {
                if (!latest.TryGetValue(book.Id, out var current) ||
                    CompareVersion(book, current) > 0)
                {
                    latest[book.Id] = book;
                }
            }
        }

        for (var index = 0; index < collections.Length; index++)
        {
            var localBooks = snapshots[index].ToDictionary(book => book.Id, StringComparer.Ordinal);
            foreach (var winner in latest.Values)
            {
                if (localBooks.TryGetValue(winner.Id, out var local) &&
                    CompareVersion(local, winner) >= 0)
                {
                    continue;
                }

                if (local is null)
                {
                    try
                    {
                        await collections[index].InsertOneAsync(
                            winner, cancellationToken: cancellationToken);
                    }
                    catch (MongoWriteException exception) when (
                        exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
                    {
                        // A concurrent write arrived after the snapshot; retry next pass.
                        continue;
                    }
                }
                else
                {
                    var filter = Builders<Book>.Filter.And(
                        Builders<Book>.Filter.Eq(book => book.Id, winner.Id),
                        Builders<Book>.Filter.Or(
                            Builders<Book>.Filter.Lt(book => book.SyncVersion, winner.SyncVersion),
                            Builders<Book>.Filter.Exists(book => book.SyncVersion, false),
                            Builders<Book>.Filter.And(
                                Builders<Book>.Filter.Eq(book => book.SyncVersion, winner.SyncVersion),
                                Builders<Book>.Filter.Lt(book => book.SyncOrigin, winner.SyncOrigin))));
                    var result = await collections[index].ReplaceOneAsync(
                        filter, winner, cancellationToken: cancellationToken);
                    if (result.MatchedCount == 0) continue;
                }
                var databaseName = collections[index].CollectionNamespace
                    .DatabaseNamespace.DatabaseName;
                await cache.RemoveAsync(
                    $"books:{databaseName}:{winner.Id}", cancellationToken);
                logger.LogInformation(
                    "Synchronized book {BookId} to {DatabaseName} at version {Version}.",
                    winner.Id, databaseName,
                    winner.SyncVersion);
            }
        }
    }

    private static int CompareVersion(Book left, Book right)
    {
        var version = left.SyncVersion.CompareTo(right.SyncVersion);
        return version != 0 ? version :
            StringComparer.Ordinal.Compare(left.SyncOrigin, right.SyncOrigin);
    }
}
