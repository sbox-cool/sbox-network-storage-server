using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Decorates <see cref="IWorkspaceStore"/> to make the store authoritative
/// for per-project Network Storage <b>metadata</b> — project lists, pages,
/// collections, workflows, queries, endpoints, game-values and rate-limit rules.
///
/// <list type="bullet">
/// <item><b>Reads</b> are store-authoritative: the store's answer is returned
/// as-is, including an empty list (a valid "zero resources" answer). workspace is
/// only a read fallback for a store <i>connection</i> failure — not for an
/// empty result. Falling back on empty made a missing/500ing workspace
/// <c>workflows.json</c> surface as a 500 to the game client.</item>
/// <item><b>Writes</b> are store-authoritative (fail-closed): a store failure
/// throws and workspace is left untouched. After the store succeeds the workspace copy is
/// refreshed best-effort so it stays a warm backup.</item>
/// </list>
///
/// Everything else — records (<c>saved.json</c>), raw paths, and <i>all</i> other
/// traffic — passes straight through to workspace unchanged.
/// </summary>
public sealed partial class StoreMetadataWorkspaceClient : IWorkspaceStore
{
    private readonly IWorkspaceStore _workspace;
    private readonly INetworkStorageStore _networkStore;
    private readonly ILogger<StoreMetadataWorkspaceClient> _logger;

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StoreMetadataWorkspaceClient(
        IWorkspaceStore workspace,
        INetworkStorageStore networkStore,
        ILogger<StoreMetadataWorkspaceClient> logger)
    {
        _workspace = workspace;
        _networkStore = networkStore;
        _logger = logger;
    }

    // ── Project list + usage (served from the store) ──

