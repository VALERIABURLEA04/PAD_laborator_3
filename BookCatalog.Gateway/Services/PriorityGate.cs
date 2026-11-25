using System.Diagnostics;

namespace BookCatalog.Gateway.Services;

public enum RequestPriority
{
    High = 0,
    Normal = 1,
    Low = 2
}

/// <summary>
/// Limits how many requests are forwarded to the APIs at the same time. Requests above the limit
/// wait in three FIFO queues; when a slot frees up it is handed directly to the oldest waiter of the
/// highest non-empty priority, so HIGH is always served before NORMAL and NORMAL before LOW.
/// </summary>
public sealed class PriorityGate(GatewayOptions options)
{
    private const int Levels = 3;

    private readonly object _lock = new();
    private readonly LinkedList<Waiter>[] _queues = { new(), new(), new() };
    private readonly long[] _processed = new long[Levels];
    private readonly double[] _totalWaitMs = new double[Levels];
    private readonly int[] _peakWaiting = new int[Levels];
    private int _active;

    public int MaxConcurrency { get; } = Math.Max(1, options.MaxConcurrency);

    public async Task<IDisposable> EnterAsync(RequestPriority priority, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (_active < MaxConcurrency && _queues.All(q => q.Count == 0))
            {
                _active++;
                RecordGrant(priority, 0);
                return new Lease(this);
            }

            waiter = new Waiter(priority, Stopwatch.GetTimestamp());
            waiter.Node = _queues[(int)priority].AddLast(waiter);
            var level = (int)priority;
            _peakWaiting[level] = Math.Max(_peakWaiting[level], _queues[level].Count);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                if (waiter.Node?.List is not null) _queues[(int)priority].Remove(waiter.Node);
            }
            waiter.Completion.TrySetCanceled(cancellationToken);
        });

        return await waiter.Completion.Task;
    }

    private void Release()
    {
        while (true)
        {
            Waiter? next = null;
            lock (_lock)
            {
                foreach (var queue in _queues)
                {
                    if (queue.First is null) continue;
                    next = queue.First.Value;
                    queue.RemoveFirst();
                    break;
                }

                if (next is null)
                {
                    _active--;
                    return;
                }

                RecordGrant(next.Priority, Stopwatch.GetElapsedTime(next.EnqueuedAt).TotalMilliseconds);
            }

            // The waiter may have been cancelled between dequeue and hand-over; then the slot goes to the next one.
            if (next.Completion.TrySetResult(new Lease(this))) return;
        }
    }

    private void RecordGrant(RequestPriority priority, double waitMs)
    {
        var level = (int)priority;
        _processed[level]++;
        _totalWaitMs[level] += waitMs;
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            object Level(RequestPriority priority)
            {
                var level = (int)priority;
                return new
                {
                    waiting = _queues[level].Count,
                    processed = _processed[level],
                    averageWaitMs = _processed[level] == 0 ? 0 : Math.Round(_totalWaitMs[level] / _processed[level], 2),
                    peakWaiting = _peakWaiting[level]
                };
            }

            return new
            {
                active = _active,
                maxConcurrency = MaxConcurrency,
                high = Level(RequestPriority.High),
                normal = Level(RequestPriority.Normal),
                low = Level(RequestPriority.Low)
            };
        }
    }

    public void ResetStatistics()
    {
        lock (_lock)
        {
            Array.Clear(_processed);
            Array.Clear(_totalWaitMs);
            Array.Clear(_peakWaiting);
        }
    }

    public static RequestPriority Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" or "h" or "1" => RequestPriority.High,
        "low" or "l" or "3" => RequestPriority.Low,
        _ => RequestPriority.Normal
    };

    private sealed class Waiter(RequestPriority priority, long enqueuedAt)
    {
        public RequestPriority Priority { get; } = priority;
        public long EnqueuedAt { get; } = enqueuedAt;
        public LinkedListNode<Waiter>? Node { get; set; }
        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(PriorityGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
