using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Outcome of <see cref="PlayerIdentity.ResolveAsync"/>: the verified player, or the
/// rejection (status, code, message) the caller renders in its own wire shape.
/// </summary>
internal readonly record struct PlayerIdentityResult(string? SteamId, int Status, string? Code, string? Message)
{
    public bool Ok => SteamId is not null;

    public static PlayerIdentityResult Verified(string steamId) => new(steamId, StatusCodes.Status200OK, null, null);

    public static PlayerIdentityResult Reject(string code, string message, int status = StatusCodes.Status401Unauthorized)
        => new(null, status, code, message);
}

/// <summary>
/// Player identity for public-key (game client) requests: auth-session tokens,
/// s&amp;box tokens (direct and host-proxied) and the claimed Steam ID. Shared by
/// endpoint execution and the direct collection document API so both verify
/// players identically.
/// </summary>
internal static class PlayerIdentity
{
    /// <summary>The Steam ID the caller claims, from headers, query or the JSON body.</summary>
    internal static string ClaimedSteamId(HttpRequest request, JsonElement body) => FirstNonEmpty(
        request.Headers["x-steam-id"].FirstOrDefault(),
        request.Headers["x-sbox-steam-id"].FirstOrDefault(),
        request.Query["steamId"].FirstOrDefault(),
        request.Query["steamid"].FirstOrDefault(),
        ReadBodyString(body, "steamId")) ?? "";

    /// <summary>
    /// Resolves the player for a public-key request. When <paramref name="required"/>
    /// is false and no auth-session token is presented, the claimed identity passes
    /// through unverified (auth-disabled projects). Otherwise the identity must be
    /// proven by a valid auth session and/or s&amp;box token, and every supplied
    /// Steam ID claim must match it. <paramref name="body"/> may be default when the
    /// request body is not an auth envelope (e.g. a stored document).
    /// </summary>
    internal static async Task<PlayerIdentityResult> ResolveAsync(
        HttpContext context, JsonElement body, string projectId, string endpointSlug, string apiKey,
        long ownerUserId, bool required, bool sessionsEnabled, string claimedSteamId)
    {
        var request = context.Request;
        var authorization = request.Headers.Authorization.FirstOrDefault() ?? "";
        var sessionToken = FirstNonEmpty(
            request.Headers["x-auth-session"].FirstOrDefault(),
            request.Headers["x-auth-session-token"].FirstOrDefault(),
            authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null,
            request.Query["authSessionToken"].FirstOrDefault(),
            request.Query["sessionToken"].FirstOrDefault(),
            ReadBodyString(body, "authSessionToken"), ReadBodyString(body, "sessionToken"));
        var token = FirstNonEmpty(
            request.Headers["x-sbox-token"].FirstOrDefault(),
            request.Headers["x-sbox-auth-token"].FirstOrDefault(),
            request.Query["token"].FirstOrDefault(), ReadBodyString(body, "token"));
        var clientSteamId = request.Headers["x-on-behalf-of"].FirstOrDefault();
        var clientToken = request.Headers["x-on-behalf-of-token"].FirstOrDefault();
        var proxySignature = request.Headers["x-proxy-signature"].FirstOrDefault();
        var hasSboxCredentials = !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(clientSteamId)
            || !string.IsNullOrEmpty(clientToken) || !string.IsNullOrEmpty(proxySignature);
        // Auth-disabled projects match legacy server: s&box tokens are ignored and the
        // claimed identity (or the proxied player, already plausibility-checked
        // by the caller) passes through. Presented auth-session tokens are still
        // validated (they are our own issuance). Required projects fall through
        // to strict verification below.
        if (!required && sessionToken is null)
            return PlayerIdentityResult.Verified(FirstNonEmpty(clientSteamId, claimedSteamId) ?? "anonymous");

        string? verifiedSteamId = null;
        if (sessionToken is not null)
        {
            if (!sessionsEnabled)
                return PlayerIdentityResult.Reject("AUTH_SESSION_DISABLED", "Auth sessions are not enabled for this project.", StatusCodes.Status403Forbidden);
            var sessions = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
            var valid = sessions.Validate(projectId, sessionToken);
            if (!valid.Ok)
                return PlayerIdentityResult.Reject(valid.Code!, valid.Message!);
            if (valid.Session!.UserId != ownerUserId || string.IsNullOrEmpty(valid.Session.SteamId))
                return PlayerIdentityResult.Reject("AUTH_SESSION_INVALID", "Auth session does not belong to this project owner.");
            verifiedSteamId = valid.Session.SteamId;
        }

        if (hasSboxCredentials || sessionToken is null)
        {
            var verifier = context.RequestServices.GetRequiredService<ISboxAuthVerifier>();
            var check = new SboxAuthCheck(token ?? "", claimedSteamId, clientSteamId, clientToken,
                proxySignature, apiKey, projectId, endpointSlug, ClientAddress.Resolve(context));
            var result = await verifier.CheckAsync(check, context.RequestAborted);
            if (!result.Ok || string.IsNullOrEmpty(result.SteamId))
                return PlayerIdentityResult.Reject("SBOX_AUTH_FAILED", result.Error ?? "Player identity could not be verified.");

            if (!string.IsNullOrEmpty(clientSteamId))
            {
                // The shared proxy verifier authenticates the host and signature,
                // not the client token. A public key is not a delegation authority:
                // verify the client as well before accepting its player identity.
                result = await verifier.CheckAsync(
                    new SboxAuthCheck(clientToken ?? "", clientSteamId, null, null, null, apiKey, projectId, endpointSlug, ClientAddress.Resolve(context)),
                    context.RequestAborted);
                if (!result.Ok || string.IsNullOrEmpty(result.SteamId))
                    return PlayerIdentityResult.Reject("SBOX_AUTH_FAILED", result.Error ?? "Delegated player identity could not be verified.");
            }

            if (verifiedSteamId is not null && !string.Equals(verifiedSteamId, result.SteamId, StringComparison.Ordinal))
                return PlayerIdentityResult.Reject("AUTH_SESSION_STEAMID_MISMATCH", "Auth session and s&box token identify different players.");
            verifiedSteamId = result.SteamId;
        }

        // Check every supported claim, not just the highest-precedence one. Proxy
        // requests carry the verified host in these fields and the client separately.
        var expectedClaim = string.IsNullOrEmpty(clientSteamId) ? verifiedSteamId : claimedSteamId;
        if (!MatchesClaim(request.Headers["x-steam-id"].FirstOrDefault())
            || !MatchesClaim(request.Headers["x-sbox-steam-id"].FirstOrDefault())
            || !MatchesClaim(request.Query["steamId"].FirstOrDefault())
            || !MatchesClaim(request.Query["steamid"].FirstOrDefault())
            || !MatchesClaim(ReadBodyString(body, "steamId")))
            return PlayerIdentityResult.Reject("AUTH_SESSION_STEAMID_MISMATCH", "Supplied Steam ID does not match the verified identity.");

        return PlayerIdentityResult.Verified(verifiedSteamId!);

        bool MatchesClaim(string? claim) => string.IsNullOrEmpty(claim)
            || string.Equals(claim, expectedClaim, StringComparison.Ordinal);
    }

    internal static string? ReadBodyString(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when name == "steamId" => value.GetRawText(),
            _ => null
        };
    }

    internal static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value)) return value;
        return null;
    }
}
