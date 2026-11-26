namespace BookCatalog.Gateway.Services;

public sealed record ContainerInfo(string Service, string ContainerId, string State, string Status, bool Protected);

public sealed class ContainerControlUnavailableException(string message) : Exception(message);

public interface IContainerControl
{
    Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken);

    Task<ContainerInfo> SetStateAsync(string service, string desiredState, CancellationToken cancellationToken);
}
