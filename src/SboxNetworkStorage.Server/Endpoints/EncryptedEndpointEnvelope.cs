using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Decrypts the signed endpoint envelope the game library sends when a project's
/// security config advertises <c>enableEncryptedRequests</c>
/// (Code/Endpoints/NetworkStorageEncryptedEnvelope.cs): the body is
/// <c>{security, encrypted:true, envelope{...}}</c> and the real input, including
/// <c>_endpointSlug</c> and <c>encryptedRequestId</c>, is AES-256-GCM encrypted
/// with a key derived from the project's public key, then HMAC-SHA256 signed.
/// Wire-compatible with the managed service (services/network-storage-encrypted-requests.js).
/// </summary>
internal static partial class EncryptedEndpointEnvelope
{
    private const string Version = "1";
    private const string Algorithm = "aes-256-gcm+hmac-sha256";
    private const int ReplayWindowSeconds = 120;
    private static readonly ConcurrentDictionary<string, long> ReplayCache = new(StringComparer.Ordinal);
    private static long lastCleanupMs;

    // JSON.stringify output for the ASCII identifiers, base64url and hex strings the envelope carries.
    private static readonly JsonSerializerOptions JsOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal readonly record struct Result(bool Ok, int Status, string? Code, string? Message, JsonElement Payload);

