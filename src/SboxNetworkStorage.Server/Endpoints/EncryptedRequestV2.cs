using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static SboxNetworkStorage.Server.Endpoints.EncryptedEndpointEnvelope;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Envelope version 2 (issue #153). The request payload is AES-256-GCM encrypted under a fresh per-request key, which
/// is RSA-OAEP-SHA256 wrapped to the server's request-encryption key (<c>encryption.keyId</c> of the signed config).
/// The response is encrypted under that same AES key. GCM replaces v1's API-key HMAC as the integrity check.
/// Confidentiality covers the request and response payloads only: bearer and auth tokens sent in plain headers or the
/// outer <c>security</c> block are not hidden, so HTTPS remains required.
/// </summary>
internal static class EncryptedRequestV2
{
    public const string Version = "2";
    public const string Algorithm = "rsa-oaep-256+aes-256-gcm";

    /// <summary><c>HttpContext.Items</c> slot holding the <see cref="ResponseKey"/> of a request that decrypted as v2.</summary>
    public const string ItemKey = "EncryptedResponse";

    private const string ResponseAadPrefix = "sboxcool.network-storage.encrypted-response.v2";

    /// <summary>The per-request AES key and the values the response is bound to. Never logged or serialized.</summary>
    internal sealed record ResponseKey(byte[] AesKey, string Nonce, string KeyId);

    /// <summary>
    /// Decrypts a v2 envelope with the server's private request-encryption key. Returns the same result shape as v1, so
    /// the caller consumes replay and sets the body as for v1. <paramref name="responseKey"/> is set only on success.
    /// </summary>
    public static Result Decrypt(JsonElement envelope, RSA privateKey, string projectId, string endpointSlug,
        string? steamId, string? sessionId, ILogger? logger, out ResponseKey? responseKey)
    {
        responseKey = null;
        var shape = ValidateShape(envelope, projectId, endpointSlug, steamId, sessionId);
        if (shape is not null) return shape.Value;
        try
        {
            var iv = DecodeBase64Url(Str(envelope, "iv"));
            if (iv.Length != 12) return Undecryptable(logger, projectId, "iv-length");
            var tag = DecodeBase64Url(Str(envelope, "tag"));
            if (tag.Length != 16) return Undecryptable(logger, projectId, "tag-length");
            var ciphertext = DecodeBase64Url(Str(envelope, "encryptedPayload"));
            if (ciphertext.Length == 0) return Undecryptable(logger, projectId, "payload-empty");
            // An RSA-OAEP block is exactly the key size. Reject any other length before the private-key operation.
            var wrappedKey = DecodeBase64Url(Str(envelope, "wrappedKey"));
            if (wrappedKey.Length != privateKey.KeySize / 8) return Undecryptable(logger, projectId, "wrapped-key-length");
            var aesKey = privateKey.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
            if (aesKey.Length != 32) return Undecryptable(logger, projectId, "aes-key-length");

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(aesKey, 16))
                aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(StableJson(BindingOf(envelope))));

            using var document = JsonDocument.Parse(plaintext);
            var payload = document.RootElement.Clone();
            if (payload.ValueKind != JsonValueKind.Object)
                return new Result(false, 400, "INVALID_BODY", "Decrypted request payload must be a JSON object.", default);
            var payloadSlug = Str(payload, "_endpointSlug").Trim();
            if (payloadSlug.Length > 0 && !string.Equals(payloadSlug, endpointSlug, StringComparison.Ordinal))
                return Invalid("Encrypted request _endpointSlug does not match the URL.");

            // Replay is consumed by ConsumeReplay once the endpoint is known to exist (reference order).
            var identity = Identity(envelope);
            responseKey = new ResponseKey(aesKey, Str(envelope, "nonce"), Str(envelope, "keyId"));
            return new Result(true, 200, null, null, WithoutReservedKeys(payload),
                Str(payload, "encryptedRequestId"), $"{identity.Type}:{identity.Value}");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            // Every unwrap, GCM, and parse failure gets the same message. Only the exception type is logged.
            return Undecryptable(logger, projectId, ex.GetType().Name);
        }
    }

    /// <summary>The one client-facing message for a v2 envelope that does not decrypt. The stage is logged, never returned.</summary>
    private const string UndecryptableMessage = "Encrypted request payload could not be decrypted.";

    private static Result Undecryptable(ILogger? logger, string projectId, string stage)
    {
        logger?.LogInformation("Encrypted request for project {ProjectId} did not decrypt at stage {Stage}.", projectId, stage);
        return Invalid(UndecryptableMessage);
    }

    /// <summary>
    /// C5: encrypts a final response body under the request's AES key. The caller writes the result in place of the body;
    /// HTTP status and headers are unchanged.
    /// </summary>
    public static object SealResponse(ResponseKey key, object body, JsonSerializerOptions options)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(body, options);
        var iv = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key.AesKey, 16))
            aes.Encrypt(iv, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes($"{ResponseAadPrefix}\0{key.Nonce}\0{key.KeyId}"));
        return new { encrypted = true, v = 2, iv = Base64Url(iv), tag = Base64Url(tag), payload = Base64Url(ciphertext) };
    }

    private static Result? ValidateShape(JsonElement envelope, string projectId, string endpointSlug, string? steamId,
        string? sessionId)
    {
        if (!string.Equals(Str(envelope, "projectId"), projectId, StringComparison.Ordinal))
            return Invalid("Encrypted request projectId does not match the URL.");
        // v2 always binds the slug. The library sends the slug it routes to, so a missing slug cannot reach a v2 endpoint.
        var envelopeSlug = Str(envelope, "endpointSlug");
        if (envelopeSlug.Length == 0 || endpointSlug.Length == 0 || envelopeSlug != endpointSlug)
            return Invalid("Encrypted request endpointSlug is missing or does not match the URL.");
        if (Str(envelope, "version") != Version)
            return Invalid("Unsupported encrypted request envelope version.");
        if (Str(envelope, "algorithm") != Algorithm)
            return Invalid("Unsupported encrypted request algorithm.");
        if (Str(envelope, "nonce").Length < 8)
            return Invalid("Encrypted request nonce is missing or too short.");
        if (Str(envelope, "keyId").Length == 0 || Str(envelope, "wrappedKey").Length == 0 || Str(envelope, "iv").Length == 0
            || Str(envelope, "tag").Length == 0 || Str(envelope, "encryptedPayload").Length == 0)
            return Invalid("Encrypted request envelope is missing keyId, wrappedKey, iv, tag, or encryptedPayload.");
        return ValidateIdentityBinding(envelope, steamId, sessionId);
    }

    /// <summary>C4 AAD: the string map whose canonical JSON the GCM tag authenticates.</summary>
    private static SortedDictionary<string, string> BindingOf(JsonElement envelope)
    {
        var identity = Identity(envelope);
        var binding = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = Version,
            ["algorithm"] = Algorithm,
            ["projectId"] = Str(envelope, "projectId"),
            ["identityType"] = identity.Type,
            ["identity"] = identity.Value,
            ["nonce"] = Str(envelope, "nonce"),
            ["keyId"] = Str(envelope, "keyId"),
        };
        // Always bound: ValidateShape refuses a v2 envelope without a non-empty endpointSlug that matches the route.
        binding["endpointSlug"] = Str(envelope, "endpointSlug");
        return binding;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Strict base64url (no hex fallback, unlike v1's DecodeBytes): the v2 wire is base64url everywhere.</summary>
    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
