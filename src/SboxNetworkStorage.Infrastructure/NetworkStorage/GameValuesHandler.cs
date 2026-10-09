using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for <c>GET /v3/values/:projectId</c> (and <c>/v1/values/:projectId</c>).
/// Reads game-values and collections from the store;
/// falls back to the workspace client on a store miss or connection error.
/// Produces the legacy-compatible client payload via <see cref="ToClientFormat"/>.
/// </summary>
public sealed class GameValuesHandler : INetworkStorageHandler
{
    private const string GameValuesFileName = "game-values.json";
    private const string CollectionsFileName = "collections.json";
    private const string ProjectsFileName = "projects.json";

    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly INetworkStorageStore _networkStore;
    private readonly ILogger<GameValuesHandler> _logger;

    public GameValuesHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageStore networkStore,
        ILogger<GameValuesHandler> logger)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _networkStore = networkStore;
        _logger = logger;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Values;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Values
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult();
        }

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
        {
            return UnauthorizedResult();
        }

        var ownerUserId = auth.UserId;
        var keyType = auth.KeyType; // "public" or "secret"

        // ── Read project list to verify project exists and is enabled ──
        IReadOnlyList<WorkspaceProject> projects;
        try
        {
            projects = await _workspaceClient.GetUserProjectsAsync(ownerUserId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                403,
                "PROJECT_DISABLED",
                new { error = new { code = "PROJECT_DISABLED", message = "This project is currently disabled." } },
                storagePathsRead: new[] { $"network-storage/users/{ownerUserId}/{ProjectsFileName}" },
                authDecision: keyType);
        }

        var project = projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null)
        {
            return UnauthorizedResult();
        }

        if (!project.Enabled)
        {
            return NetworkStorageResult.Error(
                403,
                "PROJECT_DISABLED",
                new { error = new { code = "PROJECT_DISABLED", message = "This project is currently disabled." } },
                storagePathsRead: new[] { $"network-storage/users/{ownerUserId}/{projectId}/{ProjectsFileName}" },
                authDecision: keyType);
        }

        var gameValuesPath = $"network-storage/users/{ownerUserId}/{projectId}/{GameValuesFileName}";
        var collectionsPath = $"network-storage/users/{ownerUserId}/{projectId}/{CollectionsFileName}";

        try
        {
            return await ReadFromStoreAsync(auth, keyType, projectId, gameValuesPath, collectionsPath, request.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store game-values read failed for project={ProjectId}; falling back to workspace objects", projectId);
        }

        return await ReadFromWorkspaceAsync(auth, keyType, projectId, ownerUserId, gameValuesPath, collectionsPath, request.CancellationToken);
    }

    private async Task<NetworkStorageResult> ReadFromStoreAsync(
        StorageApiKeyAuthResult auth, string keyType, string projectId,
        string gameValuesPath, string collectionsPath, CancellationToken ct)
    {
        // Read game_values and collections from the store in parallel
        var gvTask = _networkStore.ReadGameValuesAsync(projectId, ct);
        var colTask = _networkStore.ListCollectionsAsync(projectId, ct);
        await Task.WhenAll(gvTask, colTask);

        // Parse game-values payload
        var gvRow = gvTask.Result;
        JsonElement gv;
        if (gvRow is { } row && row.TryGetProperty("payload_json", out var payload)
            && payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            gv = payload.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(payload.GetString()!)
                : payload;
        }
        else
        {
            gv = JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() });
        }

        // Reconstruct collections array in the shape ToClientFormat expects:
        // [{ id, name, visibility, constants?, tables?, ... }]
        var colRows = colTask.Result;
        var collectionsArray = new List<object>();
        foreach (var col in colRows)
        {
            if (col.ValueKind != JsonValueKind.Object) continue;
            var colId = col.TryGetProperty("collection_id", out var cid) && cid.ValueKind == JsonValueKind.String
                ? cid.GetString() : null;
            var name = col.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String
                ? cn.GetString() : null;
            var visibility = col.TryGetProperty("visibility", out var cv) && cv.ValueKind == JsonValueKind.String
                ? cv.GetString() : "private";

            var entry = new Dictionary<string, object?>
            {
                ["id"] = colId,
                ["name"] = name,
                ["visibility"] = visibility,
            };

            // Relational and store stores can return parsed JSON or JSON text.
            if (col.TryGetProperty("definition_json", out var def))
            {
                try
                {
                    var defElement = def.ValueKind == JsonValueKind.String
                        ? JsonSerializer.Deserialize<JsonElement>(def.GetString()!)
                        : def;
                    if (defElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in defElement.EnumerateObject())
                        {
                            if (prop.Name is "id" or "name" or "visibility") continue;
                            entry[prop.Name] = prop.Value.Clone();
                        }
                    }
                }
                catch (JsonException) { /* malformed definition — skip extra fields */ }
            }

            collectionsArray.Add(entry);
        }

        var collectionsElement = JsonSerializer.SerializeToElement(collectionsArray);
        var clientData = ToClientFormatCore(gv, collectionsElement, CanReadPrivateCollections(auth));

        return NetworkStorageResult.Ok(
            clientData,
            storagePathsRead: new[] { $"store://{projectId}/game_values", $"store://{projectId}/collections" },
            authDecision: keyType);
    }

    private async Task<NetworkStorageResult> ReadFromWorkspaceAsync(
        StorageApiKeyAuthResult auth, string keyType, string projectId, long ownerUserId,
        string gameValuesPath, string collectionsPath, CancellationToken ct)
    {
        JsonElement gv;
        JsonElement collections;

        try
        {
            var gvTask = _workspaceClient.GetProjectResourceAsync<JsonElement>(ownerUserId, projectId, GameValuesFileName, ct);
            var colTask = _workspaceClient.GetProjectResourceAsync<JsonElement>(ownerUserId, projectId, CollectionsFileName, ct);
            await Task.WhenAll(gvTask, colTask);

            gv = gvTask.Result;
            collections = colTask.Result;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                500,
                "ENDPOINT_CONFIG_ERROR",
                new { error = new { code = "ENDPOINT_CONFIG_ERROR", message = "Endpoint configuration error. Check step collection references and field names." } },
                storagePathsRead: new[] { gameValuesPath, collectionsPath },
                authDecision: keyType);
        }

        var clientData = ToClientFormatCore(gv, collections, CanReadPrivateCollections(auth));

        return NetworkStorageResult.Ok(
            clientData,
            storagePathsRead: new[] { gameValuesPath, collectionsPath },
            authDecision: keyType);
    }

    private NetworkStorageResult UnauthorizedResult()
    {
        return NetworkStorageResult.Error(
            401,
            "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: "anonymous");
    }

    /// <summary>
    /// Port of legacy server's <c>toClientFormat(gv, collections)</c>.
    /// Merges legacy game-values.json items with collection-level constants/tables.
    /// Collection constants/tables take precedence over legacy game-values.
    /// </summary>
    internal static object ToClientFormat(JsonElement gv, JsonElement collections) =>
        ToClientFormatCore(gv, collections, includePrivate: false);

    private static bool CanReadPrivateCollections(StorageApiKeyAuthResult auth) =>
        string.Equals(auth.KeyType, "secret", StringComparison.Ordinal)
        && ApiKeyPermissionPolicy.HasPermission(auth, "collections", "r");

    private static object ToClientFormatCore(JsonElement gv, JsonElement collections, bool includePrivate)
    {
        var version = gv.ValueKind == JsonValueKind.Object && gv.TryGetProperty("_version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        var updatedAt = gv.ValueKind == JsonValueKind.Object && gv.TryGetProperty("_updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String
            ? ua.GetString()
            : null;

        var groups = new Dictionary<string, object>();
        var tables = new Dictionary<string, object>();
        var hiddenIds = new HashSet<string>(StringComparer.Ordinal);
        var hiddenCollections = new HashSet<string>(StringComparer.Ordinal);

        // Reserve private IDs before merging. Otherwise a duplicate legacy item
        // (or public collection) can reintroduce a filtered private value.
        if (!includePrivate && collections.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in collections.EnumerateArray())
            {
                if (!IsPrivateCollection(col)) continue;
                if (col.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    hiddenCollections.Add(id.GetString()!);
                foreach (var kind in new[] { "constants", "tables" })
                {
                    if (!col.TryGetProperty(kind, out var values) || values.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.Object
                            && value.TryGetProperty("id", out var valueId) && valueId.ValueKind == JsonValueKind.String)
                            hiddenIds.Add(valueId.GetString()!);
                    }
                }
            }
        }

        // ── Collection-level constants and tables take precedence ──
        if (collections.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in collections.EnumerateArray())
            {
                if (col.ValueKind != JsonValueKind.Object || (!includePrivate && IsPrivateCollection(col))) continue;
                var colId = col.TryGetProperty("id", out var colIdProp) && colIdProp.ValueKind == JsonValueKind.String
                    ? colIdProp.GetString()
                    : null;

                if (col.TryGetProperty("constants", out var constantsProp) && constantsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in constantsProp.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var cId = c.TryGetProperty("id", out var cIdProp) && cIdProp.ValueKind == JsonValueKind.String
                            ? cIdProp.GetString()
                            : null;
                        if (string.IsNullOrEmpty(cId) || hiddenIds.Contains(cId)) continue;

                        var name = c.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        var desc = c.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
                        var entries = c.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Object
                            ? entriesProp
                            : default;

                        groups[cId] = new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["description"] = desc ?? "",
                            ["values"] = entries.ValueKind == JsonValueKind.Object ? JsonObjectToDict(entries) : new Dictionary<string, object?>(),
                            ["_collection"] = colId
                        };
                    }
                }

                if (col.TryGetProperty("tables", out var tablesProp) && tablesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tablesProp.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.Object) continue;
                        var tId = t.TryGetProperty("id", out var tIdProp) && tIdProp.ValueKind == JsonValueKind.String
                            ? tIdProp.GetString()
                            : null;
                        if (string.IsNullOrEmpty(tId) || hiddenIds.Contains(tId)) continue;

                        tables[tId] = BuildTableEntry(t, colId);
                    }
                }
            }
        }

        // ── Legacy game-values items — only fill in what collections don't define ──
        var items = ExtractItems(gv);

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemId = item.TryGetProperty("id", out var itemIdProp) && itemIdProp.ValueKind == JsonValueKind.String
                ? itemIdProp.GetString()
                : null;
            if (string.IsNullOrEmpty(itemId) || hiddenIds.Contains(itemId)) continue;
            var legacyCollection = item.TryGetProperty("_collection", out var association)
                ? association
                : item.TryGetProperty("collectionId", out association) ? association : default;
            if (legacyCollection.ValueKind == JsonValueKind.String
                && hiddenCollections.Contains(legacyCollection.GetString()!)) continue;

            var itemType = item.TryGetProperty("type", out var itemTypeProp) ? itemTypeProp.GetString() : null;

            if (string.Equals(itemType, "table", StringComparison.OrdinalIgnoreCase))
            {
                if (!tables.ContainsKey(itemId))
                {
                    tables[itemId] = BuildTableEntry(item, null);
                }
            }
            else
            {
                if (!groups.ContainsKey(itemId))
                {
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var desc = item.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
                    var entries = item.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Object
                        ? entriesProp
                        : default;

                    groups[itemId] = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["description"] = desc ?? "",
                        ["values"] = entries.ValueKind == JsonValueKind.Object ? JsonObjectToDict(entries) : new Dictionary<string, object?>()
                    };
                }
            }
        }

        return new { version, updatedAt, groups, tables };
    }

    private static bool IsPrivateCollection(JsonElement collection) =>
        collection.ValueKind == JsonValueKind.Object
        && collection.TryGetProperty("visibility", out var visibility)
        && visibility.ValueKind == JsonValueKind.String
        && string.Equals(visibility.GetString(), "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalise game-value items from the raw <c>gv</c> JSON.
    /// Mirrors legacy server's <c>normalizeGameValueItems</c>.
    /// </summary>
    private static List<JsonElement> ExtractItems(JsonElement gv)
    {
        if (gv.ValueKind != JsonValueKind.Object) return new List<JsonElement>();

        // New format: { items: [...] }
        if (gv.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            return itemsProp.EnumerateArray().ToList();
        }

        // Legacy format: { groups: [...], tables: [...] }
        var result = new List<JsonElement>();
        if (gv.TryGetProperty("groups", out var groupsProp) && groupsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groupsProp.EnumerateArray())
            {
                result.Add(g);
            }
        }
        if (gv.TryGetProperty("tables", out var tablesProp) && tablesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tablesProp.EnumerateArray())
            {
                result.Add(t);
            }
        }
        return result;
    }

    private static Dictionary<string, object?> BuildTableEntry(JsonElement t, string? collectionId)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in t.EnumerateObject())
        {
            result[prop.Name] = JsonValueToObject(prop.Value);
        }
        if (collectionId is not null)
            result["_collection"] = collectionId;
        return result;
    }

    /// <summary>
    /// Convert a JSON object <see cref="JsonElement"/> to <c>Dictionary&lt;string, object&gt;</c>
    /// with primitive CLR types, matching how <c>System.Text.Json</c> serialization handles the values.
    /// </summary>
    private static Dictionary<string, object?> JsonObjectToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = JsonValueToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonValueToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => JsonArrayToList(value),
            JsonValueKind.Object => JsonObjectToDict(value),
            _ => null
        };
    }

    private static List<object> JsonArrayToList(JsonElement arr)
    {
        var list = new List<object>();
        foreach (var item in arr.EnumerateArray())
        {
            var val = JsonValueToObject(item);
            if (val is not null) list.Add(val);
        }
        return list;
    }
}
