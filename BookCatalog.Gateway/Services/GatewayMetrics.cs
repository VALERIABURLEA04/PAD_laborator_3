namespace BookCatalog.Gateway.Services;

public sealed record RequestRecord(
    DateTimeOffset Time,
    string Method,
    string Path,
    int StatusCode,
    string Backend,
    double LatencyMs,
    string Cache,
    string Priority,
    int Attempts,
    bool Sticky);

public sealed class GatewayMetrics
{
    private const int WindowSize = 2048;
    private const int RecentSize = 40;

    private readonly object _lock = new();
    private readonly double[] _latencies = new double[WindowSize];
    private readonly LinkedList<RequestRecord> _recent = new();
    private readonly Queue<DateTimeOffset> _lastSeconds = new();
    private int _count;
    private int _next;
    private long _total;
    private long _errors;
    private long _retries;
    private long _rejected;

    public void Record(RequestRecord record)
    {
        lock (_lock)
        {
            _latencies[_next] = record.LatencyMs;
            _next = (_next + 1) % WindowSize;
            _count = Math.Min(_count + 1, WindowSize);

            _total++;
            if (record.StatusCode >= 500) _errors++;
            if (record.Attempts > 1) _retries += record.Attempts - 1;
            if (record.StatusCode == StatusCodes.Status503ServiceUnavailable && record.Backend == "-") _rejected++;

            _recent.AddFirst(record);
            if (_recent.Count > RecentSize) _recent.RemoveLast();

            _lastSeconds.Enqueue(record.Time);
            TrimRate(record.Time);
        }
    }

    private void TrimRate(DateTimeOffset now)
    {
        while (_lastSeconds.Count > 0 && now - _lastSeconds.Peek() > TimeSpan.FromSeconds(10))
            _lastSeconds.Dequeue();
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            var sorted = _latencies.Take(_count).Order().ToArray();
            double Percentile(double p) => sorted.Length == 0
                ? 0
                : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)], 1);

            TrimRate(DateTimeOffset.UtcNow);
            return new
            {
                latency = new
                {
                    p50 = Percentile(0.50),
                    p95 = Percentile(0.95),
                    p99 = Percentile(0.99),
                    average = sorted.Length == 0 ? 0 : Math.Round(sorted.Average(), 1),
                    samples = sorted.Length
                },
                totals = new
                {
                    requests = _total,
                    errors = _errors,
                    retries = _retries,
                    rejected = _rejected,
                    requestsPerSecond = Math.Round(_lastSeconds.Count / 10.0, 1)
                },
                recent = _recent.ToList()
            };
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_latencies);
            _recent.Clear();
            _lastSeconds.Clear();
            _count = _next = 0;
            _total = _errors = _retries = _rejected = 0;
        }
    }
}