    public async Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken ct)
    {
        try
        {
            var memberships = await _networkStore.ListProjectsForUserAsync(userId.ToString(), ct);
            if (memberships.Count == 0)
                return await _workspace.GetUserProjectsAsync(userId, ct);

            var projects = new List<WorkspaceProject>();
            foreach (var m in memberships)
            {
                var pid = ColumnString(m, "project_id");
                if (pid is null) continue;
                var payload = await _networkStore.ReadProjectAsync(pid, ct);
                if (payload is not { } row) continue;
                projects.Add(ReconstructProject(row, pid));
            }
            return projects;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store project list read failed for user {UserId}; falling back to workspace storage", userId);
        }

        return await _workspace.GetUserProjectsAsync(userId, ct);
    }

    /// <summary>
    /// Reads current store counters and the retained workspace monthly snapshot.
    /// A present store row is authoritative for monthly traffic. Retained traffic
    /// is used only when no live row exists, because taking per-field maxima makes
    /// live values appear frozen until they exceed the old snapshot. Retained
    /// storage may still raise the cumulative footprint after migration.
    /// </summary>
    public async Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken ct)
    {
        WorkspaceProjectUsage? current = null;
        try
        {
            var storageBytes = await _networkStore.ReadProjectStorageBytesAsync(projectId, ct);
            var row = await _networkStore.ReadProjectUsageMonthlyAsync(projectId, monthKey, ct);
            if (row is { } monthly)
                current = MapMonthlyUsage(monthly, storageBytes);
            else if (storageBytes > 0)
                current = new WorkspaceProjectUsage(0, 0, 0, 0, storageBytes, 0)
                {
                    Source = "store-live",
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Store usage read failed (project={ProjectId} month={Month}); using retained workspace storage snapshot",
                projectId, monthKey);
        }

        var legacy = await _workspace.GetProjectUsageAsync(userId, projectId, monthKey, ct);
        return MergeRecoveredUsage(current, legacy);
    }

    internal static WorkspaceProjectUsage? MergeRecoveredUsage(
        WorkspaceProjectUsage? current,
        WorkspaceProjectUsage? retained)
    {
        if (current is null) return retained;
        if (retained is null || retained.StorageBytes <= current.StorageBytes) return current;
        return current with
        {
            StorageBytes = retained.StorageBytes,
            Source = "store-live+retained-storage",
            LastUpdatedAt = retained.LastUpdatedAt,
        };
    }

    /// <summary>
    /// Map a <c>project_usage_monthly</c> row to the workspace usage shape.
    /// Traffic is scoped to the requested month. Storage is the cumulative
    /// logical delta across all months, so a month rollover cannot reset the
    /// displayed footprint to zero.
    /// </summary>
    private static WorkspaceProjectUsage MapMonthlyUsage(JsonElement row, long storageBytes)
    {
        static long Read(JsonElement el, string name)
            => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
        return new WorkspaceProjectUsage(
            Requests: Read(row, "requests"),
            BytesIn: Read(row, "bytes_in"),
            BytesOut: Read(row, "bytes_out"),
            Errors: Read(row, "errors"),
            StorageBytes: Math.Max(0, storageBytes),
            ComputeUnits: Read(row, "compute_units"))
        {
            Source = "store-live",
        };
    }

    public async Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken ct)
    {
        foreach (var p in projects)
        {
            if (string.IsNullOrEmpty(p.Id))
                throw new InvalidOperationException($"Cannot persist project list for user {userId}: project '{p.Name ?? "(unnamed)"}' has an empty ID.");
        }

        var keepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            keepIds.Add(p.Id);
            var payload = JsonSerializer.SerializeToElement(p, CamelCase);
            await _networkStore.UpsertProjectAsync(p.Id, payload, 1, ct);
            await _networkStore.UpsertProjectMembershipAsync(userId.ToString(), p.Id, "owner", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
        }

        var existing = await _networkStore.ListProjectsForUserAsync(userId.ToString(), ct);
        foreach (var row in existing)
        {
            var pid = ColumnString(row, "project_id");
            if (pid is not null && !keepIds.Contains(pid))
                await _networkStore.DeleteProjectMembershipAsync(userId.ToString(), pid, ct);
        }

        try { await _workspace.SaveUserProjectsAsync(userId, projects, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "workspace storage project list mirror failed for user {UserId}", userId);
        }
    }

    // ── Pass-through members (text reads, raw paths) ──

    public async Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken ct)
    {
        var kind = Classify(resourcePath);
        if (kind == MetadataKind.None) kind = ClassifySubpath(resourcePath);
        if (kind == MetadataKind.None)
            return await _workspace.GetProjectResourceTextAsync(userId, projectId, resourcePath, ct);

        // Same store-authoritative path as GetProjectResourceAsync<T>, but
        // returns the serialized JSON text. ProjectActivityLoader reads
        // collections/endpoints/workflows via this text path; routing it through
        // The store keeps activity timestamps consistent with the authoritative
        // store instead of a stale/500ing workspace blob.
        try
        {
            JsonNode? node = kind == MetadataKind.PageContent
                ? await ReadPageContentAsync(resourcePath, projectId, ct)
                : await ReadFromStoreAsync(kind, projectId, ct);
            if (node is not null)
                return node.ToJsonString(CamelCase);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Store metadata text read failed (project={ProjectId} resource={Resource}); falling back to workspace storage",
                projectId, resourcePath);
        }

        return await _workspace.GetProjectResourceTextAsync(userId, projectId, resourcePath, ct);
    }

    public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken ct)
        => _workspace.GetRawAsync<T>(absolutePath, ct);

    public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken ct)
        => _workspace.PutRawAsync(absolutePath, data, ct);

    public async Task DeleteRawAsync(string absolutePath, CancellationToken ct)
    {
        // Intercept page-content deletions so the store (the authoritative store)
        // stays consistent when a page slug is renamed or deleted. The path shape
        // is: network-storage/users/{userId}/{projectId}/pages/{slug}.json
        var (projectId, slug) = TryParsePageContentPath(absolutePath);
        if (projectId is not null && slug is not null)
        {
            try
            {
                await _networkStore.DeletePageAsync(projectId, slug, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Store page content delete failed (project={ProjectId} slug={Slug}); deleting workspace object only",
                    projectId, slug);
            }
        }

        await _workspace.DeleteRawAsync(absolutePath, ct);
    }

    // ── Metadata-aware read ──

    public async Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
    {
        var kind = Classify(resourcePath);
        if (kind == MetadataKind.None) kind = ClassifySubpath(resourcePath);
        if (kind == MetadataKind.None)
            return await _workspace.GetProjectResourceAsync<T>(userId, projectId, resourcePath, ct);

        // The store is authoritative for metadata. An empty list is a valid
        // authoritative answer (e.g. a project with zero workflows) and MUST NOT
        // fall back to workspace — falling back makes a missing/500ing workspace blob
        // surface as a 500 to the game client (the production outage this fixes).
        // Only a store connection error falls back, so a transient the store
        // blip degrades to workspace rather than failing the whole request.
        try
        {
            JsonNode? node = kind == MetadataKind.PageContent
                ? await ReadPageContentAsync(resourcePath, projectId, ct)
                : await ReadFromStoreAsync(kind, projectId, ct);
            if (node is not null)
            {
                try
                {
                    return node.Deserialize<T>(CamelCase);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // The data was READ fine — it just doesn't fit the target type
                    // (e.g. an explicit null on a non-nullable bool/int written by the
                    // YAML source compiler). Falling through to workspace here silently
                    // returned an EMPTY list, which is how a project with real
                    // collections/endpoints in the store rendered as an empty dashboard
                    // while the workspace card — which reads the same rows as text —
                    // still counted them. Surface it instead of masking it.
                    _logger.LogError(ex,
                        "Store metadata for project={ProjectId} resource={Resource} could not be bound to {Type}; "
                        + "returning empty rather than falling back to workspace storage (workspace storage is not authoritative for metadata)",
                        projectId, resourcePath, typeof(T).Name);
                    return default;
                }
            }

            // null => singleton resource (game-values/rate-limit-rules) genuinely
            // absent in the store, or a page-content miss. Return default(T) — the
            // caller treats missing singletons as "not configured", not an error.
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Store metadata read failed (project={ProjectId} resource={Resource}); falling back to workspace storage",
                projectId, resourcePath);
        }

        return await _workspace.GetProjectResourceAsync<T>(userId, projectId, resourcePath, ct);
    }

    // ── Metadata-aware write ──

    public async Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken ct)
    {
        var kind = Classify(resourcePath);
        if (kind == MetadataKind.None) kind = ClassifySubpath(resourcePath);
        if (kind == MetadataKind.None)
        {
            await _workspace.PutProjectResourceAsync(userId, projectId, resourcePath, data, ct);
            return;
        }

        var element = JsonSerializer.SerializeToElement(data, CamelCase);
        if (kind == MetadataKind.PageContent)
            await WritePageContentAsync(resourcePath, projectId, element, ct);
        else if (kind == MetadataKind.Pages)
            await WritePagesIndexAsync(projectId, element, ct);
        else
            await WriteToStoreAsync(kind, projectId, element, ct);

        try { await _workspace.PutProjectResourceAsync(userId, projectId, resourcePath, data, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "workspace storage metadata mirror write failed (project={ProjectId} resource={Resource}); Store is authoritative",
                projectId, resourcePath);
        }
    }

    // ── the store read mapping ──

    private async Task<JsonNode?> ReadFromStoreAsync(MetadataKind kind, string projectId, CancellationToken ct)
    {
        switch (kind)
        {
            case MetadataKind.Collections:
                return BuildListNode(await _networkStore.ListCollectionsAsync(projectId, ct), "collection_id");
            case MetadataKind.Workflows:
                return BuildListNode(await _networkStore.ListWorkflowsAsync(projectId, ct), "workflow_id");
            case MetadataKind.Queries:
                return BuildListNode(await _networkStore.ListQueriesAsync(projectId, ct), "query_id");
            case MetadataKind.Endpoints:
                return BuildListNode(await _networkStore.ListEndpointsAsync(projectId, ct), "endpoint_id");
            case MetadataKind.GameValues:
                return Unwrap(await _networkStore.ReadGameValuesAsync(projectId, ct), "payload_json");
            case MetadataKind.RateLimitRules:
                return UnwrapRulesArray(Unwrap(await _networkStore.ReadRateLimitRulesAsync(projectId, ct), "rules_json"));
            case MetadataKind.Pages:
                return ReconstructPagesArray(await _networkStore.ListPagesAsync(projectId, ct));
            default:
                return null;
        }
    }

    // Rows written by 0.4.0 hold the documented {rules:[...]} wrapper; readers bind a bare array.
    private static JsonNode? UnwrapRulesArray(JsonNode? node)
        => node is JsonObject obj && obj["rules"] is JsonArray rules ? rules.DeepClone() : node;

    private async Task<JsonNode?> ReadPageContentAsync(string resourcePath, string projectId, CancellationToken ct)
    {
        var slug = ExtractPageSlug(resourcePath);
        if (slug is null) return null;
        var row = await _networkStore.ReadPageAsync(projectId, slug, ct);
        if (row is not { } r) return null;
        if (!r.TryGetProperty("content_json", out var content)) return null;
        return ToNode(content);
    }

    private static JsonArray BuildListNode(IReadOnlyList<JsonElement> rows, string idColumn)
    {
        // An empty list is a valid authoritative answer and MUST be returned as
        // an empty array, not null. Returning null here caused the read path to
        // fall back to workspace, where a 500 on workflows.json surfaced as a 500 to
        // the game client. The caller distinguishes "absent singleton" (null)
        // from "empty list" (array) by MetadataKind.
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var obj = UnwrapDefinition(row) ?? new JsonObject();
            FillFromColumn(obj, row, "id", idColumn);
            FillFromColumn(obj, row, "name", "name");
            FillFromColumn(obj, row, "visibility", "visibility");
            array.Add(obj);
        }
        return array;
    }

    private static JsonObject? UnwrapDefinition(JsonElement row)
        => row.TryGetProperty("definition_json", out var def) ? ToObject(def) : null;

    private static JsonNode? Unwrap(JsonElement? rowOrNull, string column)
    {
        if (rowOrNull is not { } row) return null;
        if (!row.TryGetProperty(column, out var val)) return null;
        return ToNode(val);
    }

    private static JsonObject? ToObject(JsonElement element) => ToNode(element) as JsonObject;

    private static JsonNode? ToNode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => JsonNode.Parse(element.GetRawText()),
        JsonValueKind.String => TryParseString(element.GetString()),
        _ => null,
    };

    private static JsonNode? TryParseString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return JsonNode.Parse(s); } catch (JsonException) { return null; }
    }

    private static void FillFromColumn(JsonObject target, JsonElement row, string targetKey, string column)
    {
        if (target.ContainsKey(targetKey)) return;
        if (row.TryGetProperty(column, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) target[targetKey] = s;
        }
    }

    private static JsonArray ReconstructPagesArray(IReadOnlyList<JsonElement> rows)
    {
        // Empty pages list is a valid authoritative answer — return an empty
        // array, not null (same rationale as BuildListNode).
        var array = new JsonArray();
        foreach (var row in rows)
        {
            var obj = new JsonObject();
            if (row.TryGetProperty("page_slug", out var s) && s.ValueKind == JsonValueKind.String) obj["slug"] = s.GetString();
            if (row.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) obj["title"] = t.GetString();
            array.Add(obj);
        }
        return array;
    }

    internal static WorkspaceProject ReconstructProject(JsonElement payload, string projectId)
    {
        var project = payload.ValueKind == JsonValueKind.Object
            ? payload.Deserialize<WorkspaceProject>(CamelCase) ?? new WorkspaceProject("", "", null, false, null, null, null)
            : new WorkspaceProject("", "", null, false, null, null, null);
        if (!string.IsNullOrEmpty(projectId))
            project = project with { Id = projectId };
        return project;
    }

    // ── the store write mapping ──

    private async Task WriteToStoreAsync(MetadataKind kind, string projectId, JsonElement element, CancellationToken ct)
    {
        switch (kind)
        {
            case MetadataKind.Collections:
                await ReconcileAsync(element,
                    upsert: (id, it) => _networkStore.UpsertCollectionAsync(
                        projectId, id, ItemString(it, "name") ?? "", ItemString(it, "visibility") ?? "private", it, 1, ct),
                    listExisting: () => _networkStore.ListCollectionsAsync(projectId, ct),
                    existingId: row => ColumnString(row, "collection_id"),
                    delete: id => _networkStore.DeleteCollectionAsync(projectId, id, ct));
                break;
            case MetadataKind.Workflows:
                await ReconcileAsync(element,
                    upsert: (id, it) => _networkStore.UpsertWorkflowAsync(
                        projectId, id, ItemString(it, "name") ?? "", it, ItemString(it, "versionHash"), 1, ct),
                    listExisting: () => _networkStore.ListWorkflowsAsync(projectId, ct),
                    existingId: row => ColumnString(row, "workflow_id"),
                    delete: id => _networkStore.DeleteWorkflowAsync(projectId, id, ct));
                break;
            case MetadataKind.Queries:
                await ReconcileAsync(element,
                    upsert: (id, it) => _networkStore.UpsertQueryAsync(
                        projectId, id, ItemString(it, "name") ?? "", ItemBool(it, "requiresSecretKey"), it, 1, ct),
                    listExisting: () => _networkStore.ListQueriesAsync(projectId, ct),
                    existingId: row => ColumnString(row, "query_id"),
                    delete: id => _networkStore.DeleteQueryAsync(projectId, id, ct));
                break;
            case MetadataKind.Endpoints:
                await ReconcileAsync(element,
                    upsert: (id, it) => _networkStore.UpsertEndpointAsync(
                        projectId, id, ItemString(it, "slug") ?? "", ItemString(it, "method") ?? "GET",
                        ItemBool(it, "enabled"), it, ItemString(it, "versionHash"), 1, ct),
                    listExisting: () => _networkStore.ListEndpointsAsync(projectId, ct),
                    existingId: row => ColumnString(row, "endpoint_id"),
                    delete: id => _networkStore.DeleteEndpointAsync(projectId, id, ct));
                break;
            case MetadataKind.GameValues:
                if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    await _networkStore.UpsertGameValuesAsync(projectId, element, null, 1, ct);
                break;
            case MetadataKind.RateLimitRules:
                if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    await _networkStore.UpsertRateLimitRulesAsync(projectId, element, 1, ct);
                break;
        }
    }

    private async Task WritePagesIndexAsync(string projectId, JsonElement element, CancellationToken ct)
    {
        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var slug = ItemString(item, "slug") ?? ItemString(item, "id");
            if (string.IsNullOrEmpty(slug)) continue;
            var title = ItemString(item, "title") ?? ItemString(item, "name") ?? slug;
            var contentJson = item.GetRawText();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _networkStore.UpsertPageAsync(projectId, slug, title, contentJson, now, now, ct);
        }
    }

    private async Task WritePageContentAsync(string resourcePath, string projectId, JsonElement element, CancellationToken ct)
    {
        var slug = ExtractPageSlug(resourcePath);
        if (slug is null) return;
        var title = ItemString(element, "title") ?? ItemString(element, "name") ?? slug;
        var contentJson = element.GetRawText();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _networkStore.UpsertPageAsync(projectId, slug, title, contentJson, now, now, ct);
    }

    private static async Task ReconcileAsync(
        JsonElement element,
        Func<string, JsonElement, Task> upsert,
        Func<Task<IReadOnlyList<JsonElement>>> listExisting,
        Func<JsonElement, string?> existingId,
        Func<string, Task> delete)
    {
        if (element.ValueKind != JsonValueKind.Array) return;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ItemString(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            keep.Add(id);
            await upsert(id, item);
        }
        foreach (var row in await listExisting())
        {
            var id = existingId(row);
            if (id is not null && !keep.Contains(id))
                await delete(id);
        }
    }

    // ── Classification ──

    private static MetadataKind Classify(string resourcePath) => resourcePath switch
    {
        "collections.json" => MetadataKind.Collections,
        "workflows.json" => MetadataKind.Workflows,
        "queries.json" => MetadataKind.Queries,
        "endpoints.json" => MetadataKind.Endpoints,
        "game-values.json" => MetadataKind.GameValues,
        "rate-limit-rules.json" => MetadataKind.RateLimitRules,
        "pages.json" => MetadataKind.Pages,
        _ => MetadataKind.None,
    };

    [GeneratedRegex(@"^pages/(.+)\.json$", RegexOptions.None, 100)]
    private static partial Regex PageContentPath();

    private static MetadataKind ClassifySubpath(string resourcePath)
        => PageContentPath().IsMatch(resourcePath) ? MetadataKind.PageContent : MetadataKind.None;

    private static string? ExtractPageSlug(string resourcePath)
    {
        var m = PageContentPath().Match(resourcePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── Absolute raw path parsing (for DeleteRawAsync page content interception) ──

    [GeneratedRegex(@"^network-storage/users/\d+/([^/]+)/pages/(.+)\.json$", RegexOptions.None, 100)]
    private static partial Regex RawPageContentPath();

    private static (string? ProjectId, string? Slug) TryParsePageContentPath(string absolutePath)
    {
        var m = RawPageContentPath().Match(absolutePath);
        if (!m.Success) return (null, null);
        return (m.Groups[1].Value, m.Groups[2].Value);
    }
    // ── Helpers ──

    private static string? ItemString(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool ItemBool(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? ColumnString(JsonElement row, string column)
        => row.TryGetProperty(column, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private enum MetadataKind
    {
        None,
        Collections,
        Workflows,
        Queries,
        Endpoints,
        GameValues,
        RateLimitRules,
        Pages,
        PageContent,
    }
}
