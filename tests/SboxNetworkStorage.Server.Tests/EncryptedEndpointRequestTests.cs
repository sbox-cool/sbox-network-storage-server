using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Projects with enableEncryptedRequests make the published game library send
/// endpoint input only inside a signed AES-256-GCM envelope. The envelope here is
/// built exactly like Code/Endpoints/NetworkStorageEncryptedEnvelope.cs (including
/// its own stable JSON writer) so formatting drift between client and server fails.
/// </summary>
public abstract class EncryptedEndpointRequestTests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private const string SteamId = "76561198000000002";
    private readonly TFactory factory = new();
    protected EncryptedEndpointRequestTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);
    public void Dispose() => factory.Dispose();

    private async Task<SelfHostProject> ProjectAsync(bool encrypted = true)
    {
        var project = await factory.CreateProjectAsync("Encrypted requests");
        if (encrypted) await factory.UpdateProjectAsync(project.ProjectId, p => p with { EncryptedRequestMode = "v1" });
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

    private static HttpRequestMessage EncryptedCall(SelfHostProject project, Dictionary<string, object> envelope, string? configVersion = null)
    {
        var security = new Dictionary<string, object> { ["clientMode"] = "encrypted", ["encryptedRequests"] = "required" };
        if (configVersion is not null) security["configVersion"] = configVersion;
        return Call(project, new Dictionary<string, object>
        {
            ["security"] = security,
            ["encrypted"] = true,
            ["envelope"] = envelope,
        });
    }

    private static HttpRequestMessage PlainCall(SelfHostProject project, object body) => Call(project, body);

    /// <summary>
    /// A library call: <c>x-ns-client-type</c> identifies the library, so endpoint errors use the envelope (HTTP 200,
    /// real status in the body).
    /// </summary>
    private static HttpRequestMessage Call(SelfHostProject project, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/set-coins?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("x-steam-id", SteamId);
        request.Headers.Add("x-ns-client-type", "game");
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

    [SkippableFact]
    public async Task LibraryEnvelope_IsDecryptedIntoEndpointInput()
    {
        var project = await ProjectAsync();
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 42)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(42, await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task TamperedOrReplayedEnvelope_IsRejectedWithoutWriting()
    {
        var project = await ProjectAsync();
        using var client = Client(factory);

        var tampered = LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 7);
        tampered["signature"] = new string('0', 64);
        using (var rejected = await client.SendAsync(EncryptedCall(project, tampered)))
            await ReadSecurityErrorAsync(rejected, 401, "REQUEST_SIGNATURE_INVALID");
        var otherPlayer = LibraryEnvelope.Create(project.PublicKey, project.ProjectId, "76561198000000003", "set-coins", 8);
        using (var mismatched = await client.SendAsync(EncryptedCall(project, otherPlayer)))
            await ReadSecurityErrorAsync(mismatched, 401, "REQUEST_SIGNATURE_INVALID");
        Assert.Null(await StoredCoinsAsync(project));

        var once = LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 9);
        using (var first = await client.SendAsync(EncryptedCall(project, once)))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replayed = await client.SendAsync(EncryptedCall(project, once));
        await ReadSecurityErrorAsync(replayed, 409, "ENCRYPTED_REQUEST_REPLAY_DETECTED");
    }

    [SkippableFact]
    public async Task PlainBody_IsRefusedWhenEncryptionIsRequired()
    {
        var project = await ProjectAsync();
        using var client = Client(factory);

        using var response = await client.SendAsync(PlainCall(project, new { coins = 5 }));

        var root = await ReadSecurityErrorAsync(response, 409, "SECURITY_ENCRYPTION_REQUIRED");
        Assert.Equal("required", root.GetProperty("security").GetProperty("expected").GetProperty("encryptedRequests").GetString());
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task Envelope_IsRefusedWhenEncryptionIsDisabled()
    {
        var project = await ProjectAsync(encrypted: false);
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project, LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 3)));

        await ReadSecurityErrorAsync(response, 409, "SECURITY_ENCRYPTION_DISABLED");
        Assert.Null(await StoredCoinsAsync(project));
    }

    /// <summary>Default-off regression: a project that never enabled encryption keeps accepting plain bodies.</summary>
    [SkippableFact]
    public async Task PlainCall_StillWorksWhenEncryptionIsOff()
    {
        var project = await ProjectAsync(encrypted: false);
        using var client = Client(factory);

        using var response = await client.SendAsync(PlainCall(project, new { coins = 5 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task EnvelopeWithStaleConfigVersion_IsRefusedWithConfigStale()
    {
        var project = await ProjectAsync();
        using var client = Client(factory);

        using var response = await client.SendAsync(EncryptedCall(project,
            LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 4), configVersion: "0000000000000000"));

        await ReadSecurityErrorAsync(response, 409, "SECURITY_CONFIG_STALE");
        Assert.Null(await StoredCoinsAsync(project));
    }

    [SkippableFact]
    public async Task UnknownClientMode_IsRefusedAsUnsupported()
    {
        var project = await ProjectAsync();
        using var client = Client(factory);

        using var response = await client.SendAsync(PlainCall(project, new { coins = 5, security = new { clientMode = "turbo" } }));

        await ReadSecurityErrorAsync(response, 400, "SECURITY_MODE_UNSUPPORTED");
    }

    [SkippableFact]
    public async Task RequireAuthSessions_RefusesCallWithoutSession()
    {
        var project = await ProjectAsync(encrypted: false);
        await factory.UpdateProjectAsync(project.ProjectId, p => p with { EnableAuthSessions = true, RequireAuthSessions = true });
        using var client = Client(factory);

        using var response = await client.SendAsync(PlainCall(project, new { coins = 5 }));

        await ReadSecurityErrorAsync(response, 401, "SECURITY_SESSION_REQUIRED");
        Assert.Null(await StoredCoinsAsync(project));
    }

    /// <summary>The replay window is the project's setting (clamped 30-600), not a fixed 120 seconds.</summary>
    [SkippableFact]
    public async Task ReplayWindow_FollowsProjectSetting()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var client = Client(factory);

        var narrow = await ProjectAsync();
        await factory.UpdateProjectAsync(narrow.ProjectId, p => p with { EncryptedRequestWindowSeconds = 30 });
        using (var stale = await client.SendAsync(EncryptedCall(narrow,
            LibraryEnvelope.Create(narrow.PublicKey, narrow.ProjectId, SteamId, "set-coins", 1, now - 40))))
            await ReadSecurityErrorAsync(stale, 400, "ENCRYPTED_REQUEST_STALE");
        Assert.Null(await StoredCoinsAsync(narrow));

        var wide = await ProjectAsync();
        await factory.UpdateProjectAsync(wide.ProjectId, p => p with { EncryptedRequestWindowSeconds = 600 });
        using var accepted = await client.SendAsync(EncryptedCall(wide,
            LibraryEnvelope.Create(wide.PublicKey, wide.ProjectId, SteamId, "set-coins", 2, now - 200)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, await StoredCoinsAsync(wide));
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

    /// <summary>Port of the library's CreateEncryptedEndpointEnvelope (steam identity, no session).</summary>
    private static class LibraryEnvelope
    {
        public static Dictionary<string, object> Create(string apiKey, string projectId, string steamId, string slug, int coins, long? issuedAt = null)
        {
            var issued = issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new Dictionary<string, object>
            {
                ["coins"] = coins,
                ["security"] = new Dictionary<string, object> { ["clientMode"] = "encrypted" },
                ["encryptedRequestId"] = $"{issued}_{Guid.NewGuid():N}",
                ["_endpointSlug"] = slug,
            };
            var envelope = new Dictionary<string, object>
            {
                ["version"] = "1",
                ["algorithm"] = "aes-256-gcm+hmac-sha256",
                ["projectId"] = projectId,
                ["nonce"] = Base64Url(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))),
                ["publicKeyFingerprint"] = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..32],
                ["steamId"] = steamId,
            };
            var iv = Guid.NewGuid().ToByteArray()[..12];
            envelope["iv"] = Base64Url(iv);
            var context = StableStringify(Context(envelope));
            var key = SHA256.HashData(Encoding.UTF8.GetBytes($"sboxcool.network-storage.encrypted-request.v1\0{apiKey}\0{context}"));
            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
            envelope["tag"] = Base64Url(tag);
            envelope["encryptedPayload"] = Base64Url(ciphertext);
            var binding = Context(envelope);
            binding["iv"] = (string)envelope["iv"];
            binding["encryptedPayload"] = (string)envelope["encryptedPayload"];
            binding["tag"] = (string)envelope["tag"];
            envelope["signature"] = Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(StableStringify(binding))));
            return envelope;
        }

        private static Dictionary<string, object> Context(Dictionary<string, object> envelope) => new()
        {
            ["version"] = envelope["version"],
            ["algorithm"] = envelope["algorithm"],
            ["projectId"] = envelope["projectId"],
            ["identityType"] = "steam",
            ["identity"] = envelope["steamId"],
            ["nonce"] = envelope["nonce"],
            ["publicKeyFingerprint"] = envelope["publicKeyFingerprint"],
        };

        private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

        // Verbatim semantics of the library's WriteStableJson/WriteJsonString.
        private static string StableStringify(Dictionary<string, object> value)
        {
            var sb = new StringBuilder("{");
            var keys = new List<string>(value.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteJsonString(sb, keys[i]);
                sb.Append(':');
                WriteJsonString(sb, Convert.ToString(value[keys[i]]) ?? "");
            }
            return sb.Append('}').ToString();
        }

        private static void WriteJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}

public sealed class SqliteEncryptedEndpointRequestTests : EncryptedEndpointRequestTests<SqliteHostFactory> { }
public sealed class PostgresEncryptedEndpointRequestTests : EncryptedEndpointRequestTests<PostgresHostFactory> { }
