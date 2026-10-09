using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>
/// An <see cref="IEndpointWriteTransaction"/> for fakes: writes and deletes are handed to the supplied delegates
/// as they arrive (a delegate may throw), <see cref="CommitAsync"/> runs <c>commit</c>, and disposing without a
/// commit runs <c>rollback</c>.
/// </summary>
public sealed class DelegateEndpointWriteTransaction(
    Action<string, string, string, IReadOnlyDictionary<string, object?>>? write = null,
    Action<string, string, string>? delete = null,
    Action? commit = null,
    Action? rollback = null) : IEndpointWriteTransaction
{
    private bool _committed;

    public Task WriteRecordAsync(string projectId, string collectionId, string key, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        write?.Invoke(projectId, collectionId, key, payload);
        return Task.CompletedTask;
    }

    public Task DeleteRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
    {
        delete?.Invoke(projectId, collectionId, key);
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct)
    {
        _committed = true;
        commit?.Invoke();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_committed) rollback?.Invoke();
        return ValueTask.CompletedTask;
    }
}
