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

    private async Task<SelfHostProject> ProjectAsync()
    {
        var project = await factory.CreateProjectAsync("Encrypted requests");
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

    private static HttpRequestMessage EncryptedCall(SelfHostProject project, Dictionary<string, object> envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/set-coins?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["security"] = new Dictionary<string, object> { ["clientMode"] = "encrypted", ["encryptedRequests"] = "required" },
                ["encrypted"] = true,
                ["envelope"] = envelope,
            }),
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
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Contains("REQUEST_SIGNATURE_INVALID", await rejected.Content.ReadAsStringAsync());
        }
        var otherPlayer = LibraryEnvelope.Create(project.PublicKey, project.ProjectId, "76561198000000003", "set-coins", 8);
        using (var mismatched = await client.SendAsync(EncryptedCall(project, otherPlayer)))
            Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);
        Assert.Null(await StoredCoinsAsync(project));

        var once = LibraryEnvelope.Create(project.PublicKey, project.ProjectId, SteamId, "set-coins", 9);
        using (var first = await client.SendAsync(EncryptedCall(project, once)))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replayed = await client.SendAsync(EncryptedCall(project, once));
        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
        Assert.Contains("ENCRYPTED_REQUEST_REPLAY_DETECTED", await replayed.Content.ReadAsStringAsync());
    }

    /// <summary>Port of the library's CreateEncryptedEndpointEnvelope (steam identity, no session).</summary>
    private static class LibraryEnvelope
    {
        public static Dictionary<string, object> Create(string apiKey, string projectId, string steamId, string slug, int coins)
        {
            var now = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object>
            {
                ["coins"] = coins,
                ["security"] = new Dictionary<string, object> { ["clientMode"] = "encrypted" },
                ["encryptedRequestId"] = $"{now.ToUnixTimeSeconds()}_{Guid.NewGuid():N}",
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
