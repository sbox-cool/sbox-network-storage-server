using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

public abstract class OwnerAuthoringTests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private readonly TFactory factory = new();
    protected OwnerAuthoringTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);
    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task AllAuthoringAndActivityRoutesRequireOwnerAndWritesRequireAntiforgery()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Owner authoring");
        var root = $"/dashboard/projects/{project.ProjectId}";
        using var anonymous = Client(factory);
        foreach (var suffix in new[] { "resources/collection", "resources/endpoint", "resources/workflow", "resources/query", "resources/game-values", "activity/analytics", "activity/logs", "activity/errors", "activity/usage" })
            Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(root + "/" + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.PostAsync(root + "/resources/game-values", Form(("definition", "{}")))).StatusCode);
        using var owner = await LoggedInClientAsync(factory);
        foreach (var kind in new[] { "collection", "endpoint", "workflow", "query", "game-values" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(root + "/resources/" + kind, Form(("definition", "{}")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/dashboard/projects/missing/resources/query")).StatusCode);
    }

    [SkippableFact]
    public async Task SourceBackedResourcesCompileAndRetainMetadata()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Source authoring");
        using var client = await LoggedInClientAsync(factory);
        foreach (var (kind, id, source) in new[]
        {
            ("collection", "player_stats", "kind: collection\nid: player_stats\nname: player_stats\ncollectionType: per-steamid\nschema: {}"),
            ("endpoint", "health", "kind: endpoint\nid: health\nslug: health\nmethod: GET\nenabled: true\nresponse:\n  ok: true"),
            ("workflow", "setup", "kind: workflow\nid: setup\nname: Setup\nsteps: []"),
            ("query", "scores", "kind: query\nid: scores\nname: Scores\ntype: count\nsources:\n  - collectionId: player_stats\nconfig:\n  limit: 20")
        })
        {
            var url = $"/dashboard/projects/{project.ProjectId}/resources/{kind}";
            var page = await client.GetStringAsync(url);
            var definition = JsonSerializer.Serialize(new { id, sourceText = source, sourceFormat = "yaml", sourcePath = $"{kind}/{id}.yaml", authoringMode = "source", sourceVersion = 1 });
            using var response = await client.PostAsync(url, Form(("definition", definition), ("__RequestVerificationToken", Csrf(page))));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            await using var scope = factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            var row = kind switch
            {
                "collection" => await store.ReadCollectionAsync(project.ProjectId, id, CancellationToken.None),
                "endpoint" => await store.ReadEndpointAsync(project.ProjectId, id, CancellationToken.None),
                "workflow" => await store.ReadWorkflowAsync(project.ProjectId, id, CancellationToken.None),
                _ => await store.ReadQueryAsync(project.ProjectId, id, CancellationToken.None)
            };
            Assert.NotNull(row);
            var payload = Column(row!.Value, "definition_json");
            Assert.Equal(source, payload.GetProperty("sourceText").GetString());
            Assert.Equal("yaml", payload.GetProperty("sourceFormat").GetString());
            Assert.Equal(id, payload.GetProperty("id").GetString());
            if (kind == "endpoint") Assert.True(payload.GetProperty("response").GetProperty("ok").GetBoolean());
            var editPage = WebUtility.HtmlDecode(await client.GetStringAsync(url + "?id=" + id));
            Assert.Contains(source, editPage);
            Assert.Contains(id, editPage);
        }
    }
    [SkippableFact]
    public async Task YamlFirstResourcesSaveDisplayAndJsonFallback()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("YAML first");
        using var client = await LoggedInClientAsync(factory);
        var url = $"/dashboard/projects/{project.ProjectId}/resources/collection";
        var template = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("id: player_stats", template);
        Assert.DoesNotContain("data-json-editor", template);
        const string yaml = "id: player_stats\nname: Player stats\ncollectionType: player\nschema:\n  coins:\n    type: number\n";
        var page = await client.GetStringAsync(url);
        using var saved = await client.PostAsync(url, Form(("definition", yaml), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var payload = Column((await store.ReadCollectionAsync(project.ProjectId, "player_stats", CancellationToken.None))!.Value, "definition_json");
        Assert.Equal(yaml, payload.GetProperty("sourceText").GetString());
        Assert.Equal("yaml", payload.GetProperty("sourceFormat").GetString());
        Assert.Equal("dashboard", payload.GetProperty("authoringMode").GetString());
        Assert.Equal("number", payload.GetProperty("schema").GetProperty("coins").GetProperty("type").GetString());
        var editPage = WebUtility.HtmlDecode(await client.GetStringAsync(url + "?id=player_stats"));
        Assert.Contains("coins:", editPage);
        Assert.DoesNotContain("&quot;id&quot;", editPage);
        using var legacy = await client.PostAsync(url, Form(("definition", "{\"id\":\"legacy\",\"name\":\"Legacy\",\"collectionType\":\"player\",\"schema\":{}}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        Assert.NotNull(await store.ReadCollectionAsync(project.ProjectId, "legacy", CancellationToken.None));
        var legacyPage = WebUtility.HtmlDecode(await client.GetStringAsync(url + "?id=legacy"));
        Assert.Contains("id: legacy", legacyPage);
        using var broken = await client.PostAsync(url, Form(("definition", "id: [unclosed"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("id: [unclosed", WebUtility.HtmlDecode(await broken.Content.ReadAsStringAsync()));
    }

    [SkippableFact]
    public async Task InvalidSourceAndIdentityChangesDoNotWriteAndPreserveInput()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Validation");
        using var client = await LoggedInClientAsync(factory);
        var url = $"/dashboard/projects/{project.ProjectId}/resources/collection";
        var page = await client.GetStringAsync(url);
        foreach (var definition in new[] { "{broken", JsonSerializer.Serialize(new { id = "expected", sourceFormat = "yaml", sourceText = "kind: collection\nid: different\nname: different" }) })
        {
            using var response = await client.PostAsync(url, Form(("definition", definition), ("__RequestVerificationToken", Csrf(page))));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(definition, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ListCollectionsAsync(project.ProjectId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task GameValuesRoundTripAndUsageDisplaysOnlyStoredMeasurements()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Live data");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}";
        var page = await client.GetStringAsync(root + "/resources/game-values");
        using var saved = await client.PostAsync(root + "/resources/game-values", Form(("definition", "{\"items\":[{\"id\":\"rewards\",\"type\":\"group\",\"entries\":{\"dailyReward\":25,\"season\":\"autumn\"}}]}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("dailyReward", await client.GetStringAsync(root + "/resources/game-values"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            var row = await store.ReadGameValuesAsync(project.ProjectId, CancellationToken.None);
            Assert.Equal(25, Column(row!.Value, "payload_json").GetProperty("items")[0].GetProperty("entries").GetProperty("dailyReward").GetInt32());
            await store.IncrementProjectUsageAsync(project.ProjectId, "2026-10", "2026-10-08", "health", new UsageDelta(731, 500, 20, 8, 55, 99, 3, 300, 8, 5, 12), CancellationToken.None);
        }
        var usage = await client.GetStringAsync(root + "/activity/usage?month=2026-10");
        Assert.Contains("731", usage);
        Assert.Contains("health", usage);
        var empty = await client.GetStringAsync(root + "/activity/usage?month=2026-09");
        Assert.Contains("No stored activity", empty);
    }

    private static JsonElement Column(JsonElement row, string name)
    {
        var value = row.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? JsonDocument.Parse(value.GetString()!).RootElement.Clone() : value;
    }
}

public sealed class SqliteOwnerAuthoringTests : OwnerAuthoringTests<SqliteHostFactory> { }
public sealed class PostgresOwnerAuthoringTests : OwnerAuthoringTests<PostgresHostFactory> { }
