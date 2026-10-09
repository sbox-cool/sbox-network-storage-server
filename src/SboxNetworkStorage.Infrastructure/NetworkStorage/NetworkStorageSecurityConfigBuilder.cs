using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

internal static class NetworkStorageSecurityConfigBuilder
{
    private const int DefaultAuthSessionTtlSeconds = 3600;
    private const int DefaultEncryptedRequestWindowSeconds = 120;
    private const int TtlSeconds = 60;

    /// <summary>
    /// The security-config signing key.
    ///
    /// This MUST come from configuration. When it does not, every process generates
    /// its own throwaway keypair, which breaks clients in two ways:
    ///   1. The keyId and public key change on every restart/deploy, so a client that
    ///      cached a config signed by the previous process fails signature verification
    ///      on startup ("security config RSA verification failed").
    ///   2. The legacy server runtime signs with its own separate ephemeral key
    ///      (services/network-storage-security-config.js), so a config published by one
    ///      runtime never verifies against the other's key.
    ///
    /// Set NETWORK_STORAGE_SECURITY_CONFIG_PRIVATE_KEY (PEM, shared with legacy server) to make
    /// signatures stable and cross-runtime verifiable. The ephemeral fallback is kept
    /// so local/dev boots still work, but it is logged loudly as a misconfiguration.
    /// </summary>
    private static readonly Lazy<RSA> SigningKeyLazy = new(LoadSigningKey, isThreadSafe: true);

    private static RSA SigningKey => SigningKeyLazy.Value;

    private static readonly Lock SigningLock = new();

    /// <summary>
    /// True when no signing key was configured and an ephemeral one is in use. Surfaced
    /// so startup diagnostics can warn instead of silently shipping unverifiable configs.
    /// </summary>
    internal static bool UsingEphemeralSigningKey { get; private set; }

    private static RSA LoadSigningKey()
    {
        var pem = Environment.GetEnvironmentVariable("NETWORK_STORAGE_SECURITY_CONFIG_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(pem))
        {
            try
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(pem.Replace("\\n", "\n"));
                return rsa;
            }
            catch (Exception ex)
            {
                // A malformed configured key is a deployment error. Fail loudly rather
                // than silently falling back to an ephemeral key that no client can verify.
                throw new InvalidOperationException(
                    "NETWORK_STORAGE_SECURITY_CONFIG_PRIVATE_KEY is set but could not be parsed as a PEM RSA private key.", ex);
            }
        }

        UsingEphemeralSigningKey = true;
        return RSA.Create(2048);
    }

    private static string SigningKeyId(string publicKeyPem)
    {
        var configured = Environment.GetEnvironmentVariable("NETWORK_STORAGE_SECURITY_CONFIG_KEY_ID");
        return !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : HashPrefix(Encoding.UTF8.GetBytes(publicKeyPem));
    }

    public static JsonElement Build(string projectId, JsonElement project, DateTimeOffset generatedAt)
    {
        var settings = new JsonObject
        {
            ["enableAuthSessions"] = ReadBoolean(project, "enableAuthSessions"),
            ["authSessionTtlSeconds"] = ReadClampedInteger(project, "authSessionTtlSeconds", DefaultAuthSessionTtlSeconds, 60, 86_400),
            ["enableEncryptedRequests"] = ReadBoolean(project, "enableEncryptedRequests"),
            ["encryptedRequestWindowSeconds"] = ReadClampedInteger(project, "encryptedRequestWindowSeconds", DefaultEncryptedRequestWindowSeconds, 30, 600),
            ["analytics"] = ReadObject(project, "analytics") ?? new JsonObject(),
        };

        var publicKeyPem = SigningKey.ExportSubjectPublicKeyInfoPem();
        var parameters = SigningKey.ExportParameters(includePrivateParameters: false);
        var configVersion = HashPrefix(StableSerialize(settings));
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectId"] = projectId,
            ["configVersion"] = configVersion,
            ["generatedAt"] = generatedAt.ToString("O"),
            ["ttlSeconds"] = TtlSeconds,
            ["modes"] = new JsonObject
            {
                ["authSessions"] = ReadBoolean(project, "enableAuthSessions") ? "enabled" : "disabled",
                ["encryptedRequests"] = ReadBoolean(project, "enableEncryptedRequests") ? "required" : "disabled",
                ["analytics"] = ReadAnalyticsEnabled(settings) ? "enabled" : "disabled",
            },
            ["settings"] = settings,
            ["signing"] = new JsonObject
            {
                ["algorithm"] = "rsa-sha256",
                ["keyId"] = SigningKeyId(publicKeyPem),
                ["publicKeyPem"] = publicKeyPem,
                ["publicKeyJwk"] = new JsonObject
                {
                    ["kty"] = "RSA",
                    ["n"] = Base64UrlEncode(parameters.Modulus ?? []),
                    ["e"] = Base64UrlEncode(parameters.Exponent ?? []),
                },
            },
        };

        byte[] signature;
        lock (SigningLock)
        {
            signature = SigningKey.SignData(StableSerialize(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        payload["signature"] = Base64UrlEncode(signature);
        return JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>
    /// Serializer options for the signed payload.
    ///
    /// The signature covers the UTF-8 bytes of this serialization, so it must match
    /// byte-for-byte what the client (and the legacy server signer, which uses JSON.stringify)
    /// produces. Utf8JsonWriter's DEFAULT encoder escapes far more than JSON.stringify
    /// does — notably the plus and slash characters, which it emits as six-character
    /// unicode escapes. Those two characters are everywhere in the base64 publicKeyPem
    /// and JWK modulus that sit inside the signed payload, so the default encoder
    /// produced bytes no JS-side verifier could reproduce, and verification failed even
    /// with a correctly shared key. UnsafeRelaxedJsonEscaping matches JSON.stringify.
    /// </summary>
    private static readonly JsonWriterOptions StableWriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    internal static byte[] StableSerialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, StableWriterOptions))
        {
            WriteStable(writer, node);
        }

        return stream.ToArray();
    }

    private static void WriteStable(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteStable(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteStable(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool ReadBoolean(JsonElement project, string name) =>
        project.ValueKind == JsonValueKind.Object
        && project.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int ReadClampedInteger(JsonElement project, string name, int fallback, int minimum, int maximum)
    {
        if (project.ValueKind != JsonValueKind.Object
            || !project.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            return fallback;
        }

        return Math.Clamp(number, minimum, maximum);
    }

    private static JsonObject? ReadObject(JsonElement project, string name) =>
        project.ValueKind == JsonValueKind.Object
        && project.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText()) as JsonObject
            : null;

    private static bool ReadAnalyticsEnabled(JsonObject settings) =>
        settings["analytics"] is not JsonObject analytics
        || analytics["enabled"] is not JsonValue enabled
        || !enabled.TryGetValue<bool>(out var value)
        || value;

    private static string HashPrefix(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()[..16];

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
