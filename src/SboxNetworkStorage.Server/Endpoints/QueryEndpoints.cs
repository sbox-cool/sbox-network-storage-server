using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Middleware;


namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Native .NET Network Storage query API endpoints — the cutover of the Bun
/// <c>controllers/queries-controller.js</c> <c>routeQueryApi</c> handler to
/// ASP.NET Core. Serves <c>GET /v3/queries/{projectId}/{queryId}</c> (and the
/// <c>/v1</c> + <c>/api/storage</c> aliases) directly via
/// <see cref="NativeQueryExecutor"/> over ScyllaDB, so query traffic no longer
/// proxies to the legacy Bun storage runtime.
///
/// <para>Wire-contract parity with Bun: success responses are HTTP 200 with
/// <c>{ ok, query, queryId, queryName, generatedAt, lastran, ttl, updatedAt,
/// ...resultFields }</c>; errors use HTTP status codes + <c>{ error: { code,
/// message } }</c> matching the Bun handler exactly.</para>
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
        if (auth is null)
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

        // ── Read the query definition from ScyllaDB ──
        var scyllaStore = context.RequestServices.GetRequiredService<INetworkStorageStore>();
        var queryRow = await scyllaStore.ReadQueryAsync(projectId, queryId, context.RequestAborted);
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

        // ── Determine live vs cached ──
        var liveResult = WantsLiveResult(context.Request.Query) || !WantsCachedResult(context.Request.Query);

        // ── Build the values context (game-values + collections) ──
        var valuesProvider = context.RequestServices.GetRequiredService<IQueryValuesContextProvider>();
        IReadOnlyDictionary<string, object?>? values = null;
        try
        {
            values = await valuesProvider.GetValuesAsync(projectId, context.RequestAborted);
        }
        catch { /* best-effort — values context is optional */ }

        // ── Execute the query natively ──
        var executor = context.RequestServices.GetRequiredService<NativeQueryExecutor>();
        QueryResult? result;
        try
        {
            result = await executor.ExecuteAsync(projectId, queryId, values, bypassCache: liveResult, context.RequestAborted);
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
            // Query not found in ScyllaDB (definition_json missing or empty).
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

        AddResultFields(body, result);

        // Cache-control header for live results.
        if (liveResult)
        {
            context.Response.Headers["Cache-Control"] = "no-store, max-age=0";
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(body, JsonOptions, context.RequestAborted);
    }

    // ── Output builder (mirror addQueryResultFields) ──

    private static void AddResultFields(Dictionary<string, object?> output, QueryResult result)
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
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { code, message }
        }, JsonOptions, context.RequestAborted);
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

