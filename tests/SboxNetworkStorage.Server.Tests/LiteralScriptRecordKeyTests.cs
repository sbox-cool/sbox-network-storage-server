using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class LiteralScriptRecordKeyTests<TFactory>(TFactory factory) : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    [SkippableTheory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public async Task Script_reads_writes_and_deletes_suffix_keys_without_aliasing(string runtime)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        var store = factory.Services.GetRequiredService<INetworkStorageStore>();
        const string script = """
            export default function handle(req, ns) {
              if (req.input.action === 'delete') ns.records.delete('players', 'item_default');
              else {
                ns.records.put('players', 'item', { value: 1 });
                ns.records.put('players', 'item_default', { value: 2 });
              }
              return { base: ns.records.get('players', 'item'), exact: ns.records.get('players', 'item_default') };
            }
            """;
        await store.UpsertEndpointAsync(project.ProjectId, "literal", "literal", "POST", true,
            JsonSerializer.SerializeToElement(new { runtime, script }), null, 1, default);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", project.SecretKey);
        using var written = await client.PostAsJsonAsync($"/v3/endpoints/{project.ProjectId}/literal", new { action = "write" });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var body = await written.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("base").GetProperty("value").GetInt32());
        Assert.Equal(2, body.GetProperty("exact").GetProperty("value").GetInt32());
        var first = await client.GetFromJsonAsync<JsonElement>($"/v3/storage/{project.ProjectId}/players/item");
        var second = await client.GetFromJsonAsync<JsonElement>($"/v3/storage/{project.ProjectId}/players/item_default");
        Assert.Equal(1, first.GetProperty("value").GetInt32());
        Assert.Equal(2, second.GetProperty("value").GetInt32());

        using var deleted = await client.PostAsJsonAsync($"/v3/endpoints/{project.ProjectId}/literal", new { action = "delete" });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var absent = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/item_default");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var surviving = await client.GetFromJsonAsync<JsonElement>($"/v3/storage/{project.ProjectId}/players/item");
        Assert.Equal(1, surviving.GetProperty("value").GetInt32());
    }

    [SkippableFact]
    public async Task Yaml_legacy_default_suffix_keeps_its_existing_meaning()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        var store = factory.Services.GetRequiredService<INetworkStorageStore>();
        using var definition = JsonDocument.Parse("""{"steps":[{"id":"save","type":"write","collection":"players","key":"legacy_default","ops":[{"op":"set","path":"value","value":3}]}]}""");
        await store.UpsertEndpointAsync(project.ProjectId, "legacy", "legacy", "POST", true, definition.RootElement, null, 1, default);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", project.SecretKey);
        using var response = await client.PostAsJsonAsync($"/v3/endpoints/{project.ProjectId}/legacy", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await store.ReadRecordAsync(project.ProjectId, "players", "legacy", default));
        Assert.Null(await store.ReadRecordAsync(project.ProjectId, "players", "legacy_default", default));
    }
}

public sealed class SqliteLiteralScriptRecordKeyTests(SqliteHostFactory factory) : LiteralScriptRecordKeyTests<SqliteHostFactory>(factory);
public sealed class PostgresLiteralScriptRecordKeyTests(PostgresHostFactory factory) : LiteralScriptRecordKeyTests<PostgresHostFactory>(factory);
