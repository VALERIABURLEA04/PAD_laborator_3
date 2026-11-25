namespace BookCatalog.Gateway.Services;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// Closed: requests pass. After <c>threshold</c> consecutive failures the circuit opens and the
/// backend is skipped for <c>openDuration</c>. Then it becomes half-open and lets exactly one trial
/// request through: success closes the circuit, failure opens it again.
/// </summary>
public sealed class CircuitBreaker(int threshold, TimeSpan openDuration)
{
    private readonly object _lock = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private bool _trialInFlight;
    private long _timesOpened;

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open && DateTimeOffset.UtcNow - _openedAt >= openDuration)
                    return CircuitState.HalfOpen;
                return _state;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    public long TimesOpened
    {
        get { lock (_lock) return _timesOpened; }
    }

    public DateTimeOffset? OpenUntil
    {
        get
        {
            lock (_lock)
                return _state == CircuitState.Open ? _openedAt + openDuration : null;
        }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Closed) return true;

            if (_state == CircuitState.Open)
            {
                if (DateTimeOffset.UtcNow - _openedAt < openDuration) return false;
                _state = CircuitState.HalfOpen;
                _trialInFlight = false;
            }

            if (_trialInFlight) return false;
            _trialInFlight = true;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            _trialInFlight = false;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _trialInFlight = false;
            if (_state != CircuitState.HalfOpen && _consecutiveFailures < threshold) return;

            if (_state != CircuitState.Open) _timesOpened++;
            _state = CircuitState.Open;
            _openedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReleaseTrial()
    {
        lock (_lock) _trialInFlight = false;
    }

    public void Reset() => RecordSuccess();
}
