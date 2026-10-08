namespace SboxNetworkStorage.Storage;

/// <summary>Atomic restoration of a portable project, distinct from ordinary unconditional upserts.</summary>
public interface IProjectImportStore
{
    /// <summary>
    /// Claims an absent project ID and runs all restoration reads and writes through the supplied store.
    /// Returns false without replay when the ID exists. Commits only after replay succeeds; exceptions
    /// and cancellation roll back the claim and every restored row, membership and workspace object.
    /// The callback must await its work and must not retain the supplied store or start concurrent work.
    /// </summary>
    Task<bool> TryImportProjectAsync(string projectId,
        Func<INetworkStorageStore, CancellationToken, Task> restore, CancellationToken ct);
}
