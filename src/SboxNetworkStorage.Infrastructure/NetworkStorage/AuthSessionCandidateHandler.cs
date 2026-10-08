using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only / dry-run candidate for <c>POST /v{1,3}/{auth-sessions,sessions}/:projectId/{create,refresh,reauth,revoke}</c>.
/// Mirrors the Bun session-routes.js handlers but never writes production session state.
///
/// Missing Bun features (blocked):
///  - <c>auth.project.enableAuthSessions</c> flag: <c>StorageApiKeyAuthResult</c> lacks this field; only
///    <c>Enabled</c> (project-level on/off) is available. The handler cannot distinguish between a project
///    that is "on but auth-sessions off" and one that is "fully on".
///  - <c>node:crypto</c> token generation / HMAC signing: <c>createStorageAuthSession</c>,
///    <c>validateStorageAuthSession</c>, <c>refreshStorageAuthSession</c>, <c>revokeStorageAuthSession</c>
///    use crypto primitives unavailable in this layer. The dry-run records the intended writes without
///    performing them.
///  - <c>checkSboxAuth</c> for reauth: the Bun reauth path validates the endpoint auth slug; this handler
///    omits that check until the sbox-auth shim is implemented.
///
/// All four actions resolve the API key, validate the project is enabled, and return a shaded result with
/// <c>IntendedWritePaths</c> populated (simulating the storage paths that would be written) but set
/// <c>StatusCode = 200</c> with a <c>"dry-run"</c> / <c>"suppressed"</c> body shape. No real tokens or
/// session records are created, rotated, or deleted.
/// </summary>
public sealed class AuthSessionCandidateHandler : INetworkStorageCandidateHandler
{
    private const string PathPrefix = "network-storage/auth-sessions";

    private readonly IStorageApiKeyResolver _apiKeyResolver;

    public AuthSessionCandidateHandler(IStorageApiKeyResolver apiKeyResolver)
    {
        _apiKeyResolver = apiKeyResolver;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.AuthSession;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.AuthSession
        && string.Equals(route.Method, "POST", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the auth-session action (create, refresh, reauth, revoke) from the route template's
    /// last literal segment. Templates are e.g. <c>/v3/auth-sessions/:projectId/create</c> →
    /// <c>"create"</c>. Returns null when the action cannot be determined.
    /// </summary>
    private static string? ExtractAction(NetworkStorageRouteEntry entry)
    {
        if (entry == null) return null;

        var template = entry.Template;
        var lastSlash = template.LastIndexOf('/');
        if (lastSlash < 0) return null;

        var last = template[(lastSlash + 1)..];
        return last is "create" or "refresh" or "reauth" or "revoke" ? last : null;
    }

    /// <summary>
    /// Reads the auth-session token from the request credentials field
    /// (mirrors Bun's <c>readAuthSessionToken</c>).
    /// </summary>
    private static string? ReadAuthSessionToken(NetworkStorageCandidateRequest request)
    {
        // The credentials carry AuthSessionToken directly (extracted by the shadow middleware
        // from Authorization/x-auth-session headers, query params, or body).
        return request.Credentials.AuthSessionToken;
    }

    /// <summary>
    /// Reads the Steam ID from the request (x-steam-id header, query param, or body).
    /// </summary>
    private static string? ReadSteamId(NetworkStorageCandidateRequest request)
    {
        // Prefer credentials SteamId (from header), then query param, then body field.
        if (!string.IsNullOrEmpty(request.Credentials.SteamId))
            return request.Credentials.SteamId;

        var querySteamId = request.QueryValue("steamId");
        if (!string.IsNullOrEmpty(querySteamId))
            return querySteamId;

        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("steamId", out var steamIdProp))
                {
                    if (steamIdProp.ValueKind == JsonValueKind.Number)
                        return steamIdProp.GetInt64().ToString();
                    if (steamIdProp.ValueKind == JsonValueKind.String)
                        return steamIdProp.GetString();
                }
            }
            catch (JsonException)
            {
                // Not parseable as JSON; ignore.
            }
        }

