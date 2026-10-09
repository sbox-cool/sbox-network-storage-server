using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for the <c>StorageGlobal</c> route family: GET routes that list global
/// records or fetch a single global record. Mirrors legacy server's <c>routeV3GlobalList</c> and
/// <c>routeV3GlobalGetRecord</c>.
///
/// <list type="bullet">
///   <item>List:   <c>GET /v3/storage/:projectId/:collectionId/list</c></item>
///   <item>Record: <c>GET /v3/storage/:projectId/:collectionId/record/:recordId</c></item>
/// </list>
///
/// Branches on the presence of a <c>recordId</c> route parameter. When present, reads a single
/// global record from <c>INetworkStorageStore.global_records</c>. Otherwise, lists that
/// collection's global records from the same table, sorts by <c>_timestamp</c> descending,
/// and applies optional <c>limit</c>/<c>after</c> query parameters with cursor-based pagination.
///
/// Store-authoritative: the append producer (<c>StorageApiEndpoints.AppendRecordAsync</c>)
/// persists to <c>INetworkStorageStore.global_records</c>, so list/record reads go through
/// the store (unwrapped via <see cref="RecordRow.ExtractPayload"/>, the helper every other
/// read path uses) and keep the existing wire shape (raw record, <c>_timestamp</c>-desc
/// pagination, cursor). Reads assume the shared store view (no private=true default),
/// matching the producer, which writes the same table regardless of collection visibility.
/// The legacy workspace enumerator path is retained only for callers that construct the
/// handler without a store (existing unit tests); production always injects the store.
/// </summary>
public sealed class StorageGlobalReadHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly IWorkspaceStorageEnumerator _storageEnumerator;
    private readonly INetworkStorageStore? _store;

    public StorageGlobalReadHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        IWorkspaceStorageEnumerator storageEnumerator,
        INetworkStorageStore? store = null)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _storageEnumerator = storageEnumerator;
        _store = store;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageGlobal;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageGlobal
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var collectionId = request.RouteParameter("collectionId") ?? string.Empty;
        var recordId = request.RouteParameter("recordId"); // null → list, non-null → single record read

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return UnauthorizedResult();

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult();
        }

        if (auth is null || !auth.Enabled)
            return UnauthorizedResult();

        var ownerUserId = auth.UserId;
        var keyType = auth.KeyType;

        // Store-authoritative reads (the producer writes global_records; the obsolete
        // workspace {collection}/global enumerator never sees those rows).
        if (_store is not null)
        {
            if (recordId is not null)
            {
                return await ReadSingleRecordFromStoreAsync(_store, projectId, collectionId, recordId, keyType, request.CancellationToken);
            }

            return await ListRecordsFromStoreAsync(_store, projectId, collectionId, keyType, request, request.CancellationToken);
        }

        if (recordId is not null)
        {
            return await ReadSingleRecordAsync(ownerUserId, projectId, collectionId, recordId, keyType, request.CancellationToken);
        }

        return await ListRecordsAsync(ownerUserId, projectId, collectionId, keyType, request, request.CancellationToken);
    }

    private async Task<NetworkStorageResult> ReadSingleRecordAsync(
        long userId, string projectId, string collectionId, string recordId,
        string keyType, CancellationToken cancellationToken)
    {
        var relativePath = $"{collectionId}/global/{recordId}.json";
        var absolutePath = $"network-storage/users/{userId}/{projectId}/{relativePath}";
        var storagePaths = new[] { absolutePath };

        JsonElement record;
        try
        {
            record = await _workspaceClient.GetProjectResourceAsync<JsonElement>(userId, projectId, relativePath, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                404, "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Record not found." } },
                storagePaths, authDecision: keyType);
        }

        // GetProjectResourceAsync<JsonElement> returns a non-nullable JsonElement;
        // missing resources yield Undefined/Null ValueKind.
        if (record.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return NetworkStorageResult.Error(
                404, "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Record not found." } },
                storagePaths, authDecision: keyType);
        }

        // legacy server's routeV3GlobalGetRecord returns the raw record object via formatResponse(…, data).
        return NetworkStorageResult.Ok(record, storagePaths, authDecision: keyType);
    }

    private async Task<NetworkStorageResult> ListRecordsAsync(
        long userId, string projectId, string collectionId,
        string keyType, NetworkStorageRequest request,
        CancellationToken cancellationToken)
    {
        var dirPath = $"{collectionId}/global";
        var dirStoragePath = $"network-storage/users/{userId}/{projectId}/{dirPath}";
        var storagePathsRead = new List<string> { dirStoragePath };

        IReadOnlyList<WorkspaceStorageEntry> entries;
        try
        {
            entries = await _storageEnumerator.ListProjectResourceAsync(userId, projectId, dirPath, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Directory missing or unreachable → empty list, matching legacy server's fallback behaviour
            return NetworkStorageResult.Ok(
                new { records = Array.Empty<object>(), cursor = (string?)null },
                storagePathsRead: new[] { dirStoragePath },
                authDecision: keyType);
        }

        // Filter .json files that are not subdirectories
        var recordIds = entries
            .Where(e => !e.IsDirectory && e.ObjectName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ObjectName[..^5]) // strip ".json"
            .ToList();

        // Parse pagination query parameters (matching legacy server's defaults: limit=50, max=100, min=1)
        var limit = 50;
        var limitStr = request.QueryValue("limit");
        if (int.TryParse(limitStr, out var parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 100);

        var after = request.QueryValue("after");

        // Read each record
        var records = new List<JsonElement>();
        foreach (var id in recordIds)
        {
            var recPath = $"{dirPath}/{id}.json";
            storagePathsRead.Add($"network-storage/users/{userId}/{projectId}/{recPath}");
            try
            {
                var rec = await _workspaceClient.GetProjectResourceAsync<JsonElement>(userId, projectId, recPath, cancellationToken);
                if (rec.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    records.Add(rec);
                }
            }
            catch
            {
                // Skip unreadable records (matching legacy server's behaviour — filter(Boolean))
            }
        }

        // Sort by _timestamp descending (newest first), matching legacy server's sort
        records.Sort((a, b) =>
        {
            var ta = TryGetTimestamp(a);
            var tb = TryGetTimestamp(b);
            return tb.CompareTo(ta);
        });

        // Apply cursor-based after filter: records with _timestamp < after
        if (!string.IsNullOrEmpty(after) && DateTimeOffset.TryParse(after, out var afterDate))
        {
            records = records.Where(r => TryGetTimestamp(r) < afterDate).ToList();
        }

        // Apply limit
        var page = records.Take(limit).ToList();

        // Cursor = timestamp of the last record in the page
        string? cursor = null;
        if (page.Count == limit && page.Count > 0)
        {
            cursor = TryGetTimestampString(page[^1]);
        }

        return NetworkStorageResult.Ok(
            new { records = page, cursor },
            storagePathsRead,
            authDecision: keyType);
    }

    /// <summary>
    /// Read a single global record from <c>INetworkStorageStore.global_records</c> (the table
    /// the append producer writes). Returns the raw stored payload, like legacy server's
    /// <c>routeV3GlobalGetRecord</c> returns the record via <c>formatResponse(…, data)</c>.
    /// </summary>
    private static async Task<NetworkStorageResult> ReadSingleRecordFromStoreAsync(
        INetworkStorageStore store, string projectId, string collectionId, string recordId,
        string keyType, CancellationToken cancellationToken)
    {
        var storagePathsRead = new[] { $"store://{projectId}/{collectionId}/global/{recordId}" };

        JsonElement? row;
        try
        {
            row = await store.ReadGlobalRecordAsync(projectId, collectionId, recordId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return StorageErrorResult(storagePathsRead, keyType);
        }

        var record = row.HasValue ? RecordRow.ExtractPayload(row.Value) : null;
        if (record is null)
        {
            return NetworkStorageResult.Error(
                404, "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Record not found." } },
                storagePathsRead, authDecision: keyType);
        }

        return NetworkStorageResult.Ok(record.Value, storagePathsRead, authDecision: keyType);
    }

    /// <summary>
    /// List global records from <c>INetworkStorageStore.global_records</c>, preserving legacy server's
    /// <c>routeV3GlobalList</c> wire shape: raw records sorted by <c>_timestamp</c> descending,
    /// optional <c>limit</c>/<c>after</c> pagination, and the last page item's raw
    /// <c>_timestamp</c> as <c>cursor</c> when the page is full.
    /// </summary>
    private static async Task<NetworkStorageResult> ListRecordsFromStoreAsync(
        INetworkStorageStore store, string projectId, string collectionId,
        string keyType, NetworkStorageRequest request,
        CancellationToken cancellationToken)
    {
        var storagePathsRead = new[] { $"store://{projectId}/{collectionId}/global" };

        IReadOnlyList<JsonElement> rows;
        try
        {
            rows = await store.ListGlobalRecordsAsync(projectId, collectionId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return StorageErrorResult(storagePathsRead, keyType);
        }

        // Unwrap each row to its stored payload via the shared helper (skips tombstones
        // and absent payloads, of which global_records has none).
        var records = new List<JsonElement>(rows.Count);
        foreach (var row in rows)
        {
            var payload = RecordRow.ExtractPayload(row);
            if (payload.HasValue)
                records.Add(payload.Value);
        }

        // Sort by _timestamp descending (newest first), matching legacy server's sort.
        records.Sort((a, b) => TryGetTimestamp(b).CompareTo(TryGetTimestamp(a)));

        // Parse pagination query parameters (matching legacy server's defaults: limit=50, max=100, min=1)
        var limit = 50;
        var limitStr = request.QueryValue("limit");
        if (int.TryParse(limitStr, out var parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 100);

        // Apply cursor-based after filter: records with _timestamp < after
        var after = request.QueryValue("after");
        if (!string.IsNullOrEmpty(after) && DateTimeOffset.TryParse(after, out var afterDate))
        {
            records = records.Where(r => TryGetTimestamp(r) < afterDate).ToList();
        }

        // Apply limit
        var page = records.Take(limit).ToList();

        // Cursor = the raw _timestamp of the last record in a full page (legacy server returns
        // page[last]._timestamp verbatim: an ISO string from legacy server writes, unix-ms from
        // the native append producer). Null when the page is not full.
        JsonElement? cursor = null;
        if (page.Count == limit && page.Count > 0
            && page[^1].ValueKind == JsonValueKind.Object
            && page[^1].TryGetProperty("_timestamp", out var cursorTimestamp))
        {
            cursor = cursorTimestamp.Clone();
        }

        return NetworkStorageResult.Ok(
            new { records = page, cursor },
            storagePathsRead,
            authDecision: keyType);
    }

    /// <summary>
    /// Extract <c>_timestamp</c> as a <see cref="DateTimeOffset"/>, defaulting to
    /// <see cref="DateTimeOffset.MinValue"/>. legacy server writes ISO-8601 strings; the native append
    /// producer (<c>StorageApiEndpoints.AppendRecordAsync</c>) stamps unix milliseconds, so
    /// both shapes are honored (matching legacy server's <c>new Date(_timestamp)</c> comparisons).
    /// </summary>
    private static DateTimeOffset TryGetTimestamp(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Object
            && record.TryGetProperty("_timestamp", out var tsProp))
        {
            if (tsProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsProp.GetString(), out var parsed))
            {
                return parsed;
            }
            if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out var unixMs))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
        }
        return DateTimeOffset.MinValue;
    }

    /// <summary>Extract the raw <c>_timestamp</c> string, or null.</summary>
    private static string? TryGetTimestampString(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Object
            && record.TryGetProperty("_timestamp", out var tsProp)
            && tsProp.ValueKind == JsonValueKind.String)
        {
            return tsProp.GetString();
        }
        return null;
    }

    private static NetworkStorageResult StorageErrorResult(IReadOnlyList<string> storagePathsRead, string keyType) =>
        NetworkStorageResult.Error(
            500, "STORAGE_ERROR",
            new { error = new { code = "STORAGE_ERROR", message = "A storage operation failed." } },
            storagePathsRead, authDecision: keyType);

    private static NetworkStorageResult UnauthorizedResult() =>
        NetworkStorageResult.Error(
            401, "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: null);
}
