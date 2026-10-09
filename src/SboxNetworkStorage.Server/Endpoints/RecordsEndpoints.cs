using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Middleware;


namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Native .NET Network Storage player-records (save-slot index) endpoints — the
/// cutover of the legacy server <c>controllers/storage-modules/api-routes.js</c> handlers
/// (<c>routeStorageApiListRecords</c>, <c>routeStorageApiCreateRecord</c>,
/// <c>routeStorageApiDeleteRecord</c>, <c>routeStorageApiRenameRecord</c>) to
/// ASP.NET Core. Serves the save-slot index CRUD
/// (<c>GET/POST /v3/storage/{projectId}/{collectionId}/{steamId}/records</c>,
/// <c>DELETE/PATCH /v3/storage/{projectId}/{collectionId}/{steamId}/records/{recordId}</c>,
/// plus the <c>/v1/storage</c> + <c>/api/storage</c> aliases) directly over the
/// workspace metadata store (<c>IWorkspaceStore</c>) and the record data
/// plane (<c>INetworkStorageDataPlane</c>), so save-slot traffic no longer
/// proxies to the legacy server storage runtime.
///
/// <para>Wire-contract parity with legacy server: success responses are HTTP 200 with the
/// legacy server body shapes; errors use HTTP status codes + <c>{ error: { code, message,
/// docsUrl } }</c> matching the legacy server <c>errorResponse</c> / <c>ERRORS</c> map.</para>
/// </summary>
public static partial class RecordsEndpoints
{
    private const string Wiki = "https://sboxcool.com/wiki/network-storage-v3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // preserve casing for the wire contract
        WriteIndented = false
    };

    // Route prefixes: /v3/storage, /v1/storage, /api/storage
    private static readonly string[] V3V1Prefixes = ["/v3/storage", "/v1/storage"];

    /// <summary>
    /// Error code → (HTTP status, default message, docs URL). Mirrors the legacy server
    /// <c>ERRORS</c> map in <c>controllers/storage-shared.js</c> for the codes
    /// these handlers emit.
    /// </summary>
    private static readonly Dictionary<string, (int Status, string Message, string DocsUrl)> Errors = new(StringComparer.Ordinal)
    {
        ["UNAUTHORIZED"] = (401, "Invalid or missing API key.", $"{Wiki}/quick-start#3-configure-the-library"),
        ["PROJECT_DISABLED"] = (403, "This project is currently disabled.", $"{Wiki}/quick-start#2-create-a-project"),
        ["FORBIDDEN"] = (403, "This secret key does not have execute access to collections.", $"{Wiki}/sync-tools#key-permissions"),
        ["ENDPOINT_ONLY"] = (403, "Direct collection access requires a secret key. Route game-client actions through endpoints or queries.", $"{Wiki}/collections#access-model"),
        ["NOT_FOUND"] = (404, "Collection not found.", $"{Wiki}/error-codes#collection-errors"),
        ["INVALID_KEY"] = (400, "Key must be alphanumeric, hyphens, or underscores (max 128 chars).", $"{Wiki}/collections#creating-a-collection"),
        ["INVALID_BODY"] = (400, "Body must be a JSON object.", $"{Wiki}/endpoints#request-body"),
        ["RECORD_LIMIT_REACHED"] = (409, "Maximum number of records reached for this player.", $"{Wiki}/collections#save-slots"),
        ["RECORD_NOT_FOUND"] = (404, "Record not found.", $"{Wiki}/collections#save-slots"),
        ["RECORD_DELETE_DISABLED"] = (403, "Record deletion is not enabled for this collection.", $"{Wiki}/collections#save-slots"),
    };

    public static IEndpointRouteBuilder MapRecords(this IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in V3V1Prefixes)
        {
            endpoints.MapGet($"{prefix}/{{projectId}}/{{collectionId}}/{{steamId}}/records", ListRecordsAsync)
                .WithDisplayName($"Network Storage records list ({prefix})");
            endpoints.MapPost($"{prefix}/{{projectId}}/{{collectionId}}/{{steamId}}/records", CreateRecordAsync)
                .WithDisplayName($"Network Storage records create ({prefix})");
            endpoints.MapDelete($"{prefix}/{{projectId}}/{{collectionId}}/{{steamId}}/records/{{recordId}}", DeleteRecordAsync)
                .WithDisplayName($"Network Storage records delete ({prefix})");
            endpoints.MapPatch($"{prefix}/{{projectId}}/{{collectionId}}/{{steamId}}/records/{{recordId}}", RenameRecordAsync)
                .WithDisplayName($"Network Storage records rename ({prefix})");
        }

        // /api/storage/{projectId}/{collectionId}/{steamId}/records aliases
        endpoints.MapGet("/api/storage/{projectId}/{collectionId}/{steamId}/records", ListRecordsAsync)
            .WithDisplayName("Network Storage records list (api/storage alias)");
        endpoints.MapPost("/api/storage/{projectId}/{collectionId}/{steamId}/records", CreateRecordAsync)
            .WithDisplayName("Network Storage records create (api/storage alias)");
        endpoints.MapDelete("/api/storage/{projectId}/{collectionId}/{steamId}/records/{recordId}", DeleteRecordAsync)
            .WithDisplayName("Network Storage records delete (api/storage alias)");
        endpoints.MapPatch("/api/storage/{projectId}/{collectionId}/{steamId}/records/{recordId}", RenameRecordAsync)
            .WithDisplayName("Network Storage records rename (api/storage alias)");

        return endpoints;
    }

    // ── GET: list records ──

    internal static async Task ListRecordsAsync(HttpContext context)
    {
        var (ok, ownerUserId, projectId, collection, error) = await ResolveAuthAsync(context);
        if (!ok) { await ErrorAsync(context, error!); return; }

        var steamId = (string?)context.GetRouteValue("steamId") ?? "";
        if (!IsValidSteamId(steamId)) { await ErrorAsync(context, "INVALID_KEY"); return; }

        var index = await ReadRecordIndexAsync(context, ownerUserId, projectId, collection!.Id, steamId);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            records = index.Records,
            maxRecords = collection.MaxRecords > 0 ? collection.MaxRecords : 1
        }, JsonOptions, context.RequestAborted);
    }

    // ── POST: create record ──

    internal static async Task CreateRecordAsync(HttpContext context)
    {
        var (ok, ownerUserId, projectId, collection, error) = await ResolveAuthAsync(context);
        if (!ok) { await ErrorAsync(context, error!); return; }

        var steamId = (string?)context.GetRouteValue("steamId") ?? "";
        if (!IsValidSteamId(steamId)) { await ErrorAsync(context, "INVALID_KEY"); return; }

        var maxRecords = collection!.MaxRecords > 0 ? collection.MaxRecords : 1;

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            body = default;
        }

        var recordName = "Save";
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("recordName", out var rnEl) && rnEl.ValueKind == JsonValueKind.String)
        {
            var name = rnEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(name)) recordName = name.Length > 64 ? name[..64] : name;
        }

        var index = await ReadRecordIndexAsync(context, ownerUserId, projectId, collection.Id, steamId);
        if (index.Records.Count >= maxRecords)
        {
            await ErrorAsync(context, "RECORD_LIMIT_REACHED", $"Maximum {maxRecords} record(s) per player. Delete an existing record first.");
            return;
        }

        var recordId = GenerateRecordId();
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        index.Records.Add(new RecordIndexEntry(recordId, recordName, now, now, false));
        await WriteRecordIndexAsync(context, ownerUserId, projectId, collection.Id, steamId, index);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { ok = true, recordId, recordName }, JsonOptions, context.RequestAborted);
    }

    // ── DELETE: delete record ──

    internal static async Task DeleteRecordAsync(HttpContext context)
    {
        var (ok, ownerUserId, projectId, collection, error) = await ResolveAuthAsync(context);
        if (!ok) { await ErrorAsync(context, error!); return; }

        var steamId = (string?)context.GetRouteValue("steamId") ?? "";
        var recordId = (string?)context.GetRouteValue("recordId") ?? "";
        if (!IsValidSteamId(steamId) || string.IsNullOrEmpty(recordId)) { await ErrorAsync(context, "INVALID_KEY"); return; }

        if (!collection!.AllowRecordDelete) { await ErrorAsync(context, "RECORD_DELETE_DISABLED"); return; }

        var index = await ReadRecordIndexAsync(context, ownerUserId, projectId, collection.Id, steamId);
        var entry = index.Records.Find(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
        if (entry is null) { await ErrorAsync(context, "RECORD_NOT_FOUND"); return; }

        // Determine the data key for this record (mirror legacy server: legacy = steamId, else steamId_recordId).
        var dataKey = entry.IsLegacy ? steamId : $"{steamId}_{recordId}";

        // Delete the underlying save data via the data plane.
        var dataPlane = context.RequestServices.GetRequiredService<INetworkStorageDataPlane>();
        try
        {
            await dataPlane.DeleteRecordAsync(ownerUserId, projectId, collection.Id, dataKey, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Best-effort — the index entry is still removed (matching legacy server, which
            // wraps deleteStoredRecord in try/catch).
        }

        index.Records.RemoveAll(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
        await WriteRecordIndexAsync(context, ownerUserId, projectId, collection.Id, steamId, index);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { ok = true }, JsonOptions, context.RequestAborted);
    }

    // ── PATCH: rename record ──

    internal static async Task RenameRecordAsync(HttpContext context)
    {
        var (ok, ownerUserId, projectId, collection, error) = await ResolveAuthAsync(context);
        if (!ok) { await ErrorAsync(context, error!); return; }

        var steamId = (string?)context.GetRouteValue("steamId") ?? "";
        var recordId = (string?)context.GetRouteValue("recordId") ?? "";
        if (!IsValidSteamId(steamId) || string.IsNullOrEmpty(recordId)) { await ErrorAsync(context, "INVALID_KEY"); return; }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            body = default;
        }

        var recordName = "";
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("recordName", out var rnEl) && rnEl.ValueKind == JsonValueKind.String)
        {
            recordName = rnEl.GetString()?.Trim() ?? "";
            if (recordName.Length > 64) recordName = recordName[..64];
        }
        if (string.IsNullOrEmpty(recordName)) { await ErrorAsync(context, "INVALID_BODY", "recordName is required."); return; }

        var index = await ReadRecordIndexAsync(context, ownerUserId, projectId, collection!.Id, steamId);
        var entry = index.Records.Find(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
        if (entry is null) { await ErrorAsync(context, "RECORD_NOT_FOUND"); return; }

        var updated = entry with { RecordName = recordName };
        index.Records.RemoveAll(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
        index.Records.Add(updated);
        await WriteRecordIndexAsync(context, ownerUserId, projectId, collection.Id, steamId, index);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { ok = true, recordId, recordName }, JsonOptions, context.RequestAborted);
    }

    // ── Auth resolution ──

    /// <summary>Resolved auth context: ownerUserId + projectId + collection on success, error code on failure.</summary>
    private readonly record struct AuthResult(
        bool Ok,
        long OwnerUserId,
        string ProjectId,
        CollectionResource? Collection,
        string? Error);

    private static async Task<AuthResult> ResolveAuthAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
            return Fail("UNAUTHORIZED");

        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null || !auth.Enabled)
            return Fail("UNAUTHORIZED");

        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
            return Fail("UNAUTHORIZED");

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

        if (!access.Project.Enabled)
            return Fail("PROJECT_DISABLED");

        // Permission check: secret keys must have collections:x; public keys are
        // ENDPOINT_ONLY (direct collection data API requires a secret key).
        if (string.Equals(auth.KeyType, "secret", StringComparison.Ordinal))
        {
            if (!ApiKeyPermissionPolicy.HasPermission(auth, "collections", "x"))
                return Fail("FORBIDDEN");
        }
        else
        {
            return Fail("ENDPOINT_ONLY");
        }

        var resources = await projectService.GetProjectResourcesForOwnerAsync(access.StorageOwnerUserId, projectId, context.RequestAborted);
        if (resources is null)
            return Fail("NOT_FOUND");

        var collection = FindCollection(resources.Collections, collectionId);
        if (collection is null)
            return Fail("NOT_FOUND");

        return new AuthResult(true, access.StorageOwnerUserId, projectId, collection, null);

        static AuthResult Fail(string error) => new(false, 0, "", null, error);
    }

    private static string? ExtractApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("x-api-key", out var hk) && !string.IsNullOrWhiteSpace(hk))
            return hk.ToString();
        if (request.Query.TryGetValue("apiKey", out var qk) && !string.IsNullOrWhiteSpace(qk))
            return qk.ToString();
        return null;
    }

    // ── Record index I/O ──

    private const string IndexFileTemplate = "{0}/data/{1}/record-index.json";

    private static async Task<RecordIndex> ReadRecordIndexAsync(
        HttpContext context, long ownerUserId, string projectId, string collectionId, string steamId)
    {
        var workspace = context.RequestServices.GetRequiredService<IWorkspaceStore>();
        var relativePath = string.Format(System.Globalization.CultureInfo.InvariantCulture, IndexFileTemplate, collectionId, steamId);

        RecordIndexData? data;
        try
        {
            data = await workspace.GetProjectResourceAsync<RecordIndexData>(ownerUserId, projectId, relativePath, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            data = null;
        }

        if (data is { Records: { } records })
            return new RecordIndex(records);

        // No index yet — legacy server would check for legacy data; we return empty (the
        // dashboard/index-creation path handles legacy migration on write).
        return new RecordIndex(new List<RecordIndexEntry>());
    }

    private static async Task WriteRecordIndexAsync(
        HttpContext context, long ownerUserId, string projectId, string collectionId, string steamId, RecordIndex index)
    {
        var workspace = context.RequestServices.GetRequiredService<IWorkspaceStore>();
        var relativePath = string.Format(System.Globalization.CultureInfo.InvariantCulture, IndexFileTemplate, collectionId, steamId);
        var data = new RecordIndexData { Records = index.Records };
        await workspace.PutProjectResourceAsync(ownerUserId, projectId, relativePath, data, context.RequestAborted);
    }

    // ── Helpers ──

    private static bool IsValidSteamId(string? steamId)
        => !string.IsNullOrEmpty(steamId) && SteamIdPattern().IsMatch(steamId);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-zA-Z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.None, 100)]
    private static partial System.Text.RegularExpressions.Regex SteamIdPattern();

    /// <summary>Generate a 6-hex record id (mirror legacy server <c>generateRecordId</c>).</summary>
    private static string GenerateRecordId()
        => Guid.NewGuid().ToString("N")[..6];

    private static CollectionResource? FindCollection(IReadOnlyList<CollectionResource> collections, string collectionId)
    {
        foreach (var c in collections)
        {
            if (string.Equals(c.Id, collectionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }

    private static async Task ErrorAsync(HttpContext context, string code, string? detail = null)
    {
        var (status, message, docsUrl) = Errors[code];
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var errorObj = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["message"] = detail ?? message,
        };
        if (!string.IsNullOrEmpty(docsUrl)) errorObj["docsUrl"] = docsUrl;
        await context.Response.WriteAsJsonAsync(new { error = errorObj }, JsonOptions, context.RequestAborted);
    }

    // ── In-memory index models ──

    private sealed record RecordIndex(List<RecordIndexEntry> Records);

    /// <summary>Serialization shape for <c>record-index.json</c> (camelCase, matching legacy server).</summary>
    private sealed class RecordIndexData
    {
        public List<RecordIndexEntry> Records { get; set; } = new();
    }

    /// <summary>Serialization shape for a single record index entry (camelCase, matching legacy server).</summary>
    private sealed record RecordIndexEntry(
        string RecordId,
        string RecordName,
        string CreatedAt,
        string UpdatedAt,
        bool IsLegacy);
}

