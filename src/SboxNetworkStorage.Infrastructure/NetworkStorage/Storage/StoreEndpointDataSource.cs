using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

/// <summary>
/// Real implementation of <see cref="IEndpointDataSource"/> backed by
/// The store via <see cref="INetworkStorageStore"/>. Reads endpoint definitions
/// from the <c>endpoints</c> table, and records from the <c>records</c> table
/// (per-player collections) or the <c>global_records</c> table (global
/// collections), routing by the collection's <c>collectionType</c> — matching
/// <see cref="NetworkStorageController"/>, <c>CollectionDataOverviewBuilder</c>,
/// and <c>StorageApiEndpoints</c>. A global collection (e.g.
/// <c>leaderboard_global</c>) has zero rows in <c>records</c>; reading it from
/// the wrong table was the root cause of the empty in-game leaderboard.
///
/// The store is the sole source of truth: a miss is a real miss.
/// This keeps the endpoint-execution read path consistent with the website
/// browse API (<c>NetworkStorageController.BrowseCollectionDataApi</c>), which
/// reads the store directly. A split-brain where the game client loads a stale
/// workspace record while the dashboard shows the current store record is
/// impossible.
/// </summary>
public sealed class StoreEndpointDataSource : IEndpointDataSource, IEndpointRecordSizeLimit
{
    private readonly INetworkStorageStore _store;
    private readonly ILogger<StoreEndpointDataSource> _logger;

