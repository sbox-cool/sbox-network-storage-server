namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Abstraction over the data stores needed by <see cref="EndpointExecutor"/>
/// to run a deterministic endpoint definition natively. The Infrastructure layer
/// provides the real implementation (the store for definition + records);
/// Application-level tests inject a fake.
/// </summary>
public interface IEndpointDataSource
{
    /// <summary>
    /// Load the endpoint definition JSON (including <c>steps</c>, <c>response</c>,
    /// <c>let</c>, collections, etc.) for a given project + slug. Returns null if
    /// the endpoint is not found or the data source is unavailable.
    /// </summary>
    Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(
        string projectId, string endpointSlug, CancellationToken ct);

    /// <summary>
    /// List all collections for a project. Each collection row contains at least
    /// <c>collection_id</c> and <c>name</c>. Used to resolve collection names
    /// (as referenced in endpoint YAML) to the IDs the records table uses.
    /// </summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(
        string projectId, CancellationToken ct);

    /// <summary>
    /// Read a single record from a collection.  Returns null if the record is
    /// missing, deleted, or the collection does not exist.  Used both for
    /// pre-fetching and (on miss) at runtime by the sync executor via closure.
    /// </summary>
    Task<object?> ReadRecordAsync(
        string projectId, string collectionId, string key, CancellationToken ct);

    /// <summary>
    /// Read all non-deleted records in a collection. Returns an empty list
    /// if the collection has no records. Used by lookup/filter/lookup_many/
    /// random_select steps to scan the full collection.
    /// </summary>
    Task<IReadOnlyList<object?>> ScanCollectionAsync(
        string projectId, string collectionId, CancellationToken ct);

    /// <summary>
    /// Load a workflow definition from the <c>workflows</c> table by id.
    /// Returns null if the workflow is not found. Used by the workflow step
    /// handler and condition workflow references.
    /// </summary>
    Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(
        string projectId, string workflowId, CancellationToken ct);

    /// <summary>
    /// Opens the transaction the live-serve endpoint path flushes its queued writes and deletes through. Every
    /// write must apply the SAME persistence semantics as the record-CRUD data plane so an endpoint-driven write
    /// is indistinguishable from a direct <c>POST /v3/storage/.../{key}</c>. Nothing is durable until
    /// <see cref="IEndpointWriteTransaction.CommitAsync"/>; disposing without committing discards every write.
    /// Throws on failure (fail-closed) — the caller surfaces a 5xx rather than silently dropping the save.
    /// </summary>
    Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct);

    /// <summary>
    /// Read a global record (collection-wide, keyed by recordId not steamId).
    /// Used by the leaderboard projection to read the current
    /// <c>entriesByPlayer</c> map before incrementally updating one player's entry.
    /// Returns null if the record is missing.
    /// </summary>
    Task<object?> ReadGlobalRecordAsync(
        string projectId, string collectionId, string recordId, CancellationToken ct);

    /// <summary>
    /// Durably write a global record's payload (collection-wide, keyed by
    /// recordId). Used by the leaderboard projection to update
    /// <c>leaderboard_global/default.entriesByPlayer.{steamId}</c> on every
    /// <c>save-all</c> <c>players</c> write. Must be idempotent (upsert by
    /// (project, collection, recordId)). Throws on failure — the projection is
    /// best-effort for the save (never blocks the 200), but a failure is reported.
    /// </summary>
    Task WriteGlobalRecordAsync(
        string projectId, string collectionId, string recordId,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct);

    /// <summary>
    /// Whether the project opted in to the built-in <c>players</c>/<c>skills</c>/<c>kills</c>/<c>leaderboard_global</c>
    /// repair and projection logic (project metadata <c>legacyPlayerProjections: true</c>). Off by default.
    /// </summary>
    Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct);
}

/// <summary>The writes of one endpoint call, applied atomically.</summary>
public interface IEndpointWriteTransaction : IAsyncDisposable
{
    /// <summary>Writes a record's payload.</summary>
    Task WriteRecordAsync(
        string projectId, string collectionId, string key,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct);

    /// <summary>Soft-deletes a record (permanent tombstone), mirroring the record-CRUD delete.</summary>
    Task DeleteRecordAsync(
        string projectId, string collectionId, string key, CancellationToken ct);

    /// <summary>Makes every write and delete durable together.</summary>
    Task CommitAsync(CancellationToken ct);
}

