using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Middleware;


namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Native .NET Network Storage query API endpoints — the cutover of the legacy server
/// <c>controllers/queries-controller.js</c> <c>routeQueryApi</c> handler to
/// ASP.NET Core. Serves <c>GET /v3/queries/{projectId}/{queryId}</c> (and the
/// <c>/v1</c> + <c>/api/storage</c> aliases) directly via
/// <see cref="NativeQueryExecutor"/> over the store, so query traffic no longer
/// proxies to the legacy server storage runtime.
///
/// <para>Wire-contract parity with legacy server: success responses are HTTP 200 with
/// <c>{ ok, query, queryId, queryName, generatedAt, lastran, ttl, updatedAt,
/// ...resultFields }</c>; errors use HTTP status codes + <c>{ error: { code,
/// message } }</c> matching the legacy server handler exactly.</para>
/// </summary>
public static class QueryEndpoints
{
    private const string Wiki = "https://sboxcool.com/wiki/network-storage-v3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // preserve casing for the wire contract
        WriteIndented = false
    };

    // Route prefixes: /v3/queries, /v1/queries, /api/storage/{projectId}/queries
    private static readonly string[] V3V1Prefixes = ["/v3/queries", "/v1/queries"];

    public static IEndpointRouteBuilder MapQueries(this IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in V3V1Prefixes)
        {
            endpoints.MapGet($"{prefix}/{{projectId}}/{{queryId}}", ExecuteQueryAsync)
                .WithDisplayName($"Network Storage query API ({prefix})");
        }

        // /api/storage/{projectId}/queries/{queryId} alias
        endpoints.MapGet("/api/storage/{projectId}/queries/{queryId}", ExecuteQueryAsync)
            .WithDisplayName("Network Storage query API (api/storage alias)");

        return endpoints;
    }

    internal static async Task ExecuteQueryAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var queryId = (string?)context.GetRouteValue("queryId") ?? "";

        // ── Auth: apiKey resolution ──
        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Missing API key.");
            return;
        }

        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null || !auth.Enabled)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid API key.");
            return;
        }

        // ── Project access ──
        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid API key.");
            return;
        }

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

        if (!access.Project.Enabled)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status403Forbidden, "DISABLED", "Project disabled.");
            return;
        }

        // ── Secret-key resolution + permission check ──
        // Mirror resolveEndpointSecretKey: if the primary apiKey is a secret key
        // (sbox_sk_), it serves as the secret key. Otherwise, check x-secret-key
        // header / secretKey query param.
        var endpointSecret = await ResolveEndpointSecretKeyAsync(context, apiKey, auth, projectId, context.RequestAborted);
        if (!endpointSecret.Ok)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status401Unauthorized, endpointSecret.Code ?? "SECRET_KEY_INVALID", "Invalid secret key.");
            return;
        }

        // ── Read the query definition from the store ──
        var networkStore = context.RequestServices.GetRequiredService<INetworkStorageStore>();
        var queryRow = await networkStore.ReadQueryAsync(projectId, queryId, context.RequestAborted);
        if (!queryRow.HasValue)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status404NotFound, "NOT_FOUND", "Query not found.");
            return;
        }

        var query = queryRow.Value;
        var requiresSecretKey = query.TryGetProperty("requires_secret_key", out var rsk) && rsk.ValueKind == JsonValueKind.True;
        var queryName = query.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? queryId
            : queryId;

        if (requiresSecretKey && !endpointSecret.HasSecretKey)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status401Unauthorized, "SECRET_KEY_REQUIRED", "This query requires a Network Storage secret key. Send it in the x-secret-key header.");
            return;
        }

        // Permission check: secret key must have queries:x
        var secretMustAuthorizeQuery = requiresSecretKey || endpointSecret.Source == "x-api-key";
        if (secretMustAuthorizeQuery && endpointSecret.HasSecretKey && !HasQueryExecutePermission(endpointSecret.Permissions))
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status403Forbidden, "FORBIDDEN", "This secret key does not have execute access to queries.");
            return;
        }

        // Phase 4 (game-version-policy): opt-in server enforcement (GET reads
        // are blocked only under block_all), pinned snapshot definitions and
        // _revisionStatus. Fail-open: lookup failures keep live behavior.
        long? revisionClientId = ClientIdentityContext.Get(context).RevisionId;
        RevisionSnapshots.SnapshotDocument? pinnedSnapshot = null;
        Dictionary<string, object?>? revisionStatus = null;
        try
        {
            var workspace = context.RequestServices.GetService<IWorkspaceStore>();
            var memoryCache = context.RequestServices.GetService<IMemoryCache>();
            if (workspace is not null && memoryCache is not null && VersionPolicyEvaluator.HasExplicitPolicy(access.Project))
            {
                var snapshotCache = new RevisionSnapshotCache(memoryCache);
                if (access.Project.RevisionServerEnforcementEnabled == true && !endpointSecret.HasSecretKey)
                {
                    var denial = await VersionPolicyEnforcement.CheckAsync(
                        access.Project, auth.UserId, projectId, revisionClientId, isSecretCaller: false,
                        isMutating: false,
                        workspace, snapshotCache, DateTimeOffset.UtcNow, context.RequestAborted);
                    if (denial is not null)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsJsonAsync(VersionPolicyEnforcement.DenialBody(denial), JsonOptions, context.RequestAborted);
                        return;
                    }
                }
                var index = await RevisionSnapshots.ReadIndexAsync(workspace, auth.UserId, projectId, context.RequestAborted);
                var decision = VersionPolicyEvaluator.Evaluate(access.Project, index, revisionClientId, DateTimeOffset.UtcNow, index.LiveRevisionId);
                if (decision.IsOutdated && !decision.GraceExpired && revisionClientId.HasValue)
                    pinnedSnapshot = await snapshotCache.GetAsync(workspace, auth.UserId, projectId, revisionClientId.Value, context.RequestAborted);
                revisionStatus = RevisionStatusInjector.Build(access.Project, decision, revisionClientId);
                if (revisionStatus is not null) context.Items["RevisionStatus"] = revisionStatus;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        catch { /* fail open */ }

         // ── Determine live vs cached ──
        // Without ?cache=1 the response keeps the live shape (no-store, no cache fields). Only a secret key
        // may actually force a fresh run: the public key ships in every game build, so a public caller is
        // always served from the shared cache, which runs the query at most once per short interval.
        var wantsLive = WantsLiveResult(context.Request.Query) || !WantsCachedResult(context.Request.Query);
        var runLive = wantsLive && endpointSecret.HasSecretKey;

        // ── Build the values context (game-values + collections) ──
        var valuesProvider = context.RequestServices.GetService<IQueryValuesContextProvider>();
        IReadOnlyDictionary<string, object?>? values = null;
        try
        {
            if (pinnedSnapshot is not null && context.RequestServices.GetService<StoreQueryValuesContextProvider>() is { } concreteValues)
                values = await concreteValues.WithRevisionSnapshot(pinnedSnapshot).GetValuesAsync(projectId, context.RequestAborted);
            else if (valuesProvider is not null)
                values = await valuesProvider.GetValuesAsync(projectId, context.RequestAborted);
        }
        catch { /* best-effort — values context is optional */ }

        // ── Execute the query natively ──
        var executor = context.RequestServices.GetRequiredService<NativeQueryExecutor>();
        QueryResult? result;
        try
        {
            // Pinned execution: the old build's query definition runs over live
            // records. Bypass the shared-query cache so a pinned run never
            // poisons (or reads) another revision's cached result.
            if (pinnedSnapshot is not null && SnapshotQueryDefinition(pinnedSnapshot, queryId) is { } pinnedQuery)
                result = await executor.ExecuteWithQueryAsync(projectId, queryId, pinnedQuery, values, bypassCache: true, context.RequestAborted);
            else
                result = await executor.ExecuteAsync(projectId, queryId, values, bypassCache: runLive, context.RequestAborted);
        }
         catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // client disconnected
        }
        catch (Exception)
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status500InternalServerError, "QUERY_FAILED", "Query execution failed.");
            return;
        }

        if (result is null)
        {
            // Query not found in the store (definition_json missing or empty).
            await QueryErrorAsync(context, requestId, StatusCodes.Status404NotFound, "NOT_FOUND", "Query not found.");
            return;
        }

        if (result.Type == "error")
        {
            await QueryErrorAsync(context, requestId, StatusCodes.Status500InternalServerError, "QUERY_FAILED", result.Message ?? "Query execution failed.");
            return;
        }

        // ── Build the API output (mirror buildQueryApiOutput) ──
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var ttlSeconds = ReadCacheTtl(query);
        var lastran = result.Performance?.At
            ?? result.CachedAt
            ?? timestamp;
        var updatedAt = timestamp;

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["query"] = queryName,
            ["queryId"] = queryId,
            ["queryName"] = queryName,
            ["generatedAt"] = timestamp,
            ["lastran"] = lastran,
            ["ttl"] = ttlSeconds,
            ["updatedAt"] = updatedAt,
        };

        AddResultFields(body, result, includeCacheFields: !wantsLive);

        // _revisionStatus rides the query body when a policy is configured;
        // absent otherwise, so legacy bodies are unchanged.
        if (context.Items["RevisionStatus"] is Dictionary<string, object?> queryStatus)
            body["_revisionStatus"] = queryStatus;

        // Cache-control header for live-shaped results.
        if (wantsLive)
        {
            context.Response.Headers["Cache-Control"] = "no-store, max-age=0";
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(body, JsonOptions, context.RequestAborted);
    }

    /// <summary>
    /// The pinned revision's query definition from the snapshot row
    /// (<c>definition_json</c> may be text or an object). Null when the
    /// snapshot does not contain the query — the caller falls back to live.
    /// </summary>
    private static JsonElement? SnapshotQueryDefinition(RevisionSnapshots.SnapshotDocument snapshot, string queryId)
    {
        foreach (var row in snapshot.Queries)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = row.TryGetProperty("query_id", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()
                ?? (row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()
                ?? (row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null) : null) : null;
            if (!string.Equals(id, queryId, StringComparison.Ordinal)) continue;
            if (row.TryGetProperty("definition_json", out var def))
            {
                if (def.ValueKind == JsonValueKind.Object) return def;
                if (def.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(def.GetString()))
                {
                    try { return JsonDocument.Parse(def.GetString()!).RootElement.Clone(); }
                    catch (JsonException) { return null; }
                }
            }
            return row;
        }
        return null;
    }

    // ── Output builder (mirror addQueryResultFields) ──

    private static void AddResultFields(Dictionary<string, object?> output, QueryResult result, bool includeCacheFields)
    {
        if (result.Type is not null) output["type"] = result.Type;
        if (result.Field is not null) output["field"] = result.Field;
        if (result.FieldLabel is not null) output["fieldLabel"] = result.FieldLabel;
        if (result.OutputFields is not null) output["outputFields"] = result.OutputFields;
        if (result.Columns is not null) output["columns"] = result.Columns;
        if (result.Entries is not null) output["entries"] = result.Entries;
        if (result.Count.HasValue) output["count"] = result.Count;
        if (result.Sum.HasValue) output["sum"] = result.Sum;
        if (result.Average.HasValue) output["average"] = result.Average;
        if (result.Value is not null) output["value"] = result.Value;
        if (result.Counted.HasValue) output["counted"] = result.Counted;
        if (result.Performance is not null) output["performance"] = result.Performance;
        if (!includeCacheFields) return;
        if (result.FromCache) output["fromCache"] = true;
        if (result.CachedAt is not null) output["cachedAt"] = result.CachedAt;
        if (result.ExpiresAt is not null) output["expiresAt"] = result.ExpiresAt;
    }

    // ── Auth helpers ──

    private static string? ExtractApiKey(HttpRequest request)
    {
        var headerKey = request.Headers.TryGetValue("x-api-key", out var hk) && !string.IsNullOrWhiteSpace(hk)
            ? hk.ToString() : null;
        if (headerKey is not null) return headerKey;
        if (request.Query.TryGetValue("apiKey", out var qk) && !string.IsNullOrWhiteSpace(qk))
            return qk.ToString();
        return null;
    }

    private static async Task<SecretKeyResult> ResolveEndpointSecretKeyAsync(
        HttpContext context, string primaryApiKey, StorageApiKeyAuthResult? auth, string projectId, CancellationToken ct)
    {
        // If the primary apiKey is a secret key, it serves as the secret key.
        if (primaryApiKey.StartsWith("sbox_sk_") && auth is { KeyType: "secret" })
        {
            return new SecretKeyResult(true, true, "x-api-key", auth.Permissions);
        }

        // Check x-secret-key header / secretKey query param.
        var secret = ReadSecretKey(context.Request);
        if (string.IsNullOrEmpty(secret))
            return new SecretKeyResult(true, false, "", null);

        // Resolve the secret key via the apiKey resolver.
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var keyData = await resolver.ResolveApiKeyAsync(secret, projectId, ct);
        if (keyData is null || keyData.KeyType != "secret" || !keyData.Enabled)
            return new SecretKeyResult(false, false, "SECRET_KEY_INVALID", null);

        return new SecretKeyResult(true, true, "x-secret-key", keyData.Permissions);
    }

    private static string? ReadSecretKey(HttpRequest request)
    {
        foreach (var name in s_secretKeyHeaders)
        {
            if (request.Headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.ToString();
        }
        foreach (var name in s_secretKeyQueryParams)
        {
            if (request.Query.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.ToString();
        }
        return null;
    }

    private static readonly string[] s_secretKeyHeaders =
        ["x-secret-key", "x-network-storage-secret", "x-storage-secret-key", "x-sbox-secret-key"];
    private static readonly string[] s_secretKeyQueryParams = ["secretKey", "networkStorageSecret"];

    /// <summary>Check if the secret key has queries:x permission (mirror checkPermission).</summary>
    private static bool HasQueryExecutePermission(Dictionary<string, string>? permissions)
    {
        if (permissions is null) return true; // null = full access
        if (!permissions.TryGetValue("queries", out var access)) return false;
        if (access == "none") return false;
        return access.Contains('x');
    }

    // ── Live/cached helpers (mirror wantsLiveQueryResult / wantsCachedQueryResult) ──

    private static bool WantsLiveResult(IQueryCollection query)
    {
        foreach (var key in s_liveKeys)
        {
            if (query.TryGetValue(key, out var v))
            {
                var val = v.ToString().Trim().ToLowerInvariant();
                if (s_trueValues.Contains(val)) return true;
            }
        }
        if (query.TryGetValue("cache", out var cv))
        {
            var val = cv.ToString().Trim().ToLowerInvariant();
            if (s_cacheBypassValues.Contains(val)) return true;
        }
        return false;
    }

    private static bool WantsCachedResult(IQueryCollection query)
    {
        if (query.TryGetValue("cache", out var cv))
        {
            var val = cv.ToString().Trim().ToLowerInvariant();
            return s_trueValues.Contains(val);
        }
        return false;
    }

    private static readonly string[] s_liveKeys = ["fresh", "live", "noCache"];
    private static readonly HashSet<string> s_trueValues = new(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };
    private static readonly HashSet<string> s_cacheBypassValues = new(StringComparer.OrdinalIgnoreCase) { "0", "false", "no", "off", "none", "bypass" };

    // ── Error helper ──

    private static async Task QueryErrorAsync(HttpContext context, string requestId, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        object body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code, ["message"] = message },
        };
        if (context.Items["RevisionStatus"] is Dictionary<string, object?> errorStatus)
            body = RevisionStatusInjector.WithStatus(body, errorStatus);
        await context.Response.WriteAsJsonAsync(body, JsonOptions, context.RequestAborted);
    }

    // ── Cache TTL ──

    private static int ReadCacheTtl(JsonElement query)
    {
        if (query.TryGetProperty("cache", out var cacheEl) && cacheEl.ValueKind == JsonValueKind.Object)
        {
            if (cacheEl.TryGetProperty("ttlSeconds", out var ttlEl) && ttlEl.TryGetInt32(out var ttl))
                return ttl;
        }
        // Also check definition_json for cache config.
        if (query.TryGetProperty("definition_json", out var defEl))
        {
            var def = defEl.ValueKind == JsonValueKind.String
                ? SafeParse(defEl.GetString()!)
                : defEl;
            if (def.ValueKind == JsonValueKind.Object && def.TryGetProperty("cache", out var dcEl) && dcEl.ValueKind == JsonValueKind.Object)
            {
                if (dcEl.TryGetProperty("ttlSeconds", out var ttlEl) && ttlEl.TryGetInt32(out var ttl))
                    return ttl;
            }
        }
        return 300;
    }

    private static JsonElement SafeParse(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return default; }
    }

    // ── Secret key result ──

    private sealed record SecretKeyResult(bool Ok, bool HasSecretKey, string Source, Dictionary<string, string>? Permissions)
    {
        public string? Code => Source == "SECRET_KEY_INVALID" ? "SECRET_KEY_INVALID" : null;
    }
}