        return null;
    }

    /// <summary>
    /// Simple Steam ID plausibility check (mirrors Bun's <c>isPlausibleSteamId</c> which checks
    /// length and numeric form).
    /// </summary>
    private static bool IsPlausibleSteamId(string? steamId)
    {
        if (string.IsNullOrEmpty(steamId)) return false;
        // Steam IDs are typically 17-digit numeric strings or "7656119..." prefixed.
        if (steamId.Length is < 7 or > 32) return false;
        foreach (var c in steamId)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the IntendedWritePaths for an auth-session create operation.
    /// Bun stores sessions in <c>network-storage/projects/{projectId}/auth-sessions/{id}...<c>
    /// but is self-contained (stateless token). The dry-run path reflects the logical storage.
    /// </summary>
    private static string[] BuildCreateIntendedWritePaths(string projectId)
    {
        return [$"{PathPrefix}/{Uri.EscapeDataString(projectId)}/session__create"];
    }

    private static string[] BuildRefreshIntendedWritePaths(string projectId)
    {
        return [$"{PathPrefix}/{Uri.EscapeDataString(projectId)}/session__refresh"];
    }

    private static string[] BuildReauthIntendedWritePaths(string projectId)
    {
        return [$"{PathPrefix}/{Uri.EscapeDataString(projectId)}/session__reauth"];
    }

    private static string[] BuildRevokeIntendedWritePaths(string projectId)
    {
        return [$"{PathPrefix}/{Uri.EscapeDataString(projectId)}/session__revoke"];
    }

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var action = request.Route.Entry is null ? null : ExtractAction(request.Route.Entry);

        if (action is null)
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "AUTH_SESSION_INVALID_ACTION",
                new
                {
                    ok = false,
                    error = new { code = "AUTH_SESSION_INVALID_ACTION", message = "Could not determine auth-session action from the request route." },
                    projectId,
                    action = (string?)null,
                    source = "candidate"
                },
                authDecision: "anonymous");
        }

        // --- Credentials check (mirrors Bun's requireSessionProject) ---
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return NetworkStorageCandidateResult.Error(
                401,
                "UNAUTHORIZED",
                new
                {
                    ok = false,
                    status = 401,
                    error = new { code = "UNAUTHORIZED", message = "No API key provided. Send x-api-key header or apiKey query parameter." },
                    action,
                    projectId,
                    source = "candidate"
                },
                authDecision: "denied");
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageCandidateResult.Error(
                500,
                "AUTH_RESOLVE_FAILED",
                new
                {
                    ok = false,
                    error = new { code = "AUTH_RESOLVE_FAILED", message = "Could not resolve API key due to an internal error." },
                    action,
                    projectId,
                    source = "candidate"
                },
                authDecision: "denied");
        }

        if (auth is null)
        {
            return NetworkStorageCandidateResult.Error(
                401,
                "UNAUTHORIZED",
                new
                {
                    ok = false,
                    status = 401,
                    error = new { code = "UNAUTHORIZED", message = "Invalid or inactive API key for this project." },
                    action,
                    projectId,
                    source = "candidate"
                },
                authDecision: "denied");
        }

        if (!auth.Enabled)
        {
            return NetworkStorageCandidateResult.Error(
                403,
                "PROJECT_DISABLED",
                new
                {
                    ok = false,
                    status = 403,
                    error = new { code = "PROJECT_DISABLED", message = "This project is disabled. Enable it in your workspace settings." },
                    action,
                    projectId,
                    source = "candidate"
                },
                authDecision: "denied");
        }

        // [MISSING] auth.project.enableAuthSessions check — StorageApiKeyAuthResult does not carry
        // this flag. Bun would return AUTH_SESSION_DISABLED here if the project has opted out of
        // auth-sessions. We proceed to the dry-run since we cannot distinguish.

        return action switch
        {
            "create" => await HandleCreateAsync(request, auth, projectId),
            "refresh" => await HandleRefreshAsync(request, auth, projectId),
            "reauth" => await HandleReauthAsync(request, auth, projectId),
            "revoke" => await HandleRevokeAsync(request, auth, projectId),
            _ => NetworkStorageCandidateResult.Error(
                400,
                "AUTH_SESSION_INVALID_ACTION",
                new
                {
                    ok = false,
                    error = new { code = "AUTH_SESSION_INVALID_ACTION", message = "Unknown auth-session action." },
                    action,
                    projectId,
                    source = "candidate"
                },
                authDecision: "denied"),
        };
    }

    private async Task<NetworkStorageCandidateResult> HandleCreateAsync(
        NetworkStorageCandidateRequest request,
        StorageApiKeyAuthResult auth,
        string projectId)
    {
        var steamId = ReadSteamId(request);

        if (string.IsNullOrEmpty(steamId) || !IsPlausibleSteamId(steamId))
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "INVALID_STEAMID",
                new
                {
                    ok = false,
                    status = 400,
                    error = new { code = "INVALID_STEAMID", message = "Create session requires a plausible x-steam-id value." },
                    action = "create",
                    projectId,
                    source = "candidate"
                },
                authDecision: "allowed");
        }

        // [MISSING] checkSboxAuth for endpoint slug "__auth_session_create" — omitted until the
        // sbox-auth shim is wired. Bun does this check before creating the session.

        var intendedWrites = BuildCreateIntendedWritePaths(projectId);

        // Dry-run: do NOT create a real session token. Bun's createStorageAuthSession:
        //   1. Generates a token via randomBytes(32) + HMAC-SHA256 signing
        //   2. Returns { token, session: { id, userId, projectId, steamId, createdAt, expiresAt }, ttlSeconds }
        // We record the path and return a suppression marker.
        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "create",
            projectId,
            steamId,
            source = "candidate",
            _note = "Real session token generation requires node:crypto (randomBytes + HMAC) which is unavailable in this layer.",
            intendedWrite = intendedWrites[0]
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private async Task<NetworkStorageCandidateResult> HandleRefreshAsync(
        NetworkStorageCandidateRequest request,
        StorageApiKeyAuthResult auth,
        string projectId)
    {
        var token = ReadAuthSessionToken(request);

        if (string.IsNullOrEmpty(token))
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "AUTH_SESSION_REQUIRED",
                new
                {
                    ok = false,
                    status = 400,
                    error = new { code = "AUTH_SESSION_REQUIRED", message = "Auth session token is required." },
                    action = "refresh",
                    projectId,
                    source = "candidate"
                },
                authDecision: "allowed");
        }

        var intendedWrites = BuildRefreshIntendedWritePaths(projectId);

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "refresh",
            projectId,
            tokenPrefix = token.Length >= 12 ? token[..12] + "..." : "(short)",
            source = "candidate",
            _note = "Real token refresh requires node:crypto HMAC verification and re-signing.",
            intendedWrite = intendedWrites[0]
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private async Task<NetworkStorageCandidateResult> HandleReauthAsync(
        NetworkStorageCandidateRequest request,
        StorageApiKeyAuthResult auth,
        string projectId)
    {
        var token = ReadAuthSessionToken(request);

        if (string.IsNullOrEmpty(token))
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "AUTH_SESSION_REQUIRED",
                new
                {
                    ok = false,
                    status = 400,
                    error = new { code = "AUTH_SESSION_REQUIRED", message = "Auth session token is required." },
                    action = "reauth",
                    projectId,
                    source = "candidate"
                },
                authDecision: "allowed");
        }

        // [MISSING] checkSboxAuth for endpoint slug "__auth_session_reauth" — omitted until the
        // sbox-auth shim is wired. Bun does this check before reauthenticating.

        var intendedWrites = BuildReauthIntendedWritePaths(projectId);

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "reauth",
            projectId,
            tokenPrefix = token.Length >= 12 ? token[..12] + "..." : "(short)",
            source = "candidate",
            _note = "Real reauth requires node:crypto session validation + HMAC re-signing.",
            intendedWrite = intendedWrites[0]
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private async Task<NetworkStorageCandidateResult> HandleRevokeAsync(
        NetworkStorageCandidateRequest request,
        StorageApiKeyAuthResult auth,
        string projectId)
    {
        var token = ReadAuthSessionToken(request);

        if (string.IsNullOrEmpty(token))
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "AUTH_SESSION_REQUIRED",
                new
                {
                    ok = false,
                    status = 400,
                    error = new { code = "AUTH_SESSION_REQUIRED", message = "Auth session token is required." },
                    action = "revoke",
                    projectId,
                    source = "candidate"
                },
                authDecision: "allowed");
        }

        var intendedWrites = BuildRevokeIntendedWritePaths(projectId);

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "revoke",
            projectId,
            tokenPrefix = token.Length >= 12 ? token[..12] + "..." : "(short)",
            source = "candidate",
            _note = "Real revocation requires session token validation via node:crypto HMAC.",
            intendedWrite = intendedWrites[0]
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }
}