    public StoreEndpointDataSource(
        INetworkStorageStore store,
        ILogger<StoreEndpointDataSource> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public int MaxPayloadBytes => _store.MaxPayloadBytes;

    public async Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(
        string projectId, string endpointSlug, CancellationToken ct)
    {
        try
        {
            if (_store is MetadataCachingNetworkStore metadata)
                return await metadata.ReadEndpointDefinitionAsync(projectId, endpointSlug, ct);

            // Try direct read first (endpointId == slug is the common case).
            var direct = await _store.ReadEndpointAsync(projectId, endpointSlug, ct);
            if (direct.HasValue) return ExtractDefinition(direct.Value);

            // Fall back to listing and filtering by slug field.
            var endpoints = await _store.ListEndpointsAsync(projectId, ct);
            foreach (var ep in endpoints)
            {
                if (ep.TryGetProperty("slug", out var slugProp) &&
                    string.Equals(slugProp.GetString(), endpointSlug, StringComparison.Ordinal))
                {
                    return ExtractDefinition(ep);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read endpoint definition {ProjectId}/{EndpointSlug} from Store", projectId, endpointSlug);
            return null;
        }
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(
        string projectId, CancellationToken ct)
    {
        try
        {
            var rows = await _store.ListCollectionsAsync(projectId, ct);
            var result = new List<Dictionary<string, object?>>();
            foreach (var row in rows)
            {
                var dict = new Dictionary<string, object?>();
                if (row.TryGetProperty("collection_id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    dict["collection_id"] = idProp.GetString();
                if (row.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    dict["name"] = nameProp.GetString();
                if (row.TryGetProperty("visibility", out var visProp) && visProp.ValueKind == JsonValueKind.String)
                    dict["visibility"] = visProp.GetString();
                if (row.TryGetProperty("definition_json", out var defProp))
                    dict["definition_json"] = EndpointExpression.FromJson(defProp);
                result.Add(dict);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list collections for {ProjectId} from Store", projectId);
            return Array.Empty<Dictionary<string, object?>>();
        }
    }
    public async Task<object?> ReadRecordAsync(
        string projectId, string collectionId, string key, CancellationToken ct)
    {
        // Global collections (collectionType == "global") store their rows in the
        // global_records table keyed by record_id, NOT in the records table keyed
        // by record_key. Routing by collection type matches every other read path
        // (NetworkStorageController.RowDetail, CollectionDataOverviewBuilder). The
        // prior implementation always read the records table, which is empty for a
        // global collection — the root cause of the empty in-game leaderboard.
        if (await IsGlobalCollectionAsync(projectId, collectionId, ct))
        {
            try
            {
                var raw = await _store.ReadGlobalRecordAsync(projectId, collectionId, key, ct);
                if (!raw.HasValue) return null;
                return ExtractRecordPayload(raw.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Store global record read failed {ProjectId}/{CollectionId}/{Key}", projectId, collectionId, key);
                return null;
            }
        }

        // The store is authoritative: a miss or exception is the final answer.
        try
        {
            var raw = await _store.ReadRecordAsync(projectId, collectionId, key, ct);
            // A read must yield the stored PAYLOAD (what {{step.field}} resolves
            // against), not the record wrapper {record_key, payload_json, deleted, …}.
            // Soft-deleted tombstones read as missing — matching legacy server's storage layer.
            if (raw.HasValue)
            {
                var payload = ExtractRecordPayload(raw.Value);
                if (payload is not null) return payload;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Record read failed {ProjectId}/{CollectionId}/{Key}", projectId, collectionId, key);
        }

        return null;
    }

    public async Task<IReadOnlyList<object?>> ScanCollectionAsync(
        string projectId, string collectionId, CancellationToken ct)
    {
        // Global collections are scanned via ListGlobalRecordsAsync (global_records
        // table), not ListRecordsAsync (records table). Same routing as
        // NetworkStorageController.BrowseCollectionDataApi.
        if (await IsGlobalCollectionAsync(projectId, collectionId, ct))
        {
            try
            {
                var rows = await _store.ListGlobalRecordsAsync(projectId, collectionId, ct);
                var result = new List<object?>();
                foreach (var row in rows)
                {
                    var payload = ExtractRecordPayload(row);
                    if (payload != null) result.Add(payload);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to scan global collection {ProjectId}/{CollectionId} from Store", projectId, collectionId);
                return Array.Empty<object?>();
            }
        }

        try
        {
            var rows = await _store.ListRecordsAsync(projectId, collectionId, ct);
            var result = new List<object?>();
            foreach (var row in rows)
            {
                // ExtractRecordPayload returns null for soft-deleted tombstones and
                // absent payloads — both skipped here (matches legacy server's scanCollectionUncached).
                var payload = ExtractRecordPayload(row);
                if (payload != null) result.Add(payload);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to scan collection {ProjectId}/{CollectionId} from Store", projectId, collectionId);
            return Array.Empty<object?>();
        }
    }

    public async Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(
        string projectId, string workflowId, CancellationToken ct)
    {
        try
        {
            var wf = await _store.ReadWorkflowAsync(projectId, workflowId, ct);
            if (!wf.HasValue) return null;
            return ExtractDefinition(wf.Value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read workflow definition {ProjectId}/{WorkflowId} from Store", projectId, workflowId);
            return null;
        }
    }

    public async Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
        => new WriteTransaction(this, await _store.BeginTransactionAsync(ct));

    private sealed class WriteTransaction(StoreEndpointDataSource owner, IStoreTransaction transaction) : IEndpointWriteTransaction
    {
        public async Task WriteRecordAsync(
            string projectId, string collectionId, string key,
            IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        {
            try
            {
                // Serialize the executor's JS-value-model payload to JSON and persist it
                // via the SAME store primitive the record-CRUD data plane uses, so an
                // endpoint-driven write is byte-identical to a direct client record write.
                // Route by collection type: global collections write to global_records
                // (keyed by record_id), per-player collections write to records (keyed by
                // record_key). Matches NetworkStorageController.RowEditSave routing.
                var element = JsonSerializer.SerializeToElement(payload);
                if (await owner.IsGlobalCollectionAsync(projectId, collectionId, ct))
                {
                    await transaction.Store.UpsertGlobalRecordAsync(projectId, collectionId, key, element, version: 1, ct);
                }
                else
                {
                    await transaction.Store.UpsertRecordAsync(projectId, collectionId, key, element, deleted: false, version: 1, ct);
                }
            }
            catch (Exception ex)
            {
                // Fail-closed: log the underlying store error and rethrow so the live-serve
                // caller surfaces a 5xx instead of silently dropping the save.
                owner._logger.LogWarning(ex, "Durable endpoint write failed {ProjectId}/{CollectionId}/{Key}", projectId, collectionId, key);
                throw;
            }
        }

        public async Task DeleteRecordAsync(
            string projectId, string collectionId, string key, CancellationToken ct)
        {
            try
            {
                // Route by collection type: global collections delete from global_records,
                // per-player collections soft-delete (tombstone) in records. Matches the
                // read/write routing and NetworkStorageController.
                if (await owner.IsGlobalCollectionAsync(projectId, collectionId, ct))
                {
                    await transaction.Store.DeleteGlobalRecordAsync(projectId, collectionId, key, ct);
                }
                else
                {
                    // Permanent soft-delete tombstone, identical to StoreNetworkStorageDataPlane.
                    await transaction.Store.UpsertRecordAsync(projectId, collectionId, key,
                        JsonSerializer.SerializeToElement<object?>(null), deleted: true, version: 1, ct);
                }
            }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "Durable endpoint delete failed {ProjectId}/{CollectionId}/{Key}", projectId, collectionId, key);
                throw;
            }
        }

        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    public async Task<object?> ReadGlobalRecordAsync(
        string projectId, string collectionId, string recordId, CancellationToken ct)
    {
        try
        {
            var raw = await _store.ReadGlobalRecordAsync(projectId, collectionId, recordId, ct);
            if (!raw.HasValue) return null;
            // Global records store the payload directly — extract it the same way
            // per-player records do (ExtractRecordPayload), so the caller gets the
            // value object, not the row wrapper.
            return ExtractRecordPayload(raw.Value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read global record {ProjectId}/{CollectionId}/{RecordId}", projectId, collectionId, recordId);
            return null;
        }
    }

    public async Task WriteGlobalRecordAsync(
        string projectId, string collectionId, string recordId,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(payload);
            await _store.UpsertGlobalRecordAsync(projectId, collectionId, recordId, element, version: 1, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable global record write failed {ProjectId}/{CollectionId}/{RecordId}", projectId, collectionId, recordId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct)
    {
        var project = await _store.ReadProjectAsync(projectId, ct);
        return project is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("legacyPlayerProjections", out var flag)
            && flag.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Unwrap a <c>records</c> row (<c>{record_key, payload_json, deleted, version, …}</c>)
    /// to the stored payload the executor's read/scan steps operate on. Returns null
    /// for a soft-deleted tombstone or an absent payload. <c>payload_json</c> is a
    /// nested JSON object in the real store (INetworkStorageStore.BuildRecordRow), so
    /// the executor must NOT receive the wrapper — that would make every
    /// <c>{{step.field}}</c> reference resolve to undefined.
    /// </summary>
    internal static object? ExtractRecordPayload(JsonElement row) => RecordRow.ExtractValueModel(row);

    /// <summary>
    /// Determine whether a collection is a global-collection-type (rows live in the
    /// <c>global_records</c> table) or per-steamid (rows live in the <c>records</c>
    /// table). Reads the collection's <c>definition_json</c> from the store and looks
    /// for the <c>collectionType</c> field. Defaults to <c>false</c> (per-steamid)
    /// when the collection is missing, the field is absent, or the lookup fails —
    /// matching the status-quo routing and <see cref="CollectionTypeLabel"/>'s fallback.
    /// This is the routing decision every other read/write path in the codebase
    /// already makes (<c>NetworkStorageController</c>, <c>CollectionDataOverviewBuilder</c>,
    /// <c>StorageApiEndpoints</c>); the endpoint executor now matches them.
    /// </summary>
    private async Task<bool> IsGlobalCollectionAsync(
        string projectId, string collectionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(collectionId))
            return false;
        try
        {
            var rows = await _store.ListCollectionsAsync(projectId, ct);
            foreach (var row in rows)
            {
                if (!row.TryGetProperty("collection_id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(idProp.GetString(), collectionId, StringComparison.Ordinal))
                    continue;
                // collectionType lives inside definition_json (not a top-level column).
                if (!row.TryGetProperty("definition_json", out var defProp))
                    return false;
                // definition_json may be a JSON string (raw text column) or a parsed
                // object (BuildCollectionRow returns it via ParseJsonOrNull, which
                // produces a JsonElement). Handle both.
                JsonElement def;
                if (defProp.ValueKind == JsonValueKind.String)
                {
                    var s = defProp.GetString();
                    if (string.IsNullOrEmpty(s)) return false;
                    using var doc = JsonDocument.Parse(s);
                    def = doc.RootElement;
                    return def.ValueKind == JsonValueKind.Object
                        && def.TryGetProperty("collectionType", out var ctProp)
                        && ctProp.ValueKind == JsonValueKind.String
                        && string.Equals(ctProp.GetString(), "global", StringComparison.OrdinalIgnoreCase);
                }
                if (defProp.ValueKind != JsonValueKind.Object)
                    return false;
                return defProp.TryGetProperty("collectionType", out var ctObj)
                    && ctObj.ValueKind == JsonValueKind.String
                    && string.Equals(ctObj.GetString(), "global", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve collectionType for {ProjectId}/{CollectionId} — defaulting to per-steamid", projectId, collectionId);
            return false;
        }
    }

    /// <summary>
    /// Extract the endpoint definition JSON from a store row.  The
    /// <c>definition_json</c> column stores the full endpoint definition (including
    /// <c>steps</c>, <c>response</c>, <c>let</c>, etc.) as a JSON string.
    /// </summary>
    private static Dictionary<string, object?>? ExtractDefinition(JsonElement ep)
    {
        if (ep.TryGetProperty("definition_json", out var defJson))
        {
            if (defJson.ValueKind == JsonValueKind.String)
            {
                var str = defJson.GetString();
                if (string.IsNullOrEmpty(str)) return null;
                using var doc = JsonDocument.Parse(str);
                return EndpointExpression.FromJson(doc.RootElement) as Dictionary<string, object?>;
            }
            if (defJson.ValueKind == JsonValueKind.Object)
                return EndpointExpression.FromJson(defJson) as Dictionary<string, object?>;
        }
        return null;
    }
}
