namespace BookCatalog.Api.Services;

public sealed class SyncVersionService
{
    private long _lastVersion = DateTime.UtcNow.Ticks;

    public Task<long> NextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var previous = Interlocked.Read(ref _lastVersion);
            var next = Math.Max(DateTime.UtcNow.Ticks, previous + 1);
            if (Interlocked.CompareExchange(ref _lastVersion, next, previous) == previous)
                return Task.FromResult(next);
        }
    }
}
