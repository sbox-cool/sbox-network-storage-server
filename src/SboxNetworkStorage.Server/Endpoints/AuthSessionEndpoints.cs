using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Routing;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Native .NET Network Storage auth-session endpoints — the cutover of the Bun
/// <c>controllers/endpoint-modules/session-routes.js</c> handlers to ASP.NET Core.
/// Serves <c>POST /v{1,3}/{auth-sessions,sessions}/{projectId}/{create,refresh,reauth,revoke}</c>
/// directly (stateless HMAC tokens via <see cref="INetworkStorageAuthSessionService"/>,
/// s&amp;box player auth via <see cref="ISboxAuthVerifier"/>), so authenticated
/// session traffic no longer proxies to the legacy Bun storage runtime.
///
/// <para>Wire-contract parity with Bun: every response is HTTP 200 with the logical
/// status in the body (<c>{ ok, status, ... }</c>) and an <c>X-Request-Id</c> header;
/// game clients branch on <c>body.ok</c>, not the HTTP status code.</para>
/// </summary>
public static partial class AuthSessionEndpoints
{
    private const string Wiki = "https://sboxcool.com/wiki/network-storage-v3";

    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly string[] RoutePrefixes =
    [
        "/v3/auth-sessions", "/v3/sessions", "/v1/auth-sessions", "/v1/sessions",
    ];

    private static readonly Dictionary<string, (int Status, string Message, string DocsUrl)> Errors = new(StringComparer.Ordinal)
    {
        ["UNAUTHORIZED"] = (401, "Invalid or missing API key.", $"{Wiki}/quick-start#3-configure-the-library"),
        ["SBOX_AUTH_FAILED"] = (401, "s&box auth token verification failed.", $"{Wiki}/sbox-auth#error-handling"),
        ["AUTH_SESSION_DISABLED"] = (403, "Auth sessions are not enabled for this project.", $"{Wiki}/sbox-auth#session-caching"),
        ["AUTH_SESSION_REQUIRED"] = (401, "Auth session token is required.", $"{Wiki}/sbox-auth#session-caching"),
        ["AUTH_SESSION_INVALID"] = (401, "Auth session token is invalid.", $"{Wiki}/sbox-auth#session-caching"),
        ["AUTH_SESSION_EXPIRED"] = (401, "Auth session has expired.", $"{Wiki}/sbox-auth#session-caching"),
        ["AUTH_SESSION_STEAMID_MISMATCH"] = (403, "Auth session does not match the supplied Steam ID.", $"{Wiki}/sbox-auth#session-caching"),
        ["INVALID_STEAMID"] = (400, "Steam ID must be a plausible numeric s&box/Steam ID.", $"{Wiki}/sbox-auth#implementing-auth-in-your-game"),
        ["PROJECT_DISABLED"] = (403, "This project is currently disabled.", $"{Wiki}/quick-start#2-create-a-project"),
    };

