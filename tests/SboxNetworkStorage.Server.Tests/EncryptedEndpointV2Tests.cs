using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Envelope v2 (issue #153): RSA-OAEP-SHA256 wraps a fresh AES-256-GCM key to the server's request-encryption key, and
/// the response is sealed under that same key. The envelope is built here from the C4 wire rules, so client and server
/// drift fails. Confidentiality covers request and response payloads only: bearer and auth tokens still travel in the
/// clear, so HTTPS remains required.
/// </summary>
public abstract class EncryptedEndpointV2Tests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private const string SteamId = "76561198000000002";
    private const string ResponseAadPrefix = "sboxcool.network-storage.encrypted-response.v2";
    private static readonly JsonSerializerOptions StableOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly TFactory factory = new();
    protected EncryptedEndpointV2Tests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);
    public void Dispose() => factory.Dispose();

    [SkippableTheory]
    [InlineData(EncryptedRequestModes.V2Preferred)]
    [InlineData(EncryptedRequestModes.V2Required)]
    public async Task V2Envelope_IsDecryptedAndTheResponseIsSealed(string mode)
    {
        var project = await ProjectAsync(mode);
        var info = CurrentKeyInfo();
        using var serverKey = PublicKeyOf(info);
        var request = CreateV2(serverKey, info.KeyId, project.ProjectId, SteamId, coins: 42);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, request.Envelope));

        var plain = await OpenSealedAsync(response, request);
        Assert.Equal(JsonValueKind.Object, plain.ValueKind);
        Assert.Equal(42, await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task V2Envelope_WithUnknownKeyId_IsRefusedAsConfigStale()
    {
        var project = await ProjectAsync(EncryptedRequestModes.V2Required);
        var info = CurrentKeyInfo();
        using var serverKey = PublicKeyOf(info);
        var request = CreateV2(serverKey, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            project.ProjectId, SteamId, coins: 4);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, request.Envelope));

        await ReadSecurityErrorAsync(response, 409, "SECURITY_CONFIG_STALE");
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task V2Envelope_WithTamperedBinding_IsRefusedAsSignatureInvalid()
    {
        var project = await ProjectAsync(EncryptedRequestModes.V2Required);
        var info = CurrentKeyInfo();
        using var serverKey = PublicKeyOf(info);
        var request = CreateV2(serverKey, info.KeyId, project.ProjectId, SteamId, coins: 9);
        var otherNonce = new Dictionary<string, object>(request.Envelope) { ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)) };
        var zeroTag = new Dictionary<string, object>(request.Envelope) { ["tag"] = Base64Url(new byte[16]) };
        var rewrapped = new Dictionary<string, object>(request.Envelope)
        {
            ["wrappedKey"] = Base64Url(serverKey.Encrypt(RandomNumberGenerator.GetBytes(32), RSAEncryptionPadding.OaepSHA256)),
        };
        using var client = Client(factory);

        foreach (var envelope in new[] { otherNonce, zeroTag, rewrapped })
        {
            using var response = await client.SendAsync(EncryptedCall(project, envelope));
            await ReadSecurityErrorAsync(response, 401, "REQUEST_SIGNATURE_INVALID");
        }
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task V2Envelope_ReplayIsRefusedAndTheRefusalIsSealed()
    {
        var project = await ProjectAsync(EncryptedRequestModes.V2Required);
        var info = CurrentKeyInfo();
        using var serverKey = PublicKeyOf(info);
        var request = CreateV2(serverKey, info.KeyId, project.ProjectId, SteamId, coins: 5);
        using var client = Client(factory);

        using (var first = await client.SendAsync(EncryptedCall(project, request.Envelope)))
            await OpenSealedAsync(first, request);
        using var replayed = await client.SendAsync(EncryptedCall(project, request.Envelope));

        var refusal = await OpenSealedAsync(replayed, request);
        Assert.Equal(409, refusal.GetProperty("status").GetInt32());
        Assert.Equal("ENCRYPTED_REQUEST_REPLAY_DETECTED", refusal.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(5, await StoredCoinsAsync(project));
    }

    [SkippableTheory]
    [InlineData(EncryptedRequestModes.Off, 409, "SECURITY_ENCRYPTION_DISABLED")]
    [InlineData(EncryptedRequestModes.V1, 400, "SECURITY_MODE_UNSUPPORTED")]
    public async Task V2Envelope_IsRefusedOnOffAndV1Projects(string mode, int status, string code)
    {
        var project = await ProjectAsync(mode);
        var info = CurrentKeyInfo();
        using var serverKey = PublicKeyOf(info);
        var request = CreateV2(serverKey, info.KeyId, project.ProjectId, SteamId, coins: 3);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, request.Envelope));

        var root = await ReadSecurityErrorAsync(response, status, code);
        Assert.Equal(mode, root.GetProperty("security").GetProperty("expected").GetProperty("encryptionMode").GetString());
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task PlainBody_IsRefusedOnV2RequiredProject()
    {
        var project = await ProjectAsync(EncryptedRequestModes.V2Required);
        using var client = Client(factory);

        using var response = await client.SendAsync(Call(project, new { coins = 5 }));

        var root = await ReadSecurityErrorAsync(response, 409, "SECURITY_ENCRYPTION_REQUIRED");
        var expected = root.GetProperty("security").GetProperty("expected");
        Assert.Equal("required", expected.GetProperty("encryptedRequests").GetString());
        Assert.Equal(EncryptedRequestModes.V2Required, expected.GetProperty("encryptionMode").GetString());
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableTheory]
    [InlineData(EncryptedRequestModes.Off, 409, "SECURITY_ENCRYPTION_DISABLED")]
    [InlineData(EncryptedRequestModes.V2Required, 409, "SECURITY_ENCRYPTION_REQUIRED")]
    public async Task V1Envelope_IsRefusedWhereV1IsNotAccepted(string mode, int status, string code)
    {
        // Refused at the gate, before any decryption, so the envelope fields need only be present.
        var project = await ProjectAsync(mode);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, LegacyEnvelope("1")));

        await ReadSecurityErrorAsync(response, status, code);
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task UnsupportedEnvelopeVersion_IsRefusedAsModeUnsupported()
    {
        var project = await ProjectAsync(EncryptedRequestModes.V2Preferred);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, LegacyEnvelope("3")));

        await ReadSecurityErrorAsync(response, 400, "SECURITY_MODE_UNSUPPORTED");
        Assert.Null(await StoredCoinsAsync(project));
    }

    private async Task<SelfHostProject> ProjectAsync(string mode)
    {
        var project = await factory.CreateProjectAsync("Encrypted requests v2");
        await factory.UpdateProjectAsync(project.ProjectId, p => p with { EncryptedRequestMode = mode });
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "Players", "public",
            JsonSerializer.SerializeToElement(new { id = "players", name = "Players", collectionType = "per-steamid" }), 1, CancellationToken.None);
        await store.UpsertEndpointAsync(project.ProjectId, "set-coins", "set-coins", "POST", true,
            JsonSerializer.SerializeToElement(new
            {
                id = "set-coins", name = "Set coins", slug = "set-coins", method = "POST", enabled = true,
                steps = new object[]
                {
                    new
                    {
                        id = "save", type = "write", collection = "players", key = "{{steamId}}",
                        ops = new object[] { new { op = "set", path = "coins", value = "{{input.coins}}" } },
                    },
                },
            }), null, 1, CancellationToken.None);
        return project;
    }

    private RequestEncryptionKeyInfo CurrentKeyInfo()
        => factory.Services.GetRequiredService<RequestEncryptionKeyStore>().Current().Current;

    private static RSA PublicKeyOf(RequestEncryptionKeyInfo info)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(info.PublicKeyPem);
        return rsa;
    }

    private static HttpRequestMessage EncryptedCall(SelfHostProject project, Dictionary<string, object> envelope)
        => Call(project, new Dictionary<string, object>
        {
            ["security"] = new Dictionary<string, object> { ["clientMode"] = "encrypted", ["encryptedRequests"] = "required" },
            ["encrypted"] = true,
            ["envelope"] = envelope,
        });

    private static HttpRequestMessage Call(SelfHostProject project, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/set-coins?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("x-steam-id", SteamId);
        return request;
    }

    private async Task<double?> StoredCoinsAsync(SelfHostProject project)
    {
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("coins").ValueKind == JsonValueKind.Number ? doc.GetProperty("coins").GetDouble() : double.Parse(doc.GetProperty("coins").GetString()!);
    }

    /// <summary>Security-class errors answer HTTP 200 with the status in the body (reference epError shape).</summary>
    private static async Task<JsonElement> ReadSecurityErrorAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement.Clone();
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        Assert.Equal(code, root.GetProperty("error").GetProperty("code").GetString());
        return root;
    }

    /// <summary>Opens a v2 sealed response with the request's AES key, bound to its nonce and keyId (C5).</summary>
    private static async Task<JsonElement> OpenSealedAsync(HttpResponseMessage response, V2Request request)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sealedBody = doc.RootElement;
        Assert.True(sealedBody.GetProperty("encrypted").GetBoolean());
        Assert.Equal(2, sealedBody.GetProperty("v").GetInt32());
        Assert.False(sealedBody.TryGetProperty("ok", out _));
        Assert.False(sealedBody.TryGetProperty("status", out _));
        var iv = DecodeBase64Url(sealedBody.GetProperty("iv").GetString()!);
        var tag = DecodeBase64Url(sealedBody.GetProperty("tag").GetString()!);
        var ciphertext = DecodeBase64Url(sealedBody.GetProperty("payload").GetString()!);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(request.AesKey, 16))
            aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes($"{ResponseAadPrefix}\0{request.Nonce}\0{request.KeyId}"));
        using var plain = JsonDocument.Parse(plaintext);
        return plain.RootElement.Clone();
    }

    private sealed record V2Request(Dictionary<string, object> Envelope, byte[] AesKey, string Nonce, string KeyId);

    /// <summary>Builds a v2 envelope for the C4 wire: wrapped AES key, GCM over the payload, AAD = the sorted binding map.</summary>
    private V2Request CreateV2(RSA serverKey, string keyId, string projectId, string steamId, int coins)
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var iv = RandomNumberGenerator.GetBytes(12);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["coins"] = coins,
            ["security"] = new Dictionary<string, object> { ["clientMode"] = "encrypted" },
            ["encryptedRequestId"] = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid():N}",
            ["_endpointSlug"] = "set-coins",
        });
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(aesKey, 16))
            aes.Encrypt(iv, payload, ciphertext, tag, BindingBytes(projectId, steamId, nonce, keyId));
        var envelope = new Dictionary<string, object>
        {
            ["version"] = "2",
            ["algorithm"] = "rsa-oaep-256+aes-256-gcm",
            ["projectId"] = projectId,
            ["keyId"] = keyId,
            ["nonce"] = nonce,
            ["steamId"] = steamId,
            ["wrappedKey"] = Base64Url(serverKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256)),
            ["iv"] = Base64Url(iv),
            ["tag"] = Base64Url(tag),
            ["encryptedPayload"] = Base64Url(ciphertext),
        };
        return new V2Request(envelope, aesKey, nonce, keyId);
    }

    /// <summary>C4 AAD: canonical JSON of the sorted binding map, with the same encoder as the server's StableJson.</summary>
    private static byte[] BindingBytes(string projectId, string steamId, string nonce, string keyId)
    {
        var binding = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "2",
            ["algorithm"] = "rsa-oaep-256+aes-256-gcm",
            ["projectId"] = projectId,
            ["identityType"] = "steam",
            ["identity"] = steamId,
            ["nonce"] = nonce,
            ["keyId"] = keyId,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding, StableOptions));
    }

    /// <summary>An envelope shaped like the other version, used only where the gate refuses before decryption.</summary>
    private static Dictionary<string, object> LegacyEnvelope(string version) => new()
    {
        ["version"] = version,
        ["algorithm"] = "aes-256-gcm+hmac-sha256",
        ["projectId"] = "unused",
        ["nonce"] = "00000000",
        ["publicKeyFingerprint"] = "00",
        ["steamId"] = SteamId,
        ["iv"] = "AAAAAAAAAAAAAAAA",
        ["tag"] = "AAAAAAAAAAAAAAAAAAAAAA",
        ["encryptedPayload"] = "AAAA",
        ["signature"] = "00",
    };

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}

public sealed class SqliteEncryptedEndpointV2Tests : EncryptedEndpointV2Tests<SqliteHostFactory> { }
public sealed class PostgresEncryptedEndpointV2Tests : EncryptedEndpointV2Tests<PostgresHostFactory> { }
