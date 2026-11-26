namespace BookCatalog.Api.Services;

public sealed class FaultState
{
    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
