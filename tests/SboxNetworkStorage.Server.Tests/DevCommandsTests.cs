using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The coding-agent backend (<c>sbox-ns dev</c>) behind the MCP development tools, run in process against a real store.
/// </summary>
public sealed class DevCommandsTests : IDisposable
{
    private const string SteamId = "76561198000000042";

    private const string Collection = """
        id: miners
        name: miners
        collectionType: per-steamid
        accessMode: endpoint
        schema:
          type: object
          properties:
            ore: { type: number, default: 0 }
        """;

    private const string Endpoint = """
        id: mine
        slug: mine
        method: POST
        input:
          properties:
            amount: { type: number, default: 1 }
        steps:
          - id: check
            type: condition
            check:
              field: input.amount
              op: gt
              value: 0
            onFail:
              status: 400
              error: INVALID_AMOUNT
              message: Amount must be positive.
          - id: add
            type: write
            collection: miners
            key: "{{steamId}}"
            ops:
              - { op: inc, path: ore, value: "{{input.amount}}" }
        response:
          status: 200
          body: { ok: true }
        """;

    private readonly SqliteHostFactory factory = new();

    public void Dispose() => factory.Dispose();

    private async Task<JsonObject> RunAsync(string tool, object arguments)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await DevCommands.DispatchAsync(tool, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments))!.AsObject(),
            scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<EffectiveConfig>(), CancellationToken.None);
    }

    private async Task<string> ProjectWithMineEndpointAsync()
    {
        var project = await factory.CreateProjectAsync("Dev tools");
        var p = project.ProjectId;
        Assert.True((await RunAsync("definition_save", new { projectId = p, kind = "collection", source = Collection, confirm = $"{p}/collection/miners", target = "live" }))["ok"]!.GetValue<bool>());
        Assert.True((await RunAsync("definition_save", new { projectId = p, kind = "endpoint", source = Endpoint, confirm = $"{p}/endpoint/mine", target = "live" }))["ok"]!.GetValue<bool>());
        return p;
    }

    [Fact]
    public async Task SaveNeedsTheExactConfirmationAndInvalidSourceNeverSaves()
    {
        var project = await factory.CreateProjectAsync("Dev tools");
        var p = project.ProjectId;

        var unconfirmed = await RunAsync("definition_save", new { projectId = p, kind = "collection", source = Collection, confirm = "yes" });
        Assert.Equal("CONFIRM_REQUIRED", unconfirmed["error"]!.GetValue<string>());
        Assert.Contains($"{p}/collection/miners", unconfirmed["message"]!.GetValue<string>());

        var invalid = await RunAsync("definition_save", new { projectId = p, kind = "endpoint", source = Endpoint.Replace("method: POST", "method: PUT"), confirm = $"{p}/endpoint/mine" });
        Assert.False(invalid["ok"]!.GetValue<bool>());
        Assert.Contains(invalid["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "INVALID_METHOD");

        var listed = await RunAsync("definitions_list", new { projectId = p });
        Assert.Empty(listed["collection"]!.AsArray());
        Assert.Empty(listed["endpoint"]!.AsArray());
    }

    [Fact]
    public async Task EndpointDryRunsShowTheWritesButStoreNothing()
    {
        var p = await ProjectWithMineEndpointAsync();

        var ok = await RunAsync("endpoint_test", new { projectId = p, slug = "mine", input = new { amount = 3 }, steamId = SteamId });
        Assert.True(ok["passed"]!.GetValue<bool>());
        Assert.Equal(3, ok["pendingWrites"]![0]!["data"]!["ore"]!.GetValue<double>());

        var rejected = await RunAsync("endpoint_test", new { projectId = p, slug = "mine", input = new { amount = -1 }, expectStatus = 400 });
        Assert.True(rejected["passed"]!.GetValue<bool>());
        Assert.Equal("INVALID_AMOUNT", rejected["result"]!["body"]!["error"]!["code"]!.GetValue<string>());

        Assert.Equal(0, (await RunAsync("data_records", new { projectId = p, collection = "miners" }))["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task RecordWritesNeedTheCurrentVersionAndFollowTheSchema()
    {
        var p = await ProjectWithMineEndpointAsync();
        var confirm = $"{p}/miners/{SteamId}";
        Assert.True((await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId, payload = new { ore = 5 }, confirm }))["ok"]!.GetValue<bool>());
        var version = (await RunAsync("data_record", new { projectId = p, collection = "miners", key = SteamId }))["version"]!.GetValue<long>();

        Assert.Equal("EXPECTED_VERSION_REQUIRED", (await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId, payload = new { ore = 9 }, confirm }))["error"]!.GetValue<string>());
        Assert.Equal("SCHEMA_VALIDATION_FAILED", (await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId, payload = new { ore = "lots" }, expectedVersion = version, confirm }))["error"]!.GetValue<string>());
        Assert.Equal("CONFLICT", (await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId, payload = new { ore = 9 }, expectedVersion = version + 1, confirm }))["error"]!.GetValue<string>());
        Assert.Equal(5, (await RunAsync("data_record", new { projectId = p, collection = "miners", key = SteamId }))["payload"]!["ore"]!.GetValue<double>());

        Assert.True((await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId, payload = new { ore = 9 }, expectedVersion = version, confirm }))["ok"]!.GetValue<bool>());
        Assert.Equal(9, (await RunAsync("data_record", new { projectId = p, collection = "miners", key = SteamId }))["payload"]!["ore"]!.GetValue<double>());

        // data_records pages from the store and filters by key prefix (the Steam ID, or "{steamId}_" for save slots).
        Assert.True((await RunAsync("data_record_write", new { projectId = p, collection = "miners", key = SteamId + "_slot1", payload = new { ore = 1 }, confirm = $"{p}/miners/{SteamId}_slot1" }))["ok"]!.GetValue<bool>());
        var all = await RunAsync("data_records", new { projectId = p, collection = "miners", keyPrefix = SteamId });
        Assert.Equal(2, all["total"]!.GetValue<long>());
        var slots = await RunAsync("data_records", new { projectId = p, collection = "miners", keyPrefix = SteamId + "_" });
        Assert.Equal(1, slots["total"]!.GetValue<long>());
        Assert.Equal(SteamId + "_slot1", slots["records"]![0]!["key"]!.GetValue<string>());
        Assert.Equal("INVALID_ARGUMENT", (await RunAsync("data_records", new { projectId = p, collection = "miners", keyPrefix = "not a key" }))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task EndpointSnippetCallsTheEndpointWithItsDeclaredInputs()
    {
        var p = await ProjectWithMineEndpointAsync();
        var snippet = (await RunAsync("endpoint_snippet", new { projectId = p, slug = "mine" }))["csharp"]!.GetValue<string>();
        Assert.Contains("NetworkStorage.CallEndpoint( \"mine\", new { amount = 1 } )", snippet);
        Assert.Contains("TryGetLastEndpointError( \"mine\"", snippet);
    }
}
