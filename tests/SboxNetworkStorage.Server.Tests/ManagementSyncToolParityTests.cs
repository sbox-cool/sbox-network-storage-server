using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Management routes the published editor Sync Tool calls (Editor/SyncToolApi.cs): it sends the secret key in
/// x-api-key plus x-public-key and treats any 200 as success, so these must really persist or execute.
/// </summary>
public sealed class ManagementSyncToolParityTests
{
    private const string SteamId = "76561198000000123";

    private const string GrantCoins = """
        {"endpoint":{"slug":"grant_coins","method":"POST","enabled":true,"description":"v1",
         "input":{"properties":{"note":{"type":"string","default":"hi"}}},
         "steps":[
           {"id":"player","type":"read","collection":"players","key":"{{steamId}}"},
           {"id":"grant","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"inc","path":"coins","value":5}]}
         ],
         "response":{"status":200,"body":{"ok":true}}}}
        """;

    private static HttpRequestMessage SyncTool(HttpMethod method, string projectId, string path, SelfHostProject project, string? body = null)
    {
        var request = new HttpRequestMessage(method, $"/v3/manage/{projectId}/{path}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("User-Agent", "SyncTool-sbox/2.0");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        using (var response = await client.SendAsync(request))
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static JsonElement Definition(JsonElement? row)
    {
        Assert.NotNull(row);
        var column = row.Value.GetProperty("definition_json");
        return column.ValueKind == JsonValueKind.String ? JsonDocument.Parse(column.GetString()!).RootElement.Clone() : column;
    }

    private static async Task SeedPlayerAsync(HttpClient client, SelfHostProject project, double coins)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Content = JsonContent.Create(new { coins });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<double> StoredCoinsAsync(HttpClient client, SelfHostProject project)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        var (status, body) = await SendAsync(client, request);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("coins").GetDouble();
    }

    [Fact]
    public async Task PatchUpsertsSingleResourcesAndMergesWithoutReplacingOthers()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync("Patch parity");
        using var client = factory.CreateClient();
        var p = project.ProjectId;

        var created = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project, GrantCoins));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal("created", created.Body.GetProperty("action").GetString());
        Assert.Equal("grant_coins", created.Body.GetProperty("resourceId").GetString());

        var other = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project,
            """{"endpoint":{"slug":"health","method":"GET","enabled":true,"steps":[],"response":{"status":200,"body":{"ok":true}}}}"""));
        Assert.Equal(HttpStatusCode.OK, other.Status);

        // Partial payload: merged over the stored definition, steps survive.
        var updated = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project,
            """{"endpoint":{"slug":"grant_coins","description":"v2"}}"""));
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("updated", updated.Body.GetProperty("action").GetString());

        // Source-backed payload, the shape the Sync Tool pushes from .yml files.
        var source = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project, JsonSerializer.Serialize(new
        {
            endpoint = new { id = "from_source", sourceFormat = "yaml", sourcePath = "endpoints/from_source.yml", sourceText = "slug: from_source\nmethod: GET\nsteps: []\nresponse:\n  status: 200\n  body:\n    ok: true\n" },
        })));
        Assert.Equal(HttpStatusCode.OK, source.Status);

        var collection = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "collections", project,
            """{"collection":{"name":"players","collectionType":"per-steamid","schema":{"coins":{"type":"number"}}}}"""));
        Assert.Equal(HttpStatusCode.OK, collection.Status);
        var collectionMerge = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "collections", project,
            """{"collection":{"name":"players","description":"Player wallet"}}"""));
        Assert.Equal("updated", collectionMerge.Body.GetProperty("action").GetString());

        var workflow = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "workflows", project,
            """{"workflow":{"id":"setup","name":"Setup","steps":[]}}"""));
        Assert.Equal(HttpStatusCode.OK, workflow.Status);
        Assert.Equal("setup", workflow.Body.GetProperty("resourceId").GetString());

        var missing = await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project, """{"slug":"no_wrapper"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal("VALIDATION_FAILED", missing.Body.GetProperty("error").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        Assert.Equal(3, (await store.ListEndpointsAsync(p, CancellationToken.None)).Count);
        var grant = Definition(await store.ReadEndpointAsync(p, "grant_coins", CancellationToken.None));
        Assert.Equal("v2", grant.GetProperty("description").GetString());
        Assert.Equal(2, grant.GetProperty("steps").GetArrayLength());
        Assert.Equal("from_source", Definition(await store.ReadEndpointAsync(p, "from_source", CancellationToken.None)).GetProperty("slug").GetString());
        var players = Definition(await store.ReadCollectionAsync(p, "players", CancellationToken.None));
        Assert.Equal("Player wallet", players.GetProperty("description").GetString());
        Assert.Equal("number", players.GetProperty("schema").GetProperty("coins").GetProperty("type").GetString());
        Assert.NotNull(await store.ReadWorkflowAsync(p, "setup", CancellationToken.None));

        // Each endpoint/workflow save is a durable version snapshot.
        var versions = await ManagementProjectObjects.ListVersionsAsync(store, NetworkStorageServices.LocalOwnerUserId, p, "endpoint", "grant_coins", CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.Equal("v2", versions[0].Definition.GetProperty("description").GetString());
        Assert.Equal("patch", versions[0].Source);
    }

    [Fact]
    public async Task PutTestsPersistsAndReplacesTheSavedList()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync("Tests parity");
        using var client = factory.CreateClient();

        var put = await SendAsync(client, SyncTool(HttpMethod.Put, project.ProjectId, "tests", project,
            """[{"id":"t1","name":"Grant works","endpoint":"grant_coins","input":{},"expect":{"outcome":"pass"}},{"name":"No id yet","endpoint":"grant_coins"}]"""));
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(2, put.Body.GetProperty("total").GetInt32());

        var listed = await SendAsync(client, SyncTool(HttpMethod.Get, project.ProjectId, "tests", project));
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        var data = listed.Body.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("t1", data[0].GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(data[1].GetProperty("id").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Put, project.ProjectId, "tests", project,
            """[{"id":"t1","name":"Only one","endpoint":"grant_coins"}]"""))).Status);
        var replaced = await SendAsync(client, SyncTool(HttpMethod.Get, project.ProjectId, "tests", project));
        Assert.Equal(1, replaced.Body.GetProperty("data").GetArrayLength());
        Assert.Equal("Only one", replaced.Body.GetProperty("data")[0].GetProperty("name").GetString());

        var invalid = await SendAsync(client, SyncTool(HttpMethod.Put, project.ProjectId, "tests", project, """{"name":"not a list"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
    }

    [Fact]
    public async Task TestEndpointAndRunTestsExecuteWithoutDurableWrites()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync("Run parity");
        using var client = factory.CreateClient();
        var p = project.ProjectId;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Patch, p, "collections", project,
            """{"collection":{"name":"players","collectionType":"per-steamid","schema":{"coins":{"type":"number"}}}}"""))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Patch, p, "endpoints", project, GrantCoins))).Status);
        await SeedPlayerAsync(client, project, 10);

        // Shape parsed by Editor/TestWindow.cs RunQuickTest + DrawResults.
        var quick = await SendAsync(client, SyncTool(HttpMethod.Post, p, "test-endpoint", project,
            JsonSerializer.Serialize(new { slug = "grant_coins", skipWebhooks = true, input = new { note = "x" }, steamId = SteamId })));
        Assert.Equal(HttpStatusCode.OK, quick.Status);
        var result = quick.Body.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(200, result.GetProperty("status").GetInt32());
        Assert.True(result.GetProperty("timing").GetProperty("total").GetDouble() >= 0);
        Assert.True(quick.Body.GetProperty("expectation").GetProperty("passed").GetBoolean());
        var steps = quick.Body.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "player", "grant" }, steps);
        Assert.Equal(10, quick.Body.GetProperty("steps")[0].GetProperty("result").GetProperty("coins").GetDouble());
        var write = Assert.Single(quick.Body.GetProperty("pendingWrites").EnumerateArray());
        Assert.Equal("players", write.GetProperty("collection").GetString());
        Assert.Equal(15, write.GetProperty("data").GetProperty("coins").GetDouble());
        Assert.Equal(10, await StoredCoinsAsync(client, project));

        // Saved tests run server side (TestWindow.RunAllTests reads results[].passed/name/reason/timing).
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Put, p, "tests", project, JsonSerializer.Serialize(new object[]
        {
            new { id = "grant", name = "Grant adds coins", endpoint = "grant_coins", steamId = SteamId, input = new { }, expect = new { outcome = "pass", status = 200 } },
            new { id = "expects_reject", name = "Wrongly expects a rejection", endpoint = "grant_coins", steamId = SteamId, expect = new { outcome = "fail" } },
            new { id = "missing", name = "Missing endpoint", endpoint = "does_not_exist" },
        })))).Status);
        var all = await SendAsync(client, SyncTool(HttpMethod.Post, p, "run-tests", project, "{}"));
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal(3, all.Body.GetProperty("total").GetInt32());
        Assert.Equal(1, all.Body.GetProperty("passed").GetInt32());
        var results = all.Body.GetProperty("results");
        Assert.True(results[0].GetProperty("passed").GetBoolean());
        Assert.False(results[1].GetProperty("passed").GetBoolean());
        Assert.Contains("Expected a rejection", results[1].GetProperty("reason").GetString());
        Assert.Contains("was not found", results[2].GetProperty("reason").GetString());

        var saved = await SendAsync(client, SyncTool(HttpMethod.Post, p, "test-endpoint", project, """{"testId":"grant"}"""));
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("Grant adds coins", saved.Body.GetProperty("name").GetString());
        Assert.Equal(10, await StoredCoinsAsync(client, project));

        var unknown = await SendAsync(client, SyncTool(HttpMethod.Post, p, "test-endpoint", project, """{"slug":"does_not_exist"}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("ENDPOINT_NOT_FOUND", unknown.Body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RateLimitRulesRoundTripThroughTheManagementApi()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync("Rules parity");
        using var client = factory.CreateClient();
        var p = project.ProjectId;

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Put, p, "rate-limit-rules", project,
            """[{"id":"rl_gold","collection":"*","field":"gold","scope":"per_player","windows":{"perHour":50000},"action":"clamp","enabled":true}]"""))).Status);
        var read = await SendAsync(client, SyncTool(HttpMethod.Get, p, "rate-limit-rules", project));
        var rule = Assert.Single(read.Body.GetProperty("rules").EnumerateArray());
        Assert.Equal("gold", rule.GetProperty("field").GetString());
        Assert.Equal(50000, rule.GetProperty("maxPerHour").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Put, p, "rate-limit-rules", project,
            """[{"id":"rl_gold","collection":"*","field":"gold","scope":"global","windows":{"perDay":9},"action":"reject","enabled":false}]"""))).Status);
        var changed = Assert.Single((await SendAsync(client, SyncTool(HttpMethod.Get, p, "rate-limit-rules", project))).Body.GetProperty("rules").EnumerateArray());
        Assert.Equal("global", changed.GetProperty("scope").GetString());
        Assert.Equal(9, changed.GetProperty("maxPerDay").GetInt32());
        Assert.False(changed.GetProperty("enabled").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, SyncTool(HttpMethod.Delete, p, "rate-limit-rules", project, "{}"))).Status);
        Assert.Empty((await SendAsync(client, SyncTool(HttpMethod.Get, p, "rate-limit-rules", project))).Body.GetProperty("rules").EnumerateArray());
    }
}
