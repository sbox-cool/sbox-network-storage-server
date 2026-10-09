namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>A transaction for fake stores that apply writes immediately: its store is the fake itself.</summary>
public sealed class PassThroughStoreTransaction(INetworkStorageStore store) : IStoreTransaction
{
    public INetworkStorageStore Store => store;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
