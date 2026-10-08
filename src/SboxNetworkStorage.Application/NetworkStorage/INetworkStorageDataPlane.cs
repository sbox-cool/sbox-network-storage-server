using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// The live Network Storage record data plane. Implementations decide where
/// records are read from and written to (ScyllaDB-authoritative once cutover is
/// enabled, or the legacy Bunny path before then). Keeps the HTTP endpoints
/// free of backend-selection logic.
/// </summary>
public interface INetworkStorageDataPlane
{
    /// <summary>
    /// Reads a single record. Returns <see cref="RecordReadResult.NotFound"/>
    /// when the record is absent (or soft-deleted) across all backends.
    /// </summary>
    Task<RecordReadResult> ReadRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct);

    /// <summary>
    /// Writes a single record. Authoritative: on the primary backend's failure
    /// this throws (fail-closed) — it is NOT silently written elsewhere.
    /// </summary>
    Task WriteRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, JsonElement value, CancellationToken ct);

    /// <summary>Deletes (soft-deletes) a single record. Fail-closed like writes.</summary>
    Task DeleteRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct);
}

/// <summary>Outcome of a record read, carrying which backend answered.</summary>
public readonly record struct RecordReadResult(bool Found, JsonElement Value, string Source)
{
    public static RecordReadResult NotFound { get; } = new(false, default, "none");
    public static RecordReadResult From(JsonElement value, string source) => new(true, value, source);
}
