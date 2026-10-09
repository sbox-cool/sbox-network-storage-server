using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Contract tests driven by the published game client
/// (sbox-cool/sbox-network-storage, Code/**). Shipped games cannot be updated,
/// so every request here copies the library's exact URL, query and header
/// shapes: <c>?apiKey=&lt;public&gt;</c> on every runtime call, player headers
/// (x-public-key, x-steam-id, x-sbox-token), and dedicated servers sending the
/// secret in <c>x-api-key</c> (storage, manage/validate). The server owns route
/// semantics; the library owns the call shapes pinned here.
/// </summary>
public abstract class LibraryWireCompatibilityTests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private const string SteamId = "76561198000000001";
    private readonly TFactory factory = new();
    protected LibraryWireCompatibilityTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);
    public void Dispose() => factory.Dispose();

    // Code/Auth/NetworkStorageDedicatedServerSecret.cs TryBuildDedicatedStorageHeaders.
    private static HttpRequestMessage DedicatedStorage(HttpMethod method, string url, string publicKey, string secretKey, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", secretKey);
        request.Headers.Add("x-public-key", publicKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<SelfHostProject> ProjectWithPlayersAsync(string name)
    {
        var project = await factory.CreateProjectAsync(name);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "Players", "public",
            JsonSerializer.SerializeToElement(new { id = "players", name = "Players", collectionType = "per-steamid" }), 1, CancellationToken.None);
        return project;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [SkippableFact]
    public async Task SaveGetDeleteDocument_DedicatedSecretHeader()
    {
        var project = await ProjectWithPlayersAsync("Library documents");
        using var client = Client(factory);
        var url = $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}";

        using (var saved = await client.SendAsync(DedicatedStorage(HttpMethod.Post, url, project.PublicKey, project.SecretKey, new { coins = 250 })))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var fetched = await client.SendAsync(DedicatedStorage(HttpMethod.Get, url, project.PublicKey, project.SecretKey)))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            Assert.Equal(250, (await JsonAsync(fetched)).GetProperty("coins").GetDouble());
        }
        using (var deleted = await client.SendAsync(DedicatedStorage(HttpMethod.Delete, url, project.PublicKey, project.SecretKey)))
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var missing = await client.SendAsync(DedicatedStorage(HttpMethod.Get, url, project.PublicKey, project.SecretKey));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // Code/Storage/NetworkStorageCollections.cs UpdateDocument posts {ops:[...]} to the save route.
    [SkippableFact]
    public async Task UpdateDocumentOps_ApplyToStoredDocument()
    {
        var project = await ProjectWithPlayersAsync("Library ops");
        using var client = Client(factory);
        var url = $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}";
        using (await client.SendAsync(DedicatedStorage(HttpMethod.Post, url, project.PublicKey, project.SecretKey,
            new { coins = 10, name = "a", items = Array.Empty<string>() }))) { }

        using var updated = await client.SendAsync(DedicatedStorage(HttpMethod.Post, url, project.PublicKey, project.SecretKey, new
        {
            ops = new object[]
            {
                new { op = "inc", path = "coins", value = 5, source = "pickup" },
                new { op = "push", path = "items", value = "sword" },
                new { op = "set", path = "stats.level", value = 2 },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var fetched = await client.SendAsync(DedicatedStorage(HttpMethod.Get, url, project.PublicKey, project.SecretKey));
        var doc = await JsonAsync(fetched);
        Assert.False(doc.TryGetProperty("ops", out _));
        Assert.Equal(15, doc.GetProperty("coins").GetDouble());
        Assert.Equal("a", doc.GetProperty("name").GetString());
        Assert.Equal("sword", doc.GetProperty("items")[0].GetString());
        Assert.Equal(2, doc.GetProperty("stats").GetProperty("level").GetDouble());

        using var invalid = await client.SendAsync(DedicatedStorage(HttpMethod.Post, url, project.PublicKey, project.SecretKey,
            new { ops = new object[] { new { op = "increment", path = "coins", amount = 1 } } }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("INVALID_OPERATION", (await JsonAsync(invalid)).GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task MissingAndInvalidKey_AreUnauthorized()
    {
        var project = await factory.CreateProjectAsync("Library errors");
        using var client = Client(factory);
        foreach (var url in new[]
        {
            $"/v3/storage/{project.ProjectId}/players/{SteamId}",
            $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey=wrong",
            $"/v3/endpoints/{project.ProjectId}/missing?apiKey=wrong",
        })
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await JsonAsync(response);
            var code = body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var errorCode)
                ? errorCode.GetString()
                : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            Assert.Equal("UNAUTHORIZED", code);
        }
    }

    // Code/Endpoints/NetworkStorageRuntimeSecurityConfigLoad.cs: no auth, parses {config{...}}.
    [SkippableFact]
    public async Task SecurityConfig_ServesSignedConfigShape()
    {
        var project = await factory.CreateProjectAsync("Library security config");
        using var client = Client(factory);
        using var response = await client.GetAsync($"/v3/security-config/{project.ProjectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = (await JsonAsync(response)).GetProperty("config");
        Assert.Equal(project.ProjectId, config.GetProperty("projectId").GetString());
        Assert.True(config.TryGetProperty("configVersion", out _));
        Assert.Equal(JsonValueKind.Object, config.GetProperty("settings").ValueKind);
        Assert.Equal("rsa-sha256", config.GetProperty("signing").GetProperty("algorithm").GetString());
        var jwk = config.GetProperty("signing").GetProperty("publicKeyJwk");
        Assert.False(string.IsNullOrEmpty(jwk.GetProperty("n").GetString()));
        Assert.False(string.IsNullOrEmpty(config.GetProperty("signature").GetString()));
    }

    [SkippableFact]
    public async Task EndpointCallAndGameValues_PublicKeyQuery()
    {
        var project = await factory.CreateProjectAsync("Library endpoints");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            await store.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
                JsonSerializer.SerializeToElement(new
                {
                    id = "ping", name = "Ping", slug = "ping", method = "POST", enabled = true,
                    response = new { pong = true },
                }), null, 1, CancellationToken.None);
        }
        using var client = Client(factory);
        // Code/Endpoints/NetworkStorageEndpointSecurity.cs: {...input, security{...}, _endpointSlug}.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/ping?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new
            {
                security = new { configVersion = "", clientMode = "editor", authSessions = false, encryptedRequests = false },
                _endpointSlug = "ping",
            }),
        };
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("x-ns-client-type", "editor");
        using var called = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, called.StatusCode);

        using var values = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
        Assert.Equal(JsonValueKind.Object, (await JsonAsync(values)).ValueKind);
    }

    // Code/Storage/NetworkStorageQueries.cs and Code/Core/NetworkStorageRevisionInit.cs.
    [SkippableFact]
    public async Task QueryRunAndRevisionInit_HandshakeShapes()
    {
        var project = await ProjectWithPlayersAsync("Library queries");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            await store.UpsertQueryAsync(project.ProjectId, "top-coins", "Top coins", false,
                JsonSerializer.SerializeToElement(new
                {
                    id = "top-coins", name = "Top coins", type = "leaderboard",
                    sources = new[] { new { collectionId = "players" } },
                    config = new { field = "coins", order = "desc", limit = 10 }, cache = new { ttlSeconds = 60 },
                }), 1, CancellationToken.None);
        }
        using var client = Client(factory);
        using var queryRequest = new HttpRequestMessage(HttpMethod.Get, $"/v3/queries/{project.ProjectId}/top-coins?apiKey={project.PublicKey}");
        queryRequest.Headers.Add("x-public-key", project.PublicKey);
        using var query = await client.SendAsync(queryRequest);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);

        using var revisionRequest = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new
            {
                projectId = project.ProjectId, clientType = "game", networkStorageVersion = "1.0",
                isPublishedGameBundle = true, revisionId = 1,
            }),
        };
        revisionRequest.Headers.Add("x-public-key", project.PublicKey);
        revisionRequest.Headers.Add("x-ns-revision-id", "1");
        revisionRequest.Headers.Add("x-ns-client-type", "game");
        using var revision = await client.SendAsync(revisionRequest);
        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
        var handshake = await JsonAsync(revision);
        Assert.True(handshake.GetProperty("ok").GetBoolean());
        Assert.True(handshake.TryGetProperty("revisionOutdated", out _));
        Assert.True(handshake.TryGetProperty("message", out _));
    }

    // Code/Auth/NetworkStorageDedicatedServerSecretValidation.cs: GET manage/validate, no apiKey query.
    [SkippableFact]
    public async Task DedicatedSecretValidate_AcceptsRealSecretRejectsWrongOne()
    {
        var project = await factory.CreateProjectAsync("Library validate");
        using var client = Client(factory);

        using var good = await client.SendAsync(DedicatedStorage(HttpMethod.Get, $"/v3/manage/{project.ProjectId}/validate", project.PublicKey, project.SecretKey));
        var goodBody = await JsonAsync(good);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.False(goodBody.TryGetProperty("error", out _));
        Assert.NotEqual(JsonValueKind.False, goodBody.TryGetProperty("ok", out var ok) ? ok.ValueKind : JsonValueKind.True);

        using var bad = await client.SendAsync(DedicatedStorage(HttpMethod.Get, $"/v3/manage/{project.ProjectId}/validate", project.PublicKey, "sk_wrong"));
        Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
    }
}

public sealed class SqliteLibraryWireCompatibilityTests : LibraryWireCompatibilityTests<SqliteHostFactory> { }
public sealed class PostgresLibraryWireCompatibilityTests : LibraryWireCompatibilityTests<PostgresHostFactory> { }
