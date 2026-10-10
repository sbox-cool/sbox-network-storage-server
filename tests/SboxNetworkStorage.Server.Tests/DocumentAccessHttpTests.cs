using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

// Direct collection document API (GET/POST/DELETE /v3/storage/{pid}/{collection}/{key}
// and /probe) over real HTTP, project/key services and relational persistence.
// Public keys ship in every game build, so they only reach collections declared
// accessMode: public, deletes need allowRecordDelete, and auth-required projects
// verify the player and restrict per-player writes to the player's own keys.
public abstract class DocumentAccessHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Player = "76561198000000001";
    private const string Victim = "76561198000000099";
    private readonly SelfHostFactory _factory;

    protected DocumentAccessHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableTheory]
    [InlineData("GET", "endpoint")]
    [InlineData("POST", "endpoint")]
    [InlineData("POST-ops", "endpoint")]
    [InlineData("DELETE", "endpoint")]
    [InlineData("PROBE", "endpoint")]
    [InlineData("GET", null)]
    [InlineData("POST", null)]
    [InlineData("DELETE", "private")]
    public async Task PublicKeyCannotTouchEndpointOnlyCollection(string method, string? accessMode)
    {
        using var setup = await CreateAsync(required: false, accessMode: accessMode, allowDelete: true);
        using var request = Document(setup, method, Victim);
        using var response = await setup.Client.SendAsync(request);
        await AssertRejectedAsync(response, HttpStatusCode.Forbidden, "ENDPOINT_ONLY");
        await AssertUntouchedAsync(setup);
    }

    [SkippableFact]
    public async Task PublicKeyDeleteNeedsRecordDeletionEnabled()
    {
        using var setup = await CreateAsync(required: false, accessMode: "public", allowDelete: false);
        using var request = Document(setup, "DELETE", Victim);
        using var response = await setup.Client.SendAsync(request);
        await AssertRejectedAsync(response, HttpStatusCode.Forbidden, "RECORD_DELETE_DISABLED");
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData("GET", null)]
    [InlineData("POST", null)]
    [InlineData("POST", "expired-token")]
    [InlineData("DELETE", "expired-token")]
    [InlineData("PROBE", "expired-token")]
    public async Task AuthRequiredProjectRejectsUnverifiedPlayer(string method, string? token)
    {
        using var setup = await CreateAsync(required: true, accessMode: "public", allowDelete: true);
        using var request = Document(setup, method, Victim, steamId: Victim, token: token);
        using var response = await setup.Client.SendAsync(request);
        await AssertRejectedAsync(response, HttpStatusCode.Unauthorized, "SBOX_AUTH_FAILED");
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData("POST")]
    [InlineData("POST-ops")]
    [InlineData("DELETE")]
    public async Task VerifiedPlayerCannotModifyAnotherPlayersDocument(string method)
    {
        using var setup = await CreateAsync(required: true, accessMode: "public", allowDelete: true);
        using var request = Document(setup, method, Victim, steamId: Player, token: "player-token");
        using var response = await setup.Client.SendAsync(request);
        await AssertRejectedAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertUntouchedAsync(setup);
        Assert.Equal(1, setup.Facepunch.Calls);
    }

    [SkippableTheory]
    [InlineData(Player)]
    [InlineData(Player + "_slot1")]
    public async Task VerifiedPlayerWritesAndDeletesOwnDocuments(string key)
    {
        using var setup = await CreateAsync(required: true, accessMode: "public", allowDelete: true);
        using (var saved = await setup.Client.SendAsync(Document(setup, "POST", key, steamId: Player, token: "player-token")))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("written", await StoredMarkerAsync(setup, key));

        using (var read = await setup.Client.SendAsync(Document(setup, "GET", key, steamId: Player, token: "player-token")))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("written", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marker").GetString());
        }

        using (var deleted = await setup.Client.SendAsync(Document(setup, "DELETE", key, steamId: Player, token: "player-token")))
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using (var gone = await setup.Client.SendAsync(Document(setup, "GET", key, steamId: Player, token: "player-token")))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        await AssertUntouchedAsync(setup);
    }

    [SkippableFact]
    public async Task VerifiedPlayerReadsAnotherPlayersPublicDocument()
    {
        using var setup = await CreateAsync(required: true, accessMode: "public", allowDelete: false);
        using var response = await setup.Client.SendAsync(Document(setup, "GET", Victim, steamId: Player, token: "player-token"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("untouched", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marker").GetString());
    }

    [SkippableFact]
    public async Task AuthDisabledProjectKeepsUnverifiedWritesOnPublicCollections()
    {
        using var setup = await CreateAsync(required: false, accessMode: "public", allowDelete: true);
        using var response = await setup.Client.SendAsync(Document(setup, "POST", Victim, steamId: Player, token: "placeholder"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("written", await StoredMarkerAsync(setup, Victim));
        Assert.Equal(0, setup.Facepunch.Calls);
    }

    [SkippableFact]
    public async Task PublicKeyCannotWriteUndeclaredCollection()
    {
        using var setup = await CreateAsync(required: false, accessMode: "public", allowDelete: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/storage/{setup.Project.ProjectId}/undeclared/{Player}")
        {
            Content = JsonContent.Create(new { marker = "written" })
        };
        request.Headers.Add("x-api-key", setup.Project.PublicKey);
        using var response = await setup.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "undeclared", Player, CancellationToken.None));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SecretKeyKeepsFullAccessToEndpointOnlyCollection(bool required)
    {
        using var setup = await CreateAsync(required, accessMode: "endpoint", allowDelete: false);
        using (var saved = await setup.Client.SendAsync(Document(setup, "POST", Victim, secret: true)))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("written", await StoredMarkerAsync(setup, Victim));
        using (var read = await setup.Client.SendAsync(Document(setup, "GET", Victim, secret: true)))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var deleted = await setup.Client.SendAsync(Document(setup, "DELETE", Victim, secret: true)))
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using (var gone = await setup.Client.SendAsync(Document(setup, "GET", Victim, secret: true)))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(0, setup.Facepunch.Calls);
    }

    [SkippableFact]
    public async Task AnalyticsEventObjectContextIsStored()
    {
        using var setup = await CreateAsync(required: false, accessMode: "public", allowDelete: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/storage/{setup.Project.ProjectId}/analytics/events")
        {
            Content = JsonContent.Create(new
            {
                steamId = Player,
                type = "shop_opened",
                severity = "custom",
                context = new { shop = "blacksmith", items = new[] { 1, 2 }, nested = new { depth = 2 } }
            })
        };
        request.Headers.Add("x-api-key", setup.Project.PublicKey);
        using var response = await setup.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        await setup.Factory.Services.GetRequiredService<AnalyticsWriterService>().FlushAsync(CancellationToken.None);
        var events = await setup.Store.ListPlayerEventsAsync(setup.Project.ProjectId, Player, 0, long.MaxValue, 10, CancellationToken.None);
        var stored = Assert.Single(events, e => e.GetProperty("event_type").GetString() == "custom.shop_opened");
        var context = stored.GetProperty("payload_json").GetProperty("payload").GetProperty("context");
        Assert.Equal(JsonValueKind.Object, context.ValueKind);
        Assert.Equal("blacksmith", context.GetProperty("shop").GetString());
        Assert.Equal(2, context.GetProperty("items").GetArrayLength());
        Assert.Equal(2, context.GetProperty("nested").GetProperty("depth").GetInt32());
    }

    private async Task<Setup> CreateAsync(bool required, string? accessMode, bool allowDelete)
    {
        var facepunch = new FacepunchTransport();
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<ISboxAuthVerifier, FacepunchSboxAuthVerifier>().ConfigurePrimaryHttpMessageHandler(() => facepunch)));
        var project = await factory.CreateProjectAsync(requireSboxAuth: required);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "security", new()
            {
                ["requireSboxAuth"] = required ? "true" : "false",
            }, CancellationToken.None);
        var definition = new Dictionary<string, object?>
        {
            ["collectionType"] = "per-steamid",
            ["allowRecordDelete"] = allowDelete,
        };
        if (accessMode is not null) definition["accessMode"] = accessMode;
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "players", "private",
            JsonSerializer.SerializeToElement(definition), 1, CancellationToken.None);
        await store.UpsertRecordAsync(project.ProjectId, "players", Victim,
            JsonSerializer.SerializeToElement(new { marker = "untouched" }), false, 1, CancellationToken.None);
        var before = (await store.ReadRecordAsync(project.ProjectId, "players", Victim, CancellationToken.None))!.Value.GetRawText();
        return new Setup(factory, factory.CreateClient(), project, store, facepunch, before);
    }

    private static HttpRequestMessage Document(Setup setup, string method, string key,
        string? steamId = null, string? token = null, bool secret = false)
    {
        var path = $"/v3/storage/{setup.Project.ProjectId}/players/{key}";
        var request = method switch
        {
            "GET" => new HttpRequestMessage(HttpMethod.Get, path),
            "PROBE" => new HttpRequestMessage(HttpMethod.Get, path + "/probe"),
            "DELETE" => new HttpRequestMessage(HttpMethod.Delete, path),
            "POST-ops" => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { ops = new[] { new { op = "set", path = "marker", value = "written" } } })
            },
            _ => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { marker = "written" }) },
        };
        request.Headers.Add("x-api-key", secret ? setup.Project.SecretKey : setup.Project.PublicKey);
        if (steamId is not null) request.Headers.Add("x-steam-id", steamId);
        if (token is not null) request.Headers.Add("x-sbox-token", token);
        return request;
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("message").GetString()));
    }

    private static async Task AssertUntouchedAsync(Setup setup)
    {
        var victim = await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Victim, CancellationToken.None);
        Assert.Equal(setup.Before, victim!.Value.GetRawText());
    }

    private static async Task<string?> StoredMarkerAsync(Setup setup, string key)
    {
        var row = await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", key, CancellationToken.None);
        return row?.GetProperty("payload_json").GetProperty("marker").GetString();
    }

    private sealed record Setup(SelfHostFactory Factory, HttpClient Client, SelfHostProject Project,
        INetworkStorageStore Store, FacepunchTransport Facepunch, string Before) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }
    }

    private sealed class FacepunchTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var verified = body.RootElement.GetProperty("token").GetString() == "player-token" ? Player : null;
            return new HttpResponseMessage(verified is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(verified is null ? "{}" : $"{{\"Status\":\"ok\",\"SteamId\":{verified}}}", Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class SqliteDocumentAccessHttpTests(SqliteHostFactory factory)
    : DocumentAccessHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresDocumentAccessHttpTests(PostgresHostFactory factory)
    : DocumentAccessHttpTests<PostgresHostFactory>(factory);
