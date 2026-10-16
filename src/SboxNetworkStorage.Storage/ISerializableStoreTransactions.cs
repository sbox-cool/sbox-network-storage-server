namespace SboxNetworkStorage.Storage;

/// <summary>Opt-in serializable transactions; ordinary store transactions retain their existing isolation.</summary>
public interface ISerializableStoreTransactions
{
    Task<IStoreTransaction> BeginSerializableTransactionAsync(CancellationToken ct);
    bool IsSerializationConflict(Exception exception);
}
