using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

internal static class NetworkStorageSecurityConfigBuilder
{
    private const int DefaultAuthSessionTtlSeconds = 3600;
    private const int DefaultEncryptedRequestWindowSeconds = 120;
    private const int TtlSeconds = 60;

    private static readonly Lock SigningLock = new();

    public static JsonElement Build(string projectId, JsonElement project, DateTimeOffset generatedAt, RequestEncryptionKeyStore keyStore, SecuritySigningKeyStore signingKeys)
    {
        var settings = SettingsOf(project);
        var mode = EncryptedRequestModeOf(project);

        var signing = signingKeys.Current();
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
                ["encryptedRequests"] = EncryptedRequestModes.RequiresEncryption(mode) ? "required" : "disabled",
                ["analytics"] = ReadAnalyticsEnabled(settings) ? "enabled" : "disabled",
            },
            ["settings"] = settings,
            ["signing"] = new JsonObject
            {
                ["algorithm"] = "rsa-sha256",
                ["keyId"] = signing.KeyId,
                ["publicKeyPem"] = signing.PublicKeyPem,
                ["publicKeyJwk"] = signing.PublicKeyJwk.DeepClone(),
            },
        };
        var announced = signing.AnnouncedAt(generatedAt);
        if (announced.Count > 0)
        {
            var rotations = new JsonArray();
            foreach (var statement in announced)
            {
                rotations.Add(statement);
            }

            ((JsonObject)payload["signing"]!)["rotations"] = rotations;
        }

        if (EncryptedRequestModes.OffersV2(mode))
        {
            var key = keyStore.Current().Current;
            payload["encryption"] = new JsonObject
            {
                ["algorithm"] = "rsa-oaep-256+aes-256-gcm",
                ["keyId"] = key.KeyId,
                ["publicKeyJwk"] = key.PublicKeyJwk.DeepClone(),
                ["fingerprint"] = key.Fingerprint,
                ["required"] = mode == EncryptedRequestModes.V2Required,
            };
        }

        byte[] signature;
        lock (SigningLock)
        {
            signature = signing.Key.SignData(StableSerialize(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    /// <summary>
    /// The settings the config version hashes and the signed config publishes. Off and v1 projects publish the exact v1
    /// bytes. v2 modes add <c>encryptedRequestMode</c>, so switching between v1 and v2 changes the config version.
    /// </summary>
    internal static JsonObject SettingsOf(JsonElement project)
    {
        var mode = EncryptedRequestModeOf(project);
        var settings = new JsonObject
        {
            ["enableAuthSessions"] = ReadBoolean(project, "enableAuthSessions"),
            ["authSessionTtlSeconds"] = ReadClampedInteger(project, "authSessionTtlSeconds", DefaultAuthSessionTtlSeconds, 60, 86_400),
            ["enableEncryptedRequests"] = EncryptedRequestModes.RequiresEncryption(mode),
            ["encryptedRequestWindowSeconds"] = ReadClampedInteger(project, "encryptedRequestWindowSeconds", DefaultEncryptedRequestWindowSeconds, 30, 600),
            ["analytics"] = ReadAnalyticsSettings(project),
        };
        if (EncryptedRequestModes.OffersV2(mode)) settings["encryptedRequestMode"] = mode;
        return settings;
    }

    /// <summary>Config version the server reports and enforces: the hash the signed config publishes.</summary>
    internal static string ConfigVersionOf(JsonElement project) => HashPrefix(StableSerialize(SettingsOf(project)));

    private static bool ReadBoolean(JsonElement project, string name) =>
        project.ValueKind == JsonValueKind.Object
        && project.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement project, string name) =>
        project.ValueKind == JsonValueKind.Object
        && project.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Effective encrypted-request mode of the raw project JSON: the stored mode first, then the legacy flag.</summary>
    private static string EncryptedRequestModeOf(JsonElement project) =>
        EncryptedRequestModes.Resolve(ReadString(project, "encryptedRequestMode"), ReadBoolean(project, "enableEncryptedRequests"));

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

    /// <summary>
    /// The analytics object the library receives. The custom-event allowlist is always written
    /// (reference defaults when the owner has not set one), so the library never runs with a list
    /// the owner cannot see.
    /// </summary>
    private static JsonObject ReadAnalyticsSettings(JsonElement project)
    {
        var analytics = ReadObject(project, "analytics") ?? new JsonObject();
        var allowed = new JsonArray();
        foreach (var name in PlayerAnalyticsAllowedEvents.ReadStored(analytics)) allowed.Add(JsonValue.Create(name));
        analytics["allowedEventTypes"] = allowed;
        return analytics;
    }

    private static bool ReadAnalyticsEnabled(JsonObject settings) =>
        settings["analytics"] is not JsonObject analytics
        || analytics["enabled"] is not JsonValue enabled
        || !enabled.TryGetValue<bool>(out var value)
        || value;

    internal static string HashPrefix(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()[..16];

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Public entry point for server-side security checks: the config version the signed config publishes.</summary>
public static class NetworkStorageSecurityConfigVersion
{
    /// <summary>Config version for a raw project JSON object (equal to the signed config's <c>configVersion</c>).</summary>
    public static string Of(JsonElement project) => NetworkStorageSecurityConfigBuilder.ConfigVersionOf(project);
}