    /// <summary>Returns the envelope object if the body carries one (envelope, encryptedRequest, or inline).</summary>
    public static bool TryRead(JsonElement body, out JsonElement envelope)
    {
        envelope = default;
        if (body.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "envelope", "encryptedRequest" })
        {
            if (body.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                envelope = nested;
                return true;
            }
        }
        if ((body.TryGetProperty("encrypted", out var flag) && flag.ValueKind == JsonValueKind.True)
            || body.TryGetProperty("encryptedPayload", out _) || body.TryGetProperty("ciphertext", out _))
        {
            envelope = body;
            return true;
        }
        return false;
    }

    /// <param name="steamId">Verified player identity to bind a steam envelope to; null skips the binding (secret-key callers).</param>
    /// <param name="sessionId">Id of the validated auth session presented with the request, if any.</param>
    public static Result Decrypt(JsonElement envelope, string publicKey, string projectId, string endpointSlug,
        string? steamId, string? sessionId, DateTimeOffset now)
    {
        var shape = ValidateShape(envelope, publicKey, projectId, endpointSlug, steamId, sessionId);
        if (shape is not null) return shape.Value;
        try
        {
            var iv = DecodeBytes(Str(envelope, "iv"));
            if (iv.Length != 12) return Invalid("Encrypted request iv must be 12 bytes.");
            var encrypted = DecodeBytes(FirstNonEmpty(Str(envelope, "encryptedPayload"), Str(envelope, "ciphertext")));
            var hasTag = Str(envelope, "tag").Length > 0;
            var tag = hasTag ? DecodeBytes(Str(envelope, "tag")) : encrypted[Math.Max(0, encrypted.Length - 16)..];
            var ciphertext = hasTag ? encrypted : encrypted[..Math.Max(0, encrypted.Length - 16)];
            if (tag.Length != 16 || ciphertext.Length == 0) return Invalid("Encrypted request ciphertext or tag is invalid.");

            var context = StableJson(Context(envelope));
            var key = SHA256.HashData(Encoding.UTF8.GetBytes($"sboxcool.network-storage.encrypted-request.v1\0{publicKey}\0{context}"));
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(context));

            using var document = JsonDocument.Parse(plaintext);
            var payload = document.RootElement.Clone();
            if (payload.ValueKind != JsonValueKind.Object)
                return new Result(false, 400, "INVALID_BODY", "Decrypted request payload must be a JSON object.", default);
            var payloadSlug = Str(payload, "_endpointSlug").Trim();
            if (payloadSlug.Length > 0 && endpointSlug.Length > 0 && !string.Equals(payloadSlug, endpointSlug, StringComparison.Ordinal))
                return Invalid("Encrypted request _endpointSlug does not match the URL.");

            var identity = Identity(envelope);
            var replay = ConsumeRequestId(projectId, $"{identity.Type}:{identity.Value}", Str(payload, "encryptedRequestId"), now);
            if (replay is not null) return replay.Value;
            return new Result(true, 200, null, null, WithoutReservedKeys(payload));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return Invalid($"Encrypted request payload could not be decrypted: {ex.Message}");
        }
    }

    private static Result? ValidateShape(JsonElement envelope, string publicKey, string projectId, string endpointSlug,
        string? steamId, string? sessionId)
    {
        if (!string.Equals(Str(envelope, "projectId"), projectId, StringComparison.Ordinal))
            return Invalid("Encrypted request projectId does not match the URL.");
        var envelopeSlug = Str(envelope, "endpointSlug");
        if (envelopeSlug.Length > 0 && endpointSlug.Length > 0 && envelopeSlug != endpointSlug)
            return Invalid("Encrypted request endpointSlug does not match the URL.");
        if (FirstNonEmpty(Str(envelope, "version"), Version) != Version)
            return Invalid("Unsupported encrypted request envelope version.");
        if (FirstNonEmpty(Str(envelope, "algorithm"), Algorithm) != Algorithm)
            return Invalid("Unsupported encrypted request algorithm.");
        if (Str(envelope, "nonce").Length < 8)
            return Invalid("Encrypted request nonce is missing or too short.");
        if (Str(envelope, "publicKeyFingerprint") != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey))).ToLowerInvariant()[..32])
            return Invalid("Encrypted request public key fingerprint does not match the API key.");
        if (Str(envelope, "iv").Length == 0 || FirstNonEmpty(Str(envelope, "encryptedPayload"), Str(envelope, "ciphertext")).Length == 0
            || Str(envelope, "signature").Length == 0)
            return Invalid("Encrypted request envelope is missing iv, encryptedPayload, or signature.");

        var identity = Identity(envelope);
        if (identity.Type.Length == 0)
            return Invalid("Encrypted request envelope must bind a steamId or sessionId.");
        if (identity.Type == "session")
        {
            if (sessionId is null)
                return new Result(false, 401, "AUTH_SESSION_REQUIRED", "Encrypted requests bound to a sessionId require a valid auth session token.", default);
            if (identity.Value != sessionId)
                return new Result(false, 401, "AUTH_SESSION_STEAMID_MISMATCH", "Encrypted request sessionId does not match the auth session.", default);
        }
        else if (steamId is not null && identity.Value != steamId)
        {
            return Invalid("Encrypted request steamId does not match the authenticated Steam ID.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(publicKey));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(StableJson(SignatureBinding(envelope))));
        byte[] presented;
        try { presented = Convert.FromHexString(Str(envelope, "signature")); }
        catch (FormatException) { return Invalid("Encrypted request signature does not match the envelope."); }
        return CryptographicOperations.FixedTimeEquals(presented, expected)
            ? null
            : Invalid("Encrypted request signature does not match the envelope.");
    }

    private static Result? ConsumeRequestId(string projectId, string identityKey, string requestId, DateTimeOffset now)
    {
        var match = RequestIdPattern().Match(requestId.Trim());
        if (!match.Success)
            return new Result(false, 400, "ENCRYPTED_REQUEST_INVALID_ID", "encryptedRequestId must use the format {unixSeconds}_{random6plus}.", default);
        var issued = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var nowSeconds = now.ToUnixTimeSeconds();
        if (Math.Abs(nowSeconds - issued) > ReplayWindowSeconds)
            return new Result(false, 400, "ENCRYPTED_REQUEST_STALE", $"encryptedRequestId is outside the accepted {ReplayWindowSeconds} second replay window.", default);

        var nowMs = now.ToUnixTimeMilliseconds();
        if (nowMs - Interlocked.Read(ref lastCleanupMs) >= 10_000)
        {
            Interlocked.Exchange(ref lastCleanupMs, nowMs);
            foreach (var entry in ReplayCache)
                if (entry.Value <= nowMs) ReplayCache.TryRemove(entry.Key, out _);
        }
        return ReplayCache.TryAdd($"{projectId}:{identityKey}:{requestId.Trim()}", (issued + ReplayWindowSeconds) * 1000)
            ? null
            : new Result(false, 409, "ENCRYPTED_REQUEST_REPLAY_DETECTED", "This encrypted request id has already been accepted for this identity.", default);
    }

    private static (string Type, string Value) Identity(JsonElement envelope)
    {
        var sessionId = Str(envelope, "sessionId").Trim();
        if (sessionId.Length > 0) return ("session", sessionId);
        var steamId = Str(envelope, "steamId").Trim();
        return steamId.Length > 0 ? ("steam", steamId) : ("", "");
    }

    private static SortedDictionary<string, string> Context(JsonElement envelope)
    {
        var identity = Identity(envelope);
        var context = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = FirstNonEmpty(Str(envelope, "version"), Version),
            ["algorithm"] = FirstNonEmpty(Str(envelope, "algorithm"), Algorithm),
            ["projectId"] = Str(envelope, "projectId"),
            ["identityType"] = identity.Type,
            ["identity"] = identity.Value,
            ["nonce"] = Str(envelope, "nonce"),
            ["publicKeyFingerprint"] = Str(envelope, "publicKeyFingerprint"),
        };
        var slug = Str(envelope, "endpointSlug");
        if (slug.Length > 0) context["endpointSlug"] = slug;
        return context;
    }

    // Identity and auth fields inside the encrypted payload never become endpoint input.
    private static readonly HashSet<string> ReservedPayloadKeys = new(StringComparer.Ordinal)
    {
        "steamId", "apiKey", "token", "authSessionToken", "sessionToken", "encryptedRequestId",
    };

    private static JsonElement WithoutReservedKeys(JsonElement payload)
    {
        var stripped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
            if (!ReservedPayloadKeys.Contains(property.Name)) stripped[property.Name] = property.Value;
        return JsonSerializer.SerializeToElement(stripped);
    }

    private static SortedDictionary<string, string> SignatureBinding(JsonElement envelope)
    {
        var binding = Context(envelope);
        binding["iv"] = Str(envelope, "iv");
        binding["encryptedPayload"] = FirstNonEmpty(Str(envelope, "encryptedPayload"), Str(envelope, "ciphertext"));
        binding["tag"] = Str(envelope, "tag");
        return binding;
    }

    private static string StableJson(SortedDictionary<string, string> value) => JsonSerializer.Serialize(value, JsOptions);

    private static byte[] DecodeBytes(string value)
    {
        if (value.Length == 0) throw new FormatException("value is required.");
        if (value.Length % 2 == 0 && value.All(Uri.IsHexDigit)) return Convert.FromHexString(value);
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => "",
            }
            : "";

    private static string FirstNonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static Result Invalid(string message) => new(false, 401, "REQUEST_SIGNATURE_INVALID", message, default);

    [GeneratedRegex("^([0-9]{9,11})_([A-Za-z0-9_-]{6,})$")]
    private static partial Regex RequestIdPattern();
}
