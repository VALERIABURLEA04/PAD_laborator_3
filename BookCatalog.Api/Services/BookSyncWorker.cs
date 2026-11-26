using System.Net.Http.Json;
using BookCatalog.Api.Models;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Driver;

namespace BookCatalog.Api.Services;

public sealed class BookSyncWorker(
    IDistributedCache cache,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<BookSyncWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IMongoClient> _clients = new(StringComparer.Ordinal);
    private HashSet<string> _lastTargets = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var discoveryUrl = configuration["Sync:DiscoveryUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Sync:DiscoveryUrl is required.");
        var serviceName = configuration["Sync:ServiceName"] ?? "bookcatalog-api";
        var http = httpClientFactory.CreateClient("sync-discovery");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var targets = await DiscoverDatabasesAsync(http, discoveryUrl, serviceName, stoppingToken);
                if (targets.Count > 1)
                    await ReconcileAsync(targets, stoppingToken);
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

    // Every API registers its own database in Service Discovery, so a new API joins the
    // synchronization automatically; one that stops sending heartbeats is skipped until it returns.
    private async Task<List<IMongoCollection<Book>>> DiscoverDatabasesAsync(
        HttpClient http, string discoveryUrl, string serviceName, CancellationToken cancellationToken)
    {
        var instances = await http.GetFromJsonAsync<List<DiscoveredInstance>>(
            $"{discoveryUrl}/services?name={Uri.EscapeDataString(serviceName)}", cancellationToken) ?? [];

        var collections = new List<IMongoCollection<Book>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (!string.Equals(instance.Status, "up", StringComparison.OrdinalIgnoreCase) ||
                instance.Metadata is null ||
                !instance.Metadata.TryGetValue("mongo", out var connection) ||
                !instance.Metadata.TryGetValue("database", out var database) ||
                string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(database))
            {
                continue;
            }

            if (!seen.Add($"{connection}|{database}")) continue;
            if (!_clients.TryGetValue(connection, out var client))
            {
                client = new MongoClient(connection);
                _clients[connection] = client;
            }
            collections.Add(client.GetDatabase(database).GetCollection<Book>("books"));
        }

        if (!seen.SetEquals(_lastTargets))
        {
            logger.LogInformation("Synchronizing {Count} databases: {Databases}.", collections.Count,
                string.Join(", ", collections.Select(c => c.CollectionNamespace.DatabaseNamespace.DatabaseName)));
            _lastTargets = seen;
        }
        return collections;
    }

    private async Task ReconcileAsync(
        List<IMongoCollection<Book>> targets,
        CancellationToken cancellationToken)
    {
        // A database that is down this round is skipped; it catches up on a later pass.
        var reads = await Task.WhenAll(targets.Select(collection => ReadAsync(collection, cancellationToken)));
        var available = reads.Where(r => r.Books is not null).ToArray();
        if (available.Length < 2) return;
        var collections = available.Select(r => r.Collection).ToArray();
        var snapshots = available.Select(r => r.Books!).ToArray();

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

    private async Task<(IMongoCollection<Book> Collection, List<Book>? Books)> ReadAsync(
        IMongoCollection<Book> collection, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return (collection, await collection.Find(FilterDefinition<Book>.Empty).ToListAsync(timeout.Token));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Skipping {Database}: {Message}",
                collection.CollectionNamespace.DatabaseNamespace.DatabaseName, exception.Message);
            return (collection, null);
        }
    }

    private static int CompareVersion(Book left, Book right)
    {
        var version = left.SyncVersion.CompareTo(right.SyncVersion);
        return version != 0 ? version :
            StringComparer.Ordinal.Compare(left.SyncOrigin, right.SyncOrigin);
    }
}

internal sealed record DiscoveredInstance(string Id, string Status, Dictionary<string, string>? Metadata);
