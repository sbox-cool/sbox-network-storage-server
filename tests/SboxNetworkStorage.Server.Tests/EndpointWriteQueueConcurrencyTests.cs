using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Independent concurrent writes to one record (issue #157). With the queue on, every write applies in arrival order.
/// The queue-off case reproduces the CAS conflict the queue replaces: a barrier forces every call to read one version.
/// </summary>
public abstract class EndpointWriteQueueConcurrencyTests<TFactory>(TFactory factory) : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";

    private const string GrantXpYaml = """
        {
          "steps":[
            {"id":"player","type":"read","collection":"players","key":"{{steamId}}"},
            {"id":"reward","type":"write","collection":"players","key":"{{steamId}}","ops":[
              {"op":"inc","path":"xp","value":1}
            ]}
          ],
          "response":{"status":200,"body":{"success":true}}
        }
        """;

    private const string GrantXpScript = """
        export default function handle(req, ns) {
          const player = ns.records.get("players", req.input.steamId);
          const xp = player.xp + 1;
          ns.records.put("players", req.input.steamId, { xp: xp });
          return { ok: true, xp: xp };
        }
        """;

    private static readonly string GrantXpScriptDefinition = JsonSerializer.Serialize(new { runtime = "javascript", script = GrantXpScript });

    [SkippableFact]
    public async Task TenYamlWritesToOneRecordAllApplyWhenQueueIsOn()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        var project = NewProjectId();
        await SeedAsync(store, project, "grant-xp", GrantXpYaml);
        var executor = new EndpointExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => InvokeAsync(executor, project, "grant-xp")));

        Assert.All(results, result => Assert.Equal(200, result?.Status));
        var saved = await store.ReadRecordAsync(project, "players", SteamId, default);
        Assert.NotNull(saved);
        Assert.Equal(10, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(11, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task FiveYamlWritesWithQueueOffLoseAllButOneToVersionConflict()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        var project = NewProjectId();
        await SeedAsync(store, project, "grant-xp", GrantXpYaml);
        var inner = new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance);
        var executor = new EndpointExecutor(new ReadBarrierDataSource(inner, participants: 5), writeQueue: new EndpointWriteQueue(enabled: false));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => InvokeAsync(executor, project, "grant-xp")));

        Assert.Equal(1, results.Count(result => result?.Status == 200));
        Assert.Equal(4, results.Count(result => result?.Status == 409));
        Assert.All(results.Where(result => result?.Status == 409), result => Assert.Contains("CONFLICT", JsonSerializer.Serialize(result!.Body)));
        var saved = await store.ReadRecordAsync(project, "players", SteamId, default);
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(2, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task TwoScriptWritesToOneRecordBothApplyWhenQueueIsOn()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        var project = NewProjectId();
        await SeedAsync(store, project, "grant-xp-script", GrantXpScriptDefinition);
        var executor = ScriptExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance), new ScriptConcurrencyGate(globalLimit: 4, perProjectLimit: 2));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => InvokeAsync(executor, project, "grant-xp-script")));

        Assert.All(results, result => Assert.Equal(200, result?.Status));
        var saved = await store.ReadRecordAsync(project, "players", SteamId, default);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(3, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task FourScriptWritesWithTwoSlotsEachAllApply()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        var project = NewProjectId();
        await SeedAsync(store, project, "grant-xp-script", GrantXpScriptDefinition);
        var gate = new ScriptConcurrencyGate(globalLimit: 4, perProjectLimit: 2, waitTime: TimeSpan.FromSeconds(10));
        var executor = ScriptExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance), gate);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => InvokeAsync(executor, project, "grant-xp-script")));

        Assert.All(results, result => Assert.Equal(200, result?.Status));
        var saved = await store.ReadRecordAsync(project, "players", SteamId, default);
        Assert.NotNull(saved);
        Assert.Equal(4, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(5L, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task ScriptWriteRefusedWhileGateIsFull_IsNotRun()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        var project = NewProjectId();
        await SeedAsync(store, project, "grant-xp-script", GrantXpScriptDefinition);
        var gate = new ScriptConcurrencyGate(globalLimit: 1, perProjectLimit: 1, waitTime: TimeSpan.Zero);
        var executor = ScriptExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance), gate);

        using var held = await gate.EnterAsync(project);
        Assert.NotNull(held);
        var refused = await InvokeAsync(executor, project, "grant-xp-script");

        Assert.Equal(429, refused?.Status);
        var body = JsonSerializer.Serialize(refused!.Body);
        Assert.Contains("SCRIPT_BUSY", body);
        Assert.Contains("\"retrySafe\":true", body);
        Assert.Equal(1, refused!.RetryAfterSeconds);

        // The refused call never ran: the record is still the seeded version 1 with xp 0.
        var saved = await store.ReadRecordAsync(project, "players", SteamId, default);
        Assert.NotNull(saved);
        Assert.Equal(0, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(1L, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task TwentyConcurrentRecordApiIncrementsAllApply()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using (var seeded = await SendRecordAsync(client, HttpMethod.Post, project, new { coins = 0 }))
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await SendRecordAsync(client, HttpMethod.Post, project, new { ops = new[] { new { op = "inc", path = "coins", value = 1 } } });
            return response.StatusCode;
        }));
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));

        using var read = await SendRecordAsync(client, HttpMethod.Get, project, body: null);
        var record = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20d, record.GetProperty("coins").GetDouble());

        var stats = factory.Services.GetRequiredService<EndpointWriteQueue>().GetStats();
        Assert.True(stats.Counters.Completed >= 21, $"record writes should pass through the write queue; completed={stats.Counters.Completed}");
        Assert.Equal(0, stats.ActiveKeys);
        Assert.Equal(0, stats.QueuedRequests);
    }

    private static string NewProjectId() => "wq" + Guid.NewGuid().ToString("N")[..12];

    private static async Task SeedAsync(INetworkStorageStore store, string projectId, string slug, string definitionJson)
    {
        await store.UpsertCollectionAsync(projectId, "players", "Players", "public", Json("""{"collectionType":"per-steamid","requireSaveVersion":true}"""), 1, default);
        await store.UpsertEndpointAsync(projectId, slug, slug, "POST", true, Json(definitionJson), null, 1, default);
        await store.UpsertRecordAsync(projectId, "players", SteamId, Json("""{"xp":0}"""), false, 1, default);
    }

    private static EndpointExecutor ScriptExecutor(StoreEndpointDataSource source, ScriptConcurrencyGate gate) =>
        new(source, scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true), scriptGate: gate);

    private static Task<EndpointExecutionResult?> InvokeAsync(EndpointExecutor executor, string projectId, string slug) =>
        executor.TryExecuteAsync(
            projectId, slug, new Dictionary<string, object?> { ["steamId"] = SteamId }, SteamId, "owner", new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: false, ct: default, liveServe: true);

    private static async Task<HttpResponseMessage> SendRecordAsync(HttpClient client, HttpMethod method, SelfHostProject project, object? body)
    {
        using var request = new HttpRequestMessage(method, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}

public sealed class SqliteEndpointWriteQueueConcurrencyTests(SqliteHostFactory factory) : EndpointWriteQueueConcurrencyTests<SqliteHostFactory>(factory);
public sealed class PostgresEndpointWriteQueueConcurrencyTests(PostgresHostFactory factory) : EndpointWriteQueueConcurrencyTests<PostgresHostFactory>(factory);
