namespace SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

/// <summary>
/// Decoded, client-facing view of a Network Storage auth session. Mirrors the
/// Bun <c>sessionFromPayload</c> shape in <c>services/network-storage-auth-sessions.js</c>:
/// timestamps are ISO-8601 strings, <c>revokedAt</c> is null unless the session
/// was just revoked.
/// </summary>
public sealed record AuthSessionView(
    string Id,
    string ProjectId,
    long UserId,
    string SteamId,
    string CreatedAt,
    string ExpiresAt,
    string? RevokedAt);

/// <summary>
/// Result of an auth-session operation. On failure carries the Network Storage
/// error <see cref="Code"/> + <see cref="Message"/> (parity with the Bun
/// <c>{ ok, code, message }</c> contract). On success carries the freshly minted
/// <see cref="Token"/> (create/refresh) and/or the decoded <see cref="Session"/>.
/// </summary>
public sealed record AuthSessionOutcome(
    bool Ok,
    string? Code,
    string? Message,
    string? Token,
    int TtlSeconds,
    AuthSessionView? Session)
{
    public static AuthSessionOutcome Failure(string code, string message)
        => new(false, code, message, null, 0, null);
}

/// <summary>
/// Native .NET replacement for <c>services/network-storage-auth-sessions.js</c>.
/// Auth sessions are <b>stateless</b> signed tokens
/// (<c>sbox_sess_&lt;base64url(payload)&gt;.&lt;base64url(HMAC-SHA256(payload))&gt;</c>):
/// there is no server-side session store, so validation re-signs the raw payload
/// substring with the shared secret. Tokens are therefore interchangeable with
/// the Bun implementation as long as both resolve the same secret — making the
/// route cutover safe by construction.
/// </summary>
public interface INetworkStorageAuthSessionService
{
    AuthSessionOutcome Create(long userId, string projectId, string steamId, int ttlSeconds, IReadOnlyDictionary<string, object?>? metadata = null);
    AuthSessionOutcome Validate(string projectId, string token, string steamId = "");
    AuthSessionOutcome Refresh(string projectId, string token, int ttlSeconds);
    AuthSessionOutcome Revoke(string projectId, string token);
}

/// <summary>
/// Supplies the HMAC secret for auth-session tokens. Mirrors the Bun
/// <c>authSessionSecret()</c> env fallback chain
/// (<c>NETWORK_STORAGE_AUTH_SESSION_SECRET</c> → <c>SESSION_SECRET</c> →
/// <c>COOKIE_SECRET</c>). Implemented over <c>IConfiguration</c> in Infrastructure.
/// </summary>
public interface IAuthSessionSecretProvider
{
    string GetSecret();
}

/// <summary>
/// Inputs for an s&amp;box player-auth check, extracted from request headers/query
/// by the endpoint layer so the verifier stays HttpContext-free (and unit-testable).
/// Mirrors Bun <c>checkSboxAuth</c>.
/// </summary>
public sealed record SboxAuthCheck(
    string HostToken,
    string HostSteamId,
    string? ClientSteamId,
    string? ClientToken,
    string? ProxySignature,
    string ApiKey,
    string ProjectId,
    string EndpointSlug);

/// <summary>Outcome of an s&amp;box auth check. <see cref="SteamId"/> is the verified Steam id.</summary>
public sealed record SboxAuthResult(bool Ok, string? SteamId, string? Error);

/// <summary>
/// Verifies s&amp;box player auth tokens (direct and proxied "on-behalf-of").
/// Native port of <c>tools/sbox/auth.js</c> (<c>verifySboxToken</c> /
/// <c>verifyProxyAuth</c>), backed by Facepunch's token service.
/// </summary>
public interface ISboxAuthVerifier
{
    Task<SboxAuthResult> CheckAsync(SboxAuthCheck check, CancellationToken cancellationToken);
}
