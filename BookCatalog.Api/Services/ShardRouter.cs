using System.Text;
using BookCatalog.Api.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BookCatalog.Api.Services;

public sealed class ShardSettings
{
    public string Name { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
}

public sealed class Shard(int index, string name, string database, IMongoClient client, ulong rangeStart, ulong rangeEnd)
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public ulong RangeStart { get; } = rangeStart;
    public ulong RangeEnd { get; } = rangeEnd;
    public IMongoCollection<Book> Books { get; } = client.GetDatabase(database).GetCollection<Book>("books");
}

/// <summary>
/// Hashed range sharding, the same idea as MongoDB hashed sharding: the book id is hashed into
/// (FNV-1a + MurmurHash3 finalizer) into the space [0, 2^32) and that space is cut into equal consecutive intervals, one per shard.
/// Hashing spreads new ids evenly; a plain range on ObjectId would send every new book to the
/// last shard because ObjectIds start with a timestamp.
/// </summary>
public sealed class ShardRouter
{
    private const ulong HashSpace = 1UL << 32;

    public IReadOnlyList<Shard> Shards { get; }

    public ShardRouter(IReadOnlyList<ShardSettings> settings)
    {
        if (settings.Count == 0) throw new InvalidOperationException("At least one shard is required.");

        var size = HashSpace / (ulong)settings.Count;
        Shards = settings.Select((shard, index) => new Shard(
            index,
            shard.Name,
            shard.Database,
            CreateClient(shard.ConnectionString),
            (ulong)index * size,
            index == settings.Count - 1 ? HashSpace : (ulong)(index + 1) * size)).ToList();
    }

    private static MongoClient CreateClient(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
        return new MongoClient(settings);
    }

    public static uint Hash(string id)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.ASCII.GetBytes(id.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        // MurmurHash3 finalizer: FNV alone barely changes the high bits for ids that differ only at
        // the end (sequential ObjectIds), and the high bits are exactly what picks the shard.
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        hash ^= hash >> 16;
        return hash;
    }

    public Shard For(string id)
    {
        var hash = Hash(id);
        return Shards.First(s => hash >= s.RangeStart && hash < s.RangeEnd);
    }

    public static string NewId() => ObjectId.GenerateNewId().ToString();
}
