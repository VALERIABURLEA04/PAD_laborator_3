using System.Text.Json;
using BookCatalog.Api.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace BookCatalog.Api.Services;

public sealed record BookCacheOptions(TimeSpan TimeToLive);

public sealed class BookCache(IDistributedCache cache, BookCacheOptions options)
{
    private static string Key(string id) => $"books:{id}";

    public async Task<Book?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var json = await cache.GetStringAsync(Key(id), cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<Book>(json);
    }

    public Task SetAsync(Book book, CancellationToken cancellationToken) =>
        cache.SetStringAsync(
            Key(book.Id),
            JsonSerializer.Serialize(book),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = options.TimeToLive
            },
            cancellationToken);

    public Task RemoveAsync(string id, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(id), cancellationToken);
}
