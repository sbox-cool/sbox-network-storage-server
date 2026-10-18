using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

namespace SboxNetworkStorage.Application.NetworkStorage.Media;

/// <summary>What a grant authorizes. Everything the upload is checked against is inside the signed payload.</summary>
public sealed record MediaGrantClaims(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("pid")] string ProjectId,
    [property: JsonPropertyName("pur")] string Purpose,
    [property: JsonPropertyName("ct")] string ContentType,
    [property: JsonPropertyName("max")] long MaxBytes,
    [property: JsonPropertyName("sha")] string Sha256,
    [property: JsonPropertyName("mid")] string MediaId,
    [property: JsonPropertyName("sid")] string OwnerSteamId,
    [property: JsonPropertyName("exp")] long ExpiresAtUnixMs,
    [property: JsonPropertyName("gid")] string GrantId);

public enum MediaGrantVerification
{
    Ok,
    Malformed,
    BadSignature,
}

/// <summary>
/// Stateless grant tokens: <c>sbox_mg_</c> + base64url(payload JSON) + "." + base64url(HMAC-SHA256(payload)).
/// The HMAC key is a purpose-derived subkey of the server's auth-session secret, so a media grant
/// cannot be replayed as an auth session token and vice versa. Single use is enforced by the store
/// (<see cref="SboxNetworkStorage.Storage.IMediaStore.TryConsumeGrantAsync"/>), not here.
/// </summary>
public sealed class MediaGrantTokens
{
    public const string Prefix = "sbox_mg_";
    public const int TtlSeconds = 120;
    private const string KeyLabel = "sbox-network-storage/media-grant/v1";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IAuthSessionSecretProvider _secrets;

    public MediaGrantTokens(IAuthSessionSecretProvider secrets) => _secrets = secrets;

    /// <summary>Random 16-byte ids, hex encoded, used for media and grant ids.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public string Issue(MediaGrantClaims claims)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims with { Version = CurrentVersion }, PayloadJson);
        var signature = Sign(payload);
        return Prefix + Base64UrlEncode(payload) + "." + Base64UrlEncode(signature);
    }

    /// <summary>Verifies the token's shape and HMAC. Expiry, use, and binding are the caller's checks.</summary>
    public MediaGrantVerification Verify(string token, out MediaGrantClaims? claims)
    {
        claims = null;
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return MediaGrantVerification.Malformed;
        var parts = token[Prefix.Length..].Split('.');
        if (parts.Length != 2) return MediaGrantVerification.Malformed;

        byte[] payload, signature;
        try
        {
            payload = Base64UrlDecode(parts[0]);
            signature = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return MediaGrantVerification.Malformed;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload)))
            return MediaGrantVerification.BadSignature;

        try
        {
            claims = JsonSerializer.Deserialize<MediaGrantClaims>(payload, PayloadJson);
        }
        catch (JsonException)
        {
            return MediaGrantVerification.Malformed;
        }
        if (claims is null || claims.Version != CurrentVersion || string.IsNullOrEmpty(claims.GrantId))
        {
            claims = null;
            return MediaGrantVerification.Malformed;
        }
        return MediaGrantVerification.Ok;
    }

    private byte[] Sign(byte[] payload)
    {
        var secret = Encoding.UTF8.GetBytes(_secrets.GetSecret());
        var key = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(KeyLabel));
        return HMACSHA256.HashData(key, payload);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 1: throw new FormatException("Invalid base64url length.");
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
