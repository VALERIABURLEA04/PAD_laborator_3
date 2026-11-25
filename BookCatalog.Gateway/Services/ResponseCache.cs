using System.Text.Json;
using StackExchange.Redis;

namespace BookCatalog.Gateway.Services;

public sealed record CachedResponse(int StatusCode, string? ContentType, byte[] Body, string? Instance);

public sealed class ResponseCache(
    IConnectionMultiplexer redis,
    GatewayOptions options,
    ILogger<ResponseCache> logger)
{
    private const string Prefix = "gateway:cache:";
    private long _hits;
    private long _misses;
    private long _bypassed;
    private long _invalidations;

    public TimeSpan TimeToLive { get; } = TimeSpan.FromSeconds(Math.Max(1, options.CacheTtlSeconds));

    public static bool IsCacheable(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Path.StartsWithSegments("/api/books", StringComparison.OrdinalIgnoreCase);

    public static bool InvalidatesBooks(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) &&
        request.Path.StartsWithSegments("/api/books", StringComparison.OrdinalIgnoreCase);

    public static bool ClientBypasses(HttpRequest request) =>
        request.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase) ||
        request.Headers.Pragma.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);

    public static string KeyFor(HttpRequest request)
    {
        var format = request.Query["format"].ToString();
        var xml = format.Equals("xml", StringComparison.OrdinalIgnoreCase) ||
                  (string.IsNullOrEmpty(format) &&
                   request.Headers.Accept.ToString().Contains("xml", StringComparison.OrdinalIgnoreCase));
        return $"{Prefix}{(xml ? "xml" : "json")}:{request.Path.Value?.ToLowerInvariant()}{request.QueryString}";
    }

    public async Task<(CachedResponse? Response, TimeSpan? Remaining)> GetAsync(string key)
    {
        try
        {
            var entry = await redis.GetDatabase().StringGetWithExpiryAsync(key);
            if (entry.Value.IsNullOrEmpty)
            {
                Interlocked.Increment(ref _misses);
                return (null, null);
            }

            Interlocked.Increment(ref _hits);
            return (JsonSerializer.Deserialize<CachedResponse>(entry.Value.ToString()), entry.Expiry);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _misses);
            logger.LogWarning("Redis read failed: {Message}", exception.Message);
            return (null, null);
        }
    }

    public void CountBypass() => Interlocked.Increment(ref _bypassed);

    public async Task SetAsync(string key, CachedResponse response)
    {
        try
        {
            await redis.GetDatabase().ExecuteAsync(
                "SET", key, JsonSerializer.Serialize(response), "EX", (int)TimeToLive.TotalSeconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Redis write failed: {Message}", exception.Message);
        }
    }

    public async Task<int> InvalidateBooksAsync() => await DeleteAsync($"{Prefix}*:/api/books*");

    public async Task<int> ClearAsync() => await DeleteAsync($"{Prefix}*");

    private async Task<int> DeleteAsync(string pattern)
    {
        var deleted = 0;
        try
        {
            var database = redis.GetDatabase();
            foreach (var endpoint in redis.GetEndPoints())
            {
                var server = redis.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;
                await foreach (var key in server.KeysAsync(pattern: pattern))
                {
                    if (await database.KeyDeleteAsync(key)) deleted++;
                }
            }
            Interlocked.Increment(ref _invalidations);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Redis invalidation failed: {Message}", exception.Message);
        }
        return deleted;
    }

    public async Task<object> SnapshotAsync()
    {
        long keys = 0;
        var connected = redis.IsConnected;
        try
        {
            foreach (var endpoint in redis.GetEndPoints())
            {
                var server = redis.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;
                await foreach (var _ in server.KeysAsync(pattern: $"{Prefix}*")) keys++;
            }
        }
        catch
        {
            connected = false;
        }

        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        return new
        {
            connected,
            hits,
            misses,
            bypassed = Interlocked.Read(ref _bypassed),
            invalidations = Interlocked.Read(ref _invalidations),
            hitRatio = hits + misses == 0 ? 0 : Math.Round(100.0 * hits / (hits + misses), 1),
            keys,
            ttlSeconds = (int)TimeToLive.TotalSeconds
        };
    }

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _bypassed, 0);
        Interlocked.Exchange(ref _invalidations, 0);
    }
}