    public static IEndpointRouteBuilder MapAuthSessions(this IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in RoutePrefixes)
        {
            endpoints.MapPost($"{prefix}/{{projectId}}/create", CreateAsync)
                .WithDisplayName($"Network Storage auth session create ({prefix})")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage auth session create (no Bun proxy)");
            endpoints.MapPost($"{prefix}/{{projectId}}/refresh", RefreshAsync)
                .WithDisplayName($"Network Storage auth session refresh ({prefix})")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage auth session refresh (no Bun proxy)");
            endpoints.MapPost($"{prefix}/{{projectId}}/reauth", ReauthAsync)
                .WithDisplayName($"Network Storage auth session reauth ({prefix})")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage auth session reauth (no Bun proxy)");
            endpoints.MapPost($"{prefix}/{{projectId}}/revoke", RevokeAsync)
                .WithDisplayName($"Network Storage auth session revoke ({prefix})")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage auth session revoke (no Bun proxy)");
        }
        return endpoints;
    }

    internal static async Task CreateAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var project = await RequireSessionProjectAsync(context);
        if (!project.Ok) { await SessionErrorAsync(context, requestId, project.ErrorCode!, project.ErrorDetail); return; }

        var body = await ReadBodyAsync(context);
        var steamId = ReadSteamId(context, body);
        if (string.IsNullOrEmpty(steamId) || !IsPlausibleSteamId(steamId))
        {
            await SessionErrorAsync(context, requestId, "INVALID_STEAMID", "Create session requires a plausible x-steam-id value.");
            return;
        }

        var verifier = context.RequestServices.GetRequiredService<ISboxAuthVerifier>();
        var sbox = await verifier.CheckAsync(BuildSboxCheck(context, project.ProjectId, "__auth_session_create"), context.RequestAborted);
        if (!sbox.Ok) { await SessionErrorAsync(context, requestId, "SBOX_AUTH_FAILED", sbox.Error); return; }
        if (!string.IsNullOrEmpty(sbox.SteamId) && !string.Equals(sbox.SteamId, steamId, StringComparison.Ordinal))
        {
            await SessionErrorAsync(context, requestId, "AUTH_SESSION_STEAMID_MISMATCH", null);
            return;
        }

        var service = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
        var created = service.Create(project.UserId, project.ProjectId, steamId, project.TtlSeconds, new Dictionary<string, object?>
        {
            ["createdFrom"] = "builtin-session-endpoint",
            ["userAgent"] = context.Request.Headers.UserAgent.ToString(),
        });
        await WriteTokenSuccessAsync(context, requestId, created.Token!, created.TtlSeconds, created.Session!);
    }

    internal static async Task RefreshAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var project = await RequireSessionProjectAsync(context);
        if (!project.Ok) { await SessionErrorAsync(context, requestId, project.ErrorCode!, project.ErrorDetail); return; }

        var body = await ReadBodyAsync(context);
        var token = ReadAuthSessionToken(context, body);
        if (string.IsNullOrEmpty(token)) { await SessionErrorAsync(context, requestId, "AUTH_SESSION_REQUIRED", null); return; }

        var service = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
        var result = service.Refresh(project.ProjectId, token, project.TtlSeconds);
        if (!result.Ok) { await SessionErrorAsync(context, requestId, result.Code!, result.Message); return; }

        await WriteTokenSuccessAsync(context, requestId, result.Token ?? token, result.TtlSeconds, result.Session!);
    }

    internal static async Task ReauthAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var project = await RequireSessionProjectAsync(context);
        if (!project.Ok) { await SessionErrorAsync(context, requestId, project.ErrorCode!, project.ErrorDetail); return; }

        var body = await ReadBodyAsync(context);
        var token = ReadAuthSessionToken(context, body);
        if (string.IsNullOrEmpty(token)) { await SessionErrorAsync(context, requestId, "AUTH_SESSION_REQUIRED", null); return; }

        var service = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
        var valid = service.Validate(project.ProjectId, token);
        if (!valid.Ok) { await SessionErrorAsync(context, requestId, valid.Code!, valid.Message); return; }

        var verifier = context.RequestServices.GetRequiredService<ISboxAuthVerifier>();
        var sbox = await verifier.CheckAsync(BuildSboxCheck(context, project.ProjectId, "__auth_session_reauth"), context.RequestAborted);
        if (!sbox.Ok) { await SessionErrorAsync(context, requestId, "SBOX_AUTH_FAILED", sbox.Error); return; }
        if (!string.IsNullOrEmpty(sbox.SteamId) && !string.Equals(sbox.SteamId, valid.Session!.SteamId, StringComparison.Ordinal))
        {
            await SessionErrorAsync(context, requestId, "AUTH_SESSION_STEAMID_MISMATCH", null);
            return;
        }

        var refreshed = service.Refresh(project.ProjectId, token, project.TtlSeconds);
        if (!refreshed.Ok) { await SessionErrorAsync(context, requestId, refreshed.Code!, refreshed.Message); return; }

        await WriteTokenSuccessAsync(context, requestId, refreshed.Token ?? token, refreshed.TtlSeconds, refreshed.Session!);
    }

    internal static async Task RevokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var project = await RequireSessionProjectAsync(context);
        if (!project.Ok) { await SessionErrorAsync(context, requestId, project.ErrorCode!, project.ErrorDetail); return; }

        var body = await ReadBodyAsync(context);
        var token = ReadAuthSessionToken(context, body);
        if (string.IsNullOrEmpty(token)) { await SessionErrorAsync(context, requestId, "AUTH_SESSION_REQUIRED", null); return; }

        var service = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
        var result = service.Revoke(project.ProjectId, token);
        if (!result.Ok) { await SessionErrorAsync(context, requestId, result.Code!, result.Message); return; }

        var body200 = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = 200,
            ["session"] = SessionDict(result.Session!),
            ["_requestId"] = requestId,
        };
        await WriteJsonAsync(context, requestId, body200);
    }

    private static async Task<SessionProject> RequireSessionProjectAsync(HttpContext context)
    {
        // Session responses are always HTTP 200 (logical status in the body),
        // so the usage middleware's 401-skip cannot filter unauthenticated
        // noise here. Suppress by default; a successful resolution re-annotates
        // the request as a metered Auth operation.
        NetworkStorageUsageContext.Suppress(context);

        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var apiKey = context.Request.Headers["x-api-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey)) apiKey = context.Request.Query["apiKey"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
            return SessionProject.Fail("UNAUTHORIZED", "No API key provided. Send x-api-key header or apiKey query parameter.");

        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null || !auth.Enabled)
            return SessionProject.Fail("UNAUTHORIZED", null);

        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
            return SessionProject.Fail("UNAUTHORIZED", null);
        if (!access.Project.Enabled)
            return SessionProject.Fail("PROJECT_DISABLED", null);
        if (access.Project.EnableAuthSessions != true)
            return SessionProject.Fail("AUTH_SESSION_DISABLED", null);

        NetworkStorageUsageContext.Set(context, projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.Usage.UsageKind.Auth);
        var ttl = access.Project.AuthSessionTtlSeconds ?? NetworkStorageAuthSessionService.DefaultTtlSeconds;
        return new SessionProject(true, null, null, auth.UserId, projectId, ttl);
    }

    private static SboxAuthCheck BuildSboxCheck(HttpContext context, string projectId, string endpointSlug)
    {
        var apiKey = context.Request.Headers["x-api-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey)) apiKey = context.Request.Query["apiKey"].FirstOrDefault();
        var hostToken = FirstNonEmpty(
            context.Request.Headers["x-sbox-token"].FirstOrDefault(),
            context.Request.Headers["x-sbox-auth-token"].FirstOrDefault(),
            context.Request.Query["token"].FirstOrDefault());
        var hostSteamId = FirstNonEmpty(
            context.Request.Headers["x-steam-id"].FirstOrDefault(),
            context.Request.Headers["x-sbox-steam-id"].FirstOrDefault(),
            context.Request.Query["steamId"].FirstOrDefault());
        return new SboxAuthCheck(
            hostToken,
            hostSteamId,
            context.Request.Headers["x-on-behalf-of"].FirstOrDefault(),
            context.Request.Headers["x-on-behalf-of-token"].FirstOrDefault(),
            context.Request.Headers["x-proxy-signature"].FirstOrDefault(),
            apiKey ?? string.Empty,
            projectId,
            endpointSlug);
    }

    private static async Task<JsonElement?> ReadBodyAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (method != HttpMethods.Post && method != HttpMethods.Put && method != HttpMethods.Patch) return null;
        try
        {
            var element = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadSteamId(HttpContext context, JsonElement? body)
    {
        var header = context.Request.Headers["x-steam-id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(header)) return header;
        var query = context.Request.Query["steamId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(query)) return query;
        if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("steamId", out var value))
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
        }
        return string.Empty;
    }

    private static string ReadAuthSessionToken(HttpContext context, JsonElement? body)
    {
        var headerToken = FirstNonEmpty(
            context.Request.Headers["x-auth-session"].FirstOrDefault(),
            context.Request.Headers["x-auth-session-token"].FirstOrDefault());
        if (!string.IsNullOrEmpty(headerToken)) return headerToken;

        var authorization = context.Request.Headers["authorization"].FirstOrDefault() ?? string.Empty;
        if (authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization[7..].Trim();
            if (!string.IsNullOrEmpty(bearer)) return bearer;
        }

        var query = FirstNonEmpty(
            context.Request.Query["authSessionToken"].FirstOrDefault(),
            context.Request.Query["sessionToken"].FirstOrDefault());
        if (!string.IsNullOrEmpty(query)) return query;

        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("authSessionToken", out var t1) && t1.ValueKind == JsonValueKind.String)
                return t1.GetString() ?? string.Empty;
            if (b.TryGetProperty("sessionToken", out var t2) && t2.ValueKind == JsonValueKind.String)
                return t2.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static async Task WriteTokenSuccessAsync(HttpContext context, string requestId, string token, int ttlSeconds, AuthSessionView session)
    {
        var body = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = 200,
            ["sessionToken"] = token,
            ["ttlSeconds"] = ttlSeconds,
            ["session"] = SessionDict(session),
            ["_requestId"] = requestId,
        };
        await WriteJsonAsync(context, requestId, body);
    }

    private static async Task SessionErrorAsync(HttpContext context, string requestId, string code, string? detail)
    {
        var (status, message, docsUrl) = Errors.TryGetValue(code, out var def) ? def : (500, "Unknown error.", Wiki);
        var resolvedMessage = detail ?? message;
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = resolvedMessage,
            ["requestId"] = requestId,
            ["docsUrl"] = docsUrl,
        };
        var body = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["status"] = status,
            ["error"] = error,
            ["message"] = resolvedMessage,
            ["_requestId"] = requestId,
        };
        await WriteJsonAsync(context, requestId, body);
    }

    private static async Task WriteJsonAsync(HttpContext context, string requestId, Dictionary<string, object?> body)
    {
        context.Response.Headers["X-Request-Id"] = requestId;
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(body, JsonOptions);
    }

    private static Dictionary<string, object?> SessionDict(AuthSessionView s) => new()
    {
        ["id"] = s.Id,
        ["projectId"] = s.ProjectId,
        ["userId"] = s.UserId,
        ["steamId"] = s.SteamId,
        ["createdAt"] = s.CreatedAt,
        ["expiresAt"] = s.ExpiresAt,
        ["revokedAt"] = s.RevokedAt,
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value)) return value;
        return string.Empty;
    }

    internal static bool IsPlausibleSteamId(string value)
        => !string.IsNullOrEmpty(value) && PlausibleSteamId().IsMatch(value.Trim());

    [GeneratedRegex("^[79][0-9]{3,16}$")]
    private static partial Regex PlausibleSteamId();

    private readonly record struct SessionProject(bool Ok, string? ErrorCode, string? ErrorDetail, long UserId, string ProjectId, int TtlSeconds)
    {
        public static SessionProject Fail(string code, string? detail) => new(false, code, detail, 0, string.Empty, 0);
    }
}
