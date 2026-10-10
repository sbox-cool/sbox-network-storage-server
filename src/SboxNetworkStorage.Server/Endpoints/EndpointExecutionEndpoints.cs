using SboxNetworkStorage.Server.Hosting;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;


namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Native .NET execution for Network Storage user-defined endpoints
/// (<c>GET</c>/<c>POST /v3/endpoints/{projectId}/{endpointSlug}</c> and <c>/v1</c>
/// aliases). Endpoint execution is served entirely by the deterministic the store
/// executor — there is no legacy server proxy in the request path. An endpoint definition the
/// executor cannot yet reproduce returns a reported 501, never a fallback to the
/// decommissioned legacy server storage-api.
/// </summary>
public static class EndpointExecutionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IEndpointRouteBuilder MapEndpointExecution(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v3/endpoints/{projectId}/{endpointSlug}", ExecuteEndpointAsync)
            .WithDisplayName("Network Storage endpoint execution (native)");
        endpoints.MapPost("/v1/endpoints/{projectId}/{endpointSlug}", ExecuteEndpointAsync)
            .WithDisplayName("Network Storage endpoint execution v1 alias (native)");

        // POST /v3/endpoints/:projectId with the slug inside the request body.
        endpoints.MapPost("/v3/endpoints/{projectId}", ExecuteEndpointAsync)
            .WithDisplayName("Network Storage endpoint execution body slug (native)");
        endpoints.MapPost("/v1/endpoints/{projectId}", ExecuteEndpointAsync)
            .WithDisplayName("Network Storage endpoint execution v1 body slug (native)");

        return endpoints;
    }

    internal static async Task ExecuteEndpointAsync(HttpContext context)
    {
        // Buffer the request body so input parsing and any downstream middleware can
        // read it. (Endpoint execution is .NET-only — there is no legacy server proxy re-read.)
        context.Request.EnableBuffering();

        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var endpointSlug = (string?)context.GetRouteValue("endpointSlug") ?? "";

        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var executor = context.RequestServices.GetRequiredService<EndpointExecutor>();

        // A dedicated server authenticates with the Network Storage SECRET key alone —
        // it has no public key to send. Accept the secret key as a first-class
        // credential (x-secret-key and friends, matching the header/query names the
        // legacy server runtime honoured) instead of demanding x-api-key. Without this an
        // `exposure: public` + `requiresSecretKey: true` endpoint called correctly by a
        // dedicated server 401'd with "Missing apiKey" before the key was ever read.
        // Credential order: an explicit x-api-key HEADER, then the secret key, then
        // the apiKey QUERY param, then x-public-key. The official library sends
        // `x-secret-key` + `x-public-key` for endpoint calls
        // (docs/network-storage-docs-generated/v3/02b-dedicated-server-runtime.md), never
        // sets an x-api-key header — but BuildUrl in NetworkStorageHttp.cs ALWAYS
        // appends `?apiKey=<public>` to the URL. Folding the query param into the
        // header extraction (via ExtractApiKey) let that public key shadow the
        // presented secret key, so every dedicated-server call authenticated as the
        // public key: the endpoints:x gate was bypassed (public keys are never
        // permission-gated) and a call whose public key was unknown to the project
        // 401'd with UNAUTHORIZED even though a valid secret key sat in x-secret-key.
        var apiKeyHeader = context.Request.Headers["x-api-key"].FirstOrDefault();
        var secretKey = ExtractEndpointSecretKey(context.Request);
        var apiKeyQuery = context.Request.Query["apiKey"].FirstOrDefault();
        var publicKey = context.Request.Headers["x-public-key"].FirstOrDefault();
        var credential = FirstNonEmpty(apiKeyHeader, secretKey, apiKeyQuery, publicKey);

        if (string.IsNullOrEmpty(credential))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "UNAUTHORIZED",
                    detail = "Missing API key. Send a public key in x-api-key or x-public-key, or a Network Storage secret key in x-secret-key.",
                },
                JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(credential, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        // A secret key driving endpoint execution must hold `endpoints` execute.
        if (!ApiKeyPermissionPolicy.HasPermission(auth, "endpoints", "x"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = "FORBIDDEN", detail = "This secret key does not have execute access to endpoints." },
                JsonOptions);
            return;
        }

        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!access.Project.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "PROJECT_DISABLED" }, JsonOptions);
            return;
        }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            body = default;
        }

        // If the slug is not on the route, read it from the JSON body.
        if (string.IsNullOrEmpty(endpointSlug) && body.ValueKind == JsonValueKind.Object)
        {
            endpointSlug = body.TryGetProperty("endpoint", out var epProp) && epProp.ValueKind == JsonValueKind.String
                ? epProp.GetString() ?? ""
                : body.TryGetProperty("slug", out var slugProp) && slugProp.ValueKind == JsonValueKind.String
                    ? slugProp.GetString() ?? ""
                    : "";
        }

        if (string.IsNullOrEmpty(endpointSlug))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_REQUEST", detail = "Missing endpoint slug." }, JsonOptions);
            return;
        }

        // Usage metering: authenticated endpoint call with a resolved slug. The
        // middleware measures duration/bytes; this annotation supplies the
        // endpoint-call classification + per-endpoint counter dimension.
        SboxNetworkStorage.Server.Middleware.NetworkStorageUsageContext.Set(
            context, projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.Usage.UsageKind.EndpointCall, endpointSlug);

        var claimedSteamId = PlayerIdentity.ClaimedSteamId(context.Request, body);
        var hasSecretKey = string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase);

        // Host proxies act for another player via x-on-behalf-of. Matching legacy server,
        // that delegation is trusted without s&box verification only for secret
        // keys (dedicated servers) and auth-disabled projects; required public-key
        // requests verify the delegated client in ResolvePlayerIdentityAsync.
        var onBehalfOf = context.Request.Headers["x-on-behalf-of"].FirstOrDefault();
        var trustedProxy = !string.IsNullOrEmpty(onBehalfOf) && (hasSecretKey || !access.RequireSboxAuth);
        if (trustedProxy && !AuthSessionEndpoints.IsPlausibleSteamId(onBehalfOf!))
        {
            await WriteEndpointErrorAsync(context, 400, "INVALID_STEAMID",
                "x-on-behalf-of must be a plausible numeric ID starting with 7 or 9.");
            return;
        }

        // Secret keys deliberately delegate player identity on dedicated servers.
        // Public keys identify the project, not a player: authenticate before any
        // endpoint steps (including writes) and use only the verified identity.
        var steamId = hasSecretKey
            ? (trustedProxy ? onBehalfOf! : FirstNonEmpty(claimedSteamId) ?? "anonymous")
            : await ResolvePlayerIdentityAsync(context, body, projectId, endpointSlug, credential,
                auth.UserId, access.RequireSboxAuth, access.Project.EnableAuthSessions == true,
                claimedSteamId);
        if (steamId is null) return;

        // Projects with enableEncryptedRequests advertise it in the signed security
        // config; the library then sends only {security, encrypted, envelope}.
        if (HttpMethods.IsPost(context.Request.Method) && EncryptedEndpointEnvelope.TryRead(body, out var envelope))
        {
            // The library derives the envelope key from its public ApiKey (x-public-key / ?apiKey).
            var decrypted = EncryptedEndpointEnvelope.Decrypt(envelope, FirstNonEmpty(publicKey, apiKeyQuery) ?? "",
                projectId, endpointSlug, hasSecretKey ? null : steamId, ResolveAuthSessionId(context, body, projectId),
                DateTimeOffset.UtcNow);
            if (!decrypted.Ok)
            {
                await WriteEndpointErrorAsync(context, decrypted.Status, decrypted.Code!, decrypted.Message!);
                return;
            }
            body = decrypted.Payload;
        }

        // Input source matches the legacy server execution path: POST reads the JSON body,
        // GET (and other non-POST methods) read query parameters. Reserved auth keys
        // are never surfaced as endpoint input.
        var input = HttpMethods.IsPost(context.Request.Method)
            ? ParseInput(body)
            : BuildInputFromQuery(context.Request.Query);

        // The identity passed to the executor is never taken from endpoint input.
        var isDedicatedServer = hasSecretKey;
        var userId = auth.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // A request targeting the staged revision (editor play sessions with PublishTarget=next send
        // x-ns-publish-target / ?revisionTarget=next) runs staged endpoint and collection definitions
        // over the live ones. Live requests never load the overrides.
        var dataSource = context.RequestServices.GetService<IEndpointDataSource>();
        var valuesProvider = context.RequestServices.GetService<IQueryValuesContextProvider>();

        // Game-values context for `source: "values"` lookups and `values.*`
        // expressions. The executor was designed to receive it (context["values"])
        // but live execution passed an empty map, so any table lookup failed.
        // Load from the same provider queries use; a load failure is a real
        // error, reported like other native executor failures below.
        IReadOnlyDictionary<string, object?> gameValues = ReadOnlyDictionary<string, object?>.Empty;
        try
        {
            if (NetworkStoragePublishTarget.IsNext(
                    context.Request.Headers[NetworkStoragePublishTarget.HeaderName].FirstOrDefault(),
                    context.Request.Query[NetworkStoragePublishTarget.QueryName].FirstOrDefault())
                && await RevisionOverlay.LoadAsync(context.RequestServices.GetRequiredService<IWorkspaceStore>(),
                    auth.UserId, projectId, context.RequestAborted) is { } overlay)
            {
                var staged = context.RequestServices.GetRequiredService<StoreEndpointDataSource>().WithRevisionOverlay(overlay);
                dataSource = staged;
                executor = executor.WithDataSource(staged);
                valuesProvider = context.RequestServices.GetRequiredService<StoreQueryValuesContextProvider>().WithRevisionOverlay(overlay);
            }
            if (valuesProvider is not null)
                gameValues = await valuesProvider.GetValuesAsync(projectId, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await ReportNativeEndpointFailureAsync(context, projectId, endpointSlug, steamId, ex);
            await WriteEndpointErrorAsync(context, StatusCodes.Status500InternalServerError,
                "ENDPOINT_EXECUTION_FAILED", "The endpoint failed to execute.");
            return;
        }

        var execStart = System.Diagnostics.Stopwatch.GetTimestamp();
        EndpointExecutionResult? result;
        try
        {
            result = await executor.TryExecuteAsync(
                projectId,
                endpointSlug,
                input,
                steamId,
                userId,
                gameValues,
                hasSecretKey,
                isDedicatedServer,
                context.RequestAborted,
                liveServe: true,
                playerKeyMode: access.Project.PlayerKeyMode,
                // Public HTTP request: honour exposure/enabled/deprecated and the
                // endpoint's requiresSecretKey flag.
                enforcePublicAccessGates: true);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // client disconnected mid-flight — not an error worth reporting
        }
        catch (Exception ex)
        {
            // .NET-only: there is no legacy server fallback (the legacy storage-api is
            // decommissioned). A native executor failure is a real error — report it
            // to /admin/errors + Discord + the per-project dashboard, return 500.
            await ReportNativeEndpointFailureAsync(context, projectId, endpointSlug, steamId, ex);
            await WriteEndpointErrorAsync(context, StatusCodes.Status500InternalServerError,
                "ENDPOINT_EXECUTION_FAILED", "The endpoint failed to execute.");
            return;
        }

        if (result is null)
        {
            // A null result conflates "no such definition" with "definition
            // present but not yet supported". Disambiguate with a definition
            // probe: missing definitions are 404 (matching the GET slug-read
            // contract), present-but-unsupported stay reported 501. A store
            // outage mid-request can misread as missing; acceptable, since auth
            // and project reads already succeeded on the same store.
            Dictionary<string, object?>? definition = null;
            if (dataSource is not null)
            {
                try
                {
                    definition = await dataSource.ReadEndpointDefinitionAsync(projectId, endpointSlug, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                catch { /* probe failure: treat as missing below */ }
            }
            if (definition is null)
            {
                await WriteEndpointErrorAsync(context, StatusCodes.Status404NotFound,
                    "ENDPOINT_NOT_FOUND", $"Endpoint \"{endpointSlug}\" was not found.");
                return;
            }
            // .NET-only: the native executor does not yet support this endpoint
            // definition, and there is no legacy server fallback. Surface a clear, reported
            // error so the gap gets filled in .NET — never silently proxied to legacy server.
            await ReportNativeEndpointUnsupportedAsync(context, projectId, endpointSlug, steamId);
            await WriteEndpointErrorAsync(context, StatusCodes.Status501NotImplemented,
                "ENDPOINT_NOT_SUPPORTED", "This endpoint is not yet supported by the .NET runtime.");
            return;
        }

        // The native executor is authoritative now (no legacy server in the success path), so a
        // native 5xx must still reach /admin/errors + Discord + the per-project
        // dashboard — the same visibility the dry-run path provided before cutover.
        //
        // 409 Conflict is reported with the same urgency as 5xx: it is a LOGICAL /
        // data-integrity error (e.g. SAVE_REGRESSION_BLOCKED / STALE_SAVE — a player's
        // save rejected by a guard), not a setup or client-input error. A persistent
        // 409 loop silently strands player progress (the 2026-06-19 "satu" incident:
        // empty skills source → permanent 409 → invisible while no alert fired). Both
        // 5xx and 409 are reported; other 4xx (400/401/403/404/429) are routine
        // client/auth/limit responses and are NOT reported here.
        if (result.Status >= 500 || result.Status == 409)
        {
            var reporter = context.RequestServices.GetService<SboxNetworkStorage.Server.Infrastructure.EndpointErrorReporter>();
            if (reporter is not null)
            {
                var (code, message) = ExtractNativeError(result.Body);
                try
                {
                    await reporter.CaptureErrorResultAsync(
                        projectId, endpointSlug, context.Request.Method, steamId,
                        result.Status, code, message, CancellationToken.None);
                }
                catch { /* reporting is best-effort; never block the response */ }
            }
        }

        // Record an `endpoint.call` analytics event so the game-analytics dashboard
        // shows the player's real activity (save-all / load-player / etc.) on the
        // timeline. Previously this emitted `session.heartbeat`, which the dashboard
        // classifies as noise and hides — making active gameplay invisible. The
        // ingester still updates presence (last_seen / is_online) for any endpoint
        // category. Best-effort: never blocks the response. Anonymous callers
        // (e.g. unauthenticated health probes) are skipped.
        if (steamId != "anonymous")
        {
            var analytics = context.RequestServices.GetService<IPlayerAnalyticsService>();
            if (analytics is not null)
            {
                var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(execStart).TotalMilliseconds;
                var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["label"] = endpointSlug,
                    ["endpointSlug"] = endpointSlug,
                    ["method"] = context.Request.Method,
                    ["status"] = result.Status,
                    ["ok"] = result.Status is > 0 and < 400,
                    ["durationMs"] = Math.Round(durationMs, 1),
                };
                var callerName = ExtractPlayerName(input, result.Body);
                if (callerName is not null) payload["playerName"] = callerName;
                await analytics.RecordEndpointEventAsync(
                    projectId, steamId, endpointSlug,
                    eventType: "endpoint.call",
                    payload: payload,
                    trackedFieldDeltas: null,
                    CancellationToken.None);
            }
        }

        context.Response.StatusCode = result.Status > 0 ? result.Status : StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(result.Body ?? new { ok = result.Ok }, JsonOptions);
    }

    private static async Task ReportNativeEndpointFailureAsync(
        HttpContext context, string projectId, string slug, string steamId, Exception ex)
    {
        var reporter = context.RequestServices.GetService<SboxNetworkStorage.Server.Infrastructure.EndpointErrorReporter>();
        if (reporter is null) return;
        try { await reporter.CaptureExceptionAsync(projectId, slug, context.Request.Method, steamId, ex, CancellationToken.None); }
        catch { /* reporting is best-effort; never mask the original failure */ }
    }

    private static async Task ReportNativeEndpointUnsupportedAsync(
        HttpContext context, string projectId, string slug, string steamId)
    {
        var reporter = context.RequestServices.GetService<SboxNetworkStorage.Server.Infrastructure.EndpointErrorReporter>();
        if (reporter is null) return;
        try
        {
            await reporter.CaptureErrorResultAsync(
                projectId, slug, context.Request.Method, steamId, 501,
                "ENDPOINT_NOT_SUPPORTED", "Native executor cannot run this endpoint definition.", CancellationToken.None);
        }
        catch { /* reporting is best-effort */ }
    }

    private static Task WriteEndpointErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { ok = false, error = new { code, message } }, JsonOptions);
    }

    private static async Task<string?> ResolvePlayerIdentityAsync(
        HttpContext context, JsonElement body, string projectId, string endpointSlug, string apiKey,
        long ownerUserId, bool required, bool sessionsEnabled, string claimedSteamId)
    {
        var identity = await PlayerIdentity.ResolveAsync(context, body, projectId, endpointSlug, apiKey,
            ownerUserId, required, sessionsEnabled, claimedSteamId);
        if (identity.Ok) return identity.SteamId;
        await WriteEndpointErrorAsync(context, identity.Status, identity.Code!, identity.Message!);
        return null;
    }

    private static string? ReadBodyString(JsonElement body, string name) => PlayerIdentity.ReadBodyString(body, name);

    /// <summary>Id of the valid auth session presented with this request (same sources as identity resolution).</summary>
    private static string? ResolveAuthSessionId(HttpContext context, JsonElement body, string projectId)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault() ?? "";
        var token = FirstNonEmpty(
            context.Request.Headers["x-auth-session"].FirstOrDefault(),
            context.Request.Headers["x-auth-session-token"].FirstOrDefault(),
            authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null,
            context.Request.Query["authSessionToken"].FirstOrDefault(),
            context.Request.Query["sessionToken"].FirstOrDefault(),
            ReadBodyString(body, "authSessionToken"), ReadBodyString(body, "sessionToken"));
        if (token is null) return null;
        var valid = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>().Validate(projectId, token);
        return valid.Ok ? valid.Session?.Id : null;
    }

    private static readonly HashSet<string> ReservedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey", "steamId", "token", "authSessionToken", "sessionToken",
        "encryptedRequestId", "secretKey", "networkStorageSecret", "network_storage_secret_key",
    };

    /// <summary>
    /// Builds endpoint input from query parameters for non-POST execution requests,
    /// excluding reserved auth/identity keys. Mirrors the legacy server execution path, which
    /// reads GET input from the query string.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildInputFromQuery(IQueryCollection query)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in query)
        {
            if (ReservedQueryKeys.Contains(pair.Key)) continue;
            dict[pair.Key] = pair.Value.ToString();
        }
        return dict;
    }

    private static string? FirstNonEmpty(params string?[] values) => PlayerIdentity.FirstNonEmpty(values);

    /// <summary>
    /// Header/query names a dedicated server may use to present its Network Storage
    /// secret key. Mirrors ENDPOINT_SECRET_HEADER_NAMES / ENDPOINT_SECRET_QUERY_NAMES
    /// in <c>controllers/storage-shared.js</c> so the .NET ingress accepts exactly the
    /// same credentials the legacy server runtime did.
    /// </summary>
    private static readonly string[] SecretKeyHeaderNames =
    [
        "x-secret-key",
        "x-network-storage-secret",
        "x-storage-secret-key",
        "x-sbox-secret-key",
    ];

    private static readonly string[] SecretKeyQueryNames = ["secretKey", "networkStorageSecret"];

    internal static string? ExtractEndpointSecretKey(HttpRequest request)
    {
        foreach (var name in SecretKeyHeaderNames)
        {
            var value = request.Headers[name].FirstOrDefault();
            if (!string.IsNullOrEmpty(value)) return value;
        }

        foreach (var name in SecretKeyQueryNames)
        {
            if (request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                return value.ToString();
        }

        return null;
    }

    // Best-effort player display-name capture for analytics: saves (save-all,
    // save-stats, init-player) carry playerName in the input; loads echo it back in
    // the result body (optionally nested under "response"). Returns null when no
    // name is present so the ingester leaves the stored profile name untouched.
    private static string? ExtractPlayerName(IReadOnlyDictionary<string, object?> input, object? body)
    {
        if (input.TryGetValue("playerName", out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
            return s;
        if (body is not IReadOnlyDictionary<string, object?> dict)
            return null;
        if (dict.TryGetValue("playerName", out var pv) && pv is string ps && !string.IsNullOrWhiteSpace(ps))
            return ps;
        if (dict.TryGetValue("response", out var rv) && rv is IReadOnlyDictionary<string, object?> rd
            && rd.TryGetValue("playerName", out var rpv) && rpv is string rps && !string.IsNullOrWhiteSpace(rps))
            return rps;
        return null;
    }

    // Pull {error:{code,message}} out of a native execution result body for error reporting.
    private static (string Code, string Message) ExtractNativeError(object? body)
    {
        if (body is Dictionary<string, object?> dict
            && dict.TryGetValue("error", out var errObj)
            && errObj is Dictionary<string, object?> err)
        {
            var code = err.TryGetValue("code", out var c) ? c?.ToString() ?? "UNKNOWN" : "UNKNOWN";
            var message = err.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
            return (code, message);
        }
        return ("ENDPOINT_NATIVE_ERROR", "Native endpoint execution returned an error result.");
    }

    private static IReadOnlyDictionary<string, object?> ParseInput(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return ReadOnlyDictionary<string, object?>.Empty;

        // Prefer an explicit "input" or "args" field, otherwise use the whole body
        // minus metadata keys.
        JsonElement source = body;
        if (body.TryGetProperty("input", out var inputProp) && inputProp.ValueKind == JsonValueKind.Object)
            source = inputProp;
        else if (body.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
            source = argsProp;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in source.EnumerateObject())
        {
            if (prop.NameEquals("endpoint") || prop.NameEquals("slug"))
                continue;
            dict[prop.Name] = JsonToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value), System.StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonToObject).ToList(),
            _ => element.GetRawText()
        };
    }
}
