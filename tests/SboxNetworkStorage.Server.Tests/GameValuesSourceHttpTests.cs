using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

public abstract class GameValuesSourceHttpTests<TFactory>(TFactory factory) : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string MapSource = """
        sourceVersion: 1
        kind: collection
        id: game_values
        name: Game Values
        definition:
          collectionType: global
          accessMode: public
          constants:
            economy:
              startingGold: 7
          tables:
            upgrades:
              columns: [ id, baseCost, costGrowth ]
              rows:
                - [ pickaxe, 10, 1.25 ]
        """;

    private const string ArraySource = """
        sourceVersion: 1
        kind: collection
        id: game_values
        visibility: public
        constants:
          - id: economy
            entries:
              startingGold: 7
        tables:
          - id: upgrades
            columns:
              - key: id
                type: string
              - key: baseCost
                type: number
              - key: costGrowth
                type: number
            rows:
              - id: pickaxe
                baseCost: 10
                costGrowth: 1.25
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceGroupsAndRowsReachPublicClientsAndEndpointLookups(bool legacyArrays)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Source values", keyMode: "public");
        using var client = factory.CreateClient();
        await PushAsync(client, project, legacyArrays ? ArraySource : MapSource);
        using var response = await ValuesAsync(client, project, project.PublicKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var values = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, values.GetProperty("groups").GetProperty("economy").GetProperty("values").GetProperty("startingGold").GetInt32());
        var table = values.GetProperty("tables").GetProperty("upgrades");
        Assert.Equal("pickaxe", table.GetProperty("rows")[0].GetProperty("id").GetString());
        Assert.Equal(10, table.GetProperty("rows")[0].GetProperty("baseCost").GetInt32());
        Assert.Equal(1.25, table.GetProperty("rows")[0].GetProperty("costGrowth").GetDouble());
        Assert.Equal("number", table.GetProperty("columns")[1].GetProperty("type").GetString());
        using var endpoint = await ManageAsync(client, project, "endpoints", new[] { new
        {
            id = "read-values", slug = "read-values", method = "POST", enabled = true, exposure = "public",
            steps = new object[]
            {
                new { id = "one", type = "lookup", source = "values", table = "upgrades", where = new { field = "id", op = "==", value = "pickaxe" }, required = true },
                new { id = "many", type = "lookup_many", source = "values", table = "upgrades", keyField = "id", keys = new[] { "pickaxe" }, asMap = false },
            },
            response = new { status = 200, body = new { gold = "{{values.economy.startingGold}}", cost = "{{one.baseCost}}", rows = "{{many}}" } },
        } });
        Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/read-values") { Content = JsonContent.Create(new { steamId = "76561198000000001" }) };
        request.Headers.Add("x-api-key", project.PublicKey);
        using var called = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, called.StatusCode);
        var result = await called.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, result.GetProperty("gold").GetInt32());
        Assert.Equal(10, result.GetProperty("cost").GetInt32());
        Assert.Equal("pickaxe", result.GetProperty("rows")[0].GetProperty("id").GetString());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPrivateSourceCannotBeExposedByPublicAccessModeOrDuplicateItems(bool legacyArrays)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Private source values");
        using var client = factory.CreateClient();
        var source = legacyArrays ? ArraySource.Replace("visibility: public", "visibility: private") : MapSource + "\nvisibility: private\n";
        await PushAsync(client, project, source);
        using var items = await ManageAsync(client, project, "game-values", new { items = new object[]
        {
            new { id = "economy", type = "group", entries = new { startingGold = 999 } },
            new { id = "upgrades", type = "table", columns = Array.Empty<object>(), rows = new[] { new { id = "leaked" } } },
        } });
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        foreach (var key in new[] { project.PublicKey, project.SecretKey })
        {
            using var response = await ValuesAsync(client, project, key);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var values = await response.Content.ReadFromJsonAsync<JsonElement>();
            var privateAllowed = key == project.SecretKey;
            Assert.Equal(privateAllowed, values.GetProperty("groups").TryGetProperty("economy", out var economy));
            Assert.Equal(privateAllowed, values.GetProperty("tables").TryGetProperty("upgrades", out var upgrades));
            if (privateAllowed)
            {
                Assert.Equal(7, economy.GetProperty("values").GetProperty("startingGold").GetInt32());
                Assert.Equal("pickaxe", upgrades.GetProperty("rows")[0].GetProperty("id").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task PinnedSourceValuesKeepTheirPublishedRowsWhileLiveChanges()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Pinned source values");
        using var client = factory.CreateClient();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
                1, project.ProjectId, "revisions", new Dictionary<string, string> { ["revisionEnforcementMode"] = "allow_continue", ["revisionGracePeriodMinutes"] = "60" }, default);
        await PushAsync(client, project, MapSource);
        await PublishAsync(client, project, 41);
        await PushAsync(client, project, MapSource + "\n");
        var changed = MapSource.Replace("- [ pickaxe, 10, 1.25 ]", "- [ pickaxe, 10, 1.25 ]\n        - [ drill, 20, 1.5 ]");
        await PushAsync(client, project, changed);
        await PublishAsync(client, project, 42);
        using var oldResponse = await ValuesAsync(client, project, project.PublicKey, "41");
        using var currentResponse = await ValuesAsync(client, project, project.PublicKey, "42");
        Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        var old = await oldResponse.Content.ReadFromJsonAsync<JsonElement>();
        var current = await currentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "pickaxe" }, old.GetProperty("tables").GetProperty("upgrades").GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        Assert.Equal(new[] { "pickaxe", "drill" }, current.GetProperty("tables").GetProperty("upgrades").GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()));
    }

    private static async Task PushAsync(HttpClient client, SelfHostProject project, string source)
    {
        using var response = await ManageAsync(client, project, "collections", new[] { new { id = "game_values", kind = "collection", sourceFormat = "yaml", sourcePath = "collections/game_values.collection.yml", sourceText = source } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PublishAsync(HttpClient client, SelfHostProject project, long revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = JsonContent.Create(new { packageIdent = "source.audit", currentRevisionId = revision }) };
        request.Headers.Add("x-api-key", project.SecretKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> ManageAsync(HttpClient client, SelfHostProject project, string resource, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/v3/manage/{project.ProjectId}/{resource}") { Content = JsonContent.Create(body) };
        request.Headers.Add("x-api-key", project.SecretKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ValuesAsync(HttpClient client, SelfHostProject project, string key, string? revision = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{project.ProjectId}");
        request.Headers.Add("x-api-key", key);
        if (revision is not null) request.Headers.Add("x-ns-revision-id", revision);
        return client.SendAsync(request);
    }
}

public sealed class SqliteGameValuesSourceHttpTests(SqliteHostFactory factory) : GameValuesSourceHttpTests<SqliteHostFactory>(factory);
public sealed class PostgresGameValuesSourceHttpTests(PostgresHostFactory factory) : GameValuesSourceHttpTests<PostgresHostFactory>(factory);
public sealed class GameValuesSourceDefinitionTests
{
    [Fact]
    public void CompiledSourceValuesAreUsedWithoutRecompilingSourceText()
    {
        using var row = JsonDocument.Parse("""
        {
          "collection_id":"game_values",
          "visibility":"public",
          "definition_json":{
            "sourceText":"not valid yaml: [",
            "sourceFormat":"yaml",
            "constants":[{"id":"economy","entries":{"startingGold":7}}],
            "tables":[]
          }
        }
        """);

        var response = GameValuesHandler.ToClientFormatFromStoreRows(null, [row.RootElement], includePrivate: false);
        var values = JsonSerializer.SerializeToElement(response);

        Assert.Equal(7, values.GetProperty("groups").GetProperty("economy").GetProperty("values").GetProperty("startingGold").GetInt32());
    }
}
