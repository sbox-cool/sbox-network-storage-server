using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

// A `scope: global` call leaves no telemetry: no endpoint.call analytics event, no endpoint.executed or
// request.rejected hook, and no usage metering. The public answer stays the unknown-slug 404 (pinned bytes
// live in ScriptedGlobalEndpointHttpTests).
public abstract class ScriptedGlobalEndpointTelemetryHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string GlobalDefinition = """{"runtime":"javascript","scope":"global","script":"export function coinsOf() { return 1; }"}""";
    private const string PingDefinition = """{"id":"ping","name":"Ping","slug":"ping","method":"POST","enabled":true,"response":{"pong":true}}""";
    private const string Player = "76561198000000001";
    private readonly SelfHostFactory _factory;

    protected ScriptedGlobalEndpointTelemetryHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task Global_call_records_no_analytics_hook_or_usage_while_a_public_call_does()
    {
        var recorder = new Recorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => UseRecorder(services, recorder)));
        var withGlobal = await host.CreateProjectAsync("Global telemetry");
        var withoutGlobal = await host.CreateProjectAsync("Unknown telemetry");
        await UpsertAsync(host, withGlobal.ProjectId, "bank", GlobalDefinition);
        await UpsertAsync(host, withGlobal.ProjectId, "ping", PingDefinition);
        using var client = host.CreateClient();
        var usage = host.Services.GetRequiredService<NetworkStorageUsageTracker>();

        // Control: a public endpoint called by a claimed player reaches every sink. Without it, the empty
        // results for the global call below could mean "recorder is dead" rather than "nothing recorded".
        var pingUsageBefore = usage.GetSnapshot().TotalTrackedRequests;
        var ping = await PostAsync(client, withGlobal, "ping", Player);
        Assert.Equal(HttpStatusCode.OK, ping.Status);
        Assert.Contains(recorder.Analytics.Events, e =>
            e.ProjectId == withGlobal.ProjectId && e.SteamId == Player && e.Slug == "ping" && e.EventType == "endpoint.call");
        Assert.Contains(recorder.Hooks.Events, e => e.ProjectId == withGlobal.ProjectId && e.Hook == "endpoint.executed");
        Assert.Equal(pingUsageBefore + 1, usage.GetSnapshot().TotalTrackedRequests);

        var analyticsBefore = AnalyticsCount(recorder, withGlobal.ProjectId);
        var hooksBefore = HookCount(recorder, withGlobal.ProjectId);
        var usageBefore = usage.GetSnapshot().TotalTrackedRequests;

        var global = await PostAsync(client, withGlobal, "bank", Player);

        Assert.Equal(HttpStatusCode.NotFound, global.Status);
        Assert.Equal(analyticsBefore, AnalyticsCount(recorder, withGlobal.ProjectId));
        Assert.Equal(hooksBefore, HookCount(recorder, withGlobal.ProjectId));
        Assert.Equal(usageBefore, usage.GetSnapshot().TotalTrackedRequests);

        // Control: the unknown slug is the same answer, and it is metered and hooked, so the probes are live.
        var unknownUsageBefore = usage.GetSnapshot().TotalTrackedRequests;
        var unknown = await PostAsync(client, withoutGlobal, "bank", Player);
        Assert.Equal(unknown.Status, global.Status);
        Assert.Equal(unknown.Body, global.Body);
        Assert.Equal(unknownUsageBefore + 1, usage.GetSnapshot().TotalTrackedRequests);
        Assert.Contains(recorder.Hooks.Events, e => e.ProjectId == withoutGlobal.ProjectId && e.Hook == "endpoint.executed");
    }

    [SkippableFact]
    public async Task Blocked_player_gets_the_same_403_and_no_rejection_is_recorded_for_a_global_call()
    {
        var recorder = new Recorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => UseRecorder(services, recorder)));
        var withGlobal = await host.CreateProjectAsync("Global blocked");
        var withoutGlobal = await host.CreateProjectAsync("Unknown blocked");
        await UpsertAsync(host, withGlobal.ProjectId, "bank", GlobalDefinition);
        var blocks = host.Services.GetRequiredService<PlayerBlockService>();
        await blocks.BlockAsync(withGlobal.ProjectId, Player, PlayerBlockService.AllScope, "demo cheat", expiresAtUnixMs: null, "owner-dashboard", CancellationToken.None);
        await blocks.BlockAsync(withoutGlobal.ProjectId, Player, PlayerBlockService.AllScope, "demo cheat", expiresAtUnixMs: null, "owner-dashboard", CancellationToken.None);
        using var client = host.CreateClient();

        var hooksBefore = HookCount(recorder, withGlobal.ProjectId);
        var global = await PostAsync(client, withGlobal, "bank", Player);
        var unknown = await PostAsync(client, withoutGlobal, "bank", Player);

        Assert.Equal(HttpStatusCode.Forbidden, global.Status);
        Assert.Equal(unknown.Status, global.Status);
        Assert.Equal(unknown.Body, global.Body);
        Assert.Equal(hooksBefore, HookCount(recorder, withGlobal.ProjectId));
        // Control: the same block on an unknown slug is recorded as a rejection, so the hook probe is live.
        Assert.Contains(recorder.Hooks.Events, e => e.ProjectId == withoutGlobal.ProjectId && e.Hook == "request.rejected");
    }

    private static void UseRecorder(IServiceCollection services, Recorder recorder)
    {
        services.RemoveAll<IPlayerAnalyticsService>();
        services.AddSingleton<IPlayerAnalyticsService>(recorder.Analytics);
        services.RemoveAll<IModuleHookEmitter>();
        services.AddSingleton<IModuleHookEmitter>(recorder.Hooks);
    }

    private static int AnalyticsCount(Recorder recorder, string projectId)
        => recorder.Analytics.Events.Count(e => e.ProjectId == projectId);

    private static int HookCount(Recorder recorder, string projectId)
        => recorder.Hooks.Events.Count(e => e.ProjectId == projectId);

    private static async Task UpsertAsync(SelfHostFactory host, string projectId, string slug, string definition)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertEndpointAsync(projectId, slug, slug, "POST", true,
            JsonDocument.Parse(definition).RootElement, null, 1, CancellationToken.None);
    }

    private static async Task<Reply> PostAsync(HttpClient client, SelfHostProject project, string slug, string steamId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v3/endpoints/{project.ProjectId}/{slug}", UriKind.Relative))
        {
            Content = JsonContent.Create(new Dictionary<string, object?>()),
        };
        request.Headers.Add("x-api-key", project.PublicKey);
        request.Headers.Add("x-steam-id", steamId);
        using var response = await client.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record Reply(HttpStatusCode Status, string Body);

    private sealed class Recorder
    {
        public RecordingAnalytics Analytics { get; } = new();
        public RecordingHooks Hooks { get; } = new();
    }

    private sealed class RecordingAnalytics : IPlayerAnalyticsService
    {
        public ConcurrentQueue<(string ProjectId, string SteamId, string Slug, string EventType)> Events { get; } = new();

        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordEndpointEventAsync(
            string projectId,
            string steamId,
            string endpointSlug,
            string eventType,
            IReadOnlyDictionary<string, object>? payload,
            IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
            CancellationToken cancellationToken)
        {
            Events.Enqueue((projectId, steamId, endpointSlug, eventType));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHooks : IModuleHookEmitter
    {
        public ConcurrentQueue<ModuleHookEvent> Events { get; } = new();

        public bool TryEmit(ModuleHookEvent hookEvent)
        {
            Events.Enqueue(hookEvent);
            return true;
        }

        public long DroppedEvents => 0;
    }
}

public sealed class SqliteScriptedGlobalEndpointTelemetryHttpTests(SqliteHostFactory factory)
    : ScriptedGlobalEndpointTelemetryHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresScriptedGlobalEndpointTelemetryHttpTests(PostgresHostFactory factory)
    : ScriptedGlobalEndpointTelemetryHttpTests<PostgresHostFactory>(factory);
