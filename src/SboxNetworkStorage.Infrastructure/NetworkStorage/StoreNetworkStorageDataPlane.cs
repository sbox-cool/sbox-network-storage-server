using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// store-only Network Storage data plane. All reads and writes go through
/// The store — no workspace fallback, no newest-wins comparison. workspace is used
/// only for snapshot backups (separate service).
///
/// - Reads/writes route by <c>collectionType</c>: global collections use the
///   <c>global_records</c> table, per-player collections use <c>records</c>.
///   Without this routing, a read/write on a global collection (e.g.
///   <c>leaderboard_global</c>) would hit the empty <c>records</c> table.
/// - Writes fail-closed: a store error throws; nothing is written elsewhere.
/// - Tombstones are permanent: a soft-delete is never resurrected.
/// - A store miss returns NotFound (the record does not exist).
/// </summary>
public sealed class StoreNetworkStorageDataPlane(
    INetworkStorageStore store) : INetworkStorageDataPlane
{
    public async Task<RecordReadResult> ReadRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        // Route by collectionType: global collections store rows in global_records.
        var row = await IsGlobalCollectionAsync(projectId, collectionId, ct)
            ? await store.ReadGlobalRecordAsync(projectId, collectionId, recordKey, ct)
            : await store.ReadRecordAsync(projectId, collectionId, recordKey, ct);
        if (row is not { } record)
            return RecordReadResult.NotFound;

        var payload = RecordRow.ExtractPayload(record);
        return payload is null
            ? RecordReadResult.NotFound
            : RecordReadResult.From(payload.Value, "store");
    }

    public async Task WriteRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, JsonElement value, CancellationToken ct)
    {
        if (await IsGlobalCollectionAsync(projectId, collectionId, ct))
            await store.UpsertGlobalRecordAsync(projectId, collectionId, recordKey, value, version: 1, ct);
        else
            await store.UpsertRecordAsync(projectId, collectionId, recordKey, value, deleted: false, version: 1, ct);
    }

    public async Task DeleteRecordAsync(
        long ownerUserId, string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        // Global collections have no soft-delete tombstone column — hard-delete.
        // Per-player collections use a permanent soft-delete tombstone.
        if (await IsGlobalCollectionAsync(projectId, collectionId, ct))
            await store.DeleteGlobalRecordAsync(projectId, collectionId, recordKey, ct);
        else
            await store.UpsertRecordAsync(projectId, collectionId, recordKey,
                JsonDocument.Parse("null").RootElement, deleted: true, version: 1, ct);
    }

    /// <summary>
    /// Resolve whether a collection is <c>collectionType: "global"</c> by reading
    /// its <c>definition_json</c> from the store. Mirrors
    /// <see cref="StoreEndpointDataSource.IsGlobalCollectionAsync"/> and
    /// <see cref="NativeQueryExecutor.IsGlobalCollectionAsync"/>: defaults to
    /// <c>false</c> (per-steamid) when the collection is missing, the field is
    /// absent, or the lookup fails — matching every other read/write path.
    /// </summary>
    private async Task<bool> IsGlobalCollectionAsync(
        string projectId, string collectionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(collectionId))
            return false;
        try
        {
            var col = await store.ReadCollectionAsync(projectId, collectionId, ct);
            if (!col.HasValue) return false;
            if (!col.Value.TryGetProperty("definition_json", out var defProp)) return false;
            // definition_json may be a JSON string (InMemoryNetworkStorageStore stores it as
            // GetRawText()) or a parsed object (the real store uses ParseJsonColumn
            // which returns a detached JsonElement). Handle both.
            if (defProp.ValueKind == JsonValueKind.String)
            {
                var s = defProp.GetString();
                if (string.IsNullOrEmpty(s)) return false;
                using var doc = JsonDocument.Parse(s);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("collectionType", out var ctProp)
                    && ctProp.ValueKind == JsonValueKind.String
                    && string.Equals(ctProp.GetString(), "global", StringComparison.OrdinalIgnoreCase);
            }
            if (defProp.ValueKind != JsonValueKind.Object) return false;
            return defProp.TryGetProperty("collectionType", out var ctObj)
                && ctObj.ValueKind == JsonValueKind.String
                && string.Equals(ctObj.GetString(), "global", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Default to per-steamid on any lookup failure — matching the
            // status-quo routing and every other IsGlobalCollectionAsync.
            return false;
        }
    }
}
