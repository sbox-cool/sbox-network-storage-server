using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.EndpointSpike.Broker;
using SboxNetworkStorage.EndpointSpike.Compiler;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Sandbox;
using SboxNetworkStorage.EndpointSpike.Stores;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.EndpointSpike.Runner;

/// <summary>
/// Runs each scenario twice against a fresh project: once as an in-process dry run through the real
/// <see cref="EndpointExecutor"/> (no persistence), once through the sandbox worker (persists only on success).
/// Records a per-scenario evidence line and a pass/fail verdict against the design's expected outcome.
/// </summary>
public static class Harness
{
    public const string Collection = "players";
    public const string UserId = "spike-user";

    public static async Task<(IReadOnlyList<Dictionary<string, object?>> Evidence, bool AllPassed)> RunAllAsync(
        OpenedStore opened, CompiledEndpoint compiled, CancellationToken ct, WorkerLaunch? launch = null)
    {
        var ping = await opened.Admin.PingAsync(ct);
        var evidence = new List<Dictionary<string, object?>>();
        var allPassed = true;
        foreach (var scenario in FinishCombatScenarios.All())
        {
            var line = await RunScenarioAsync(opened, compiled, scenario, ping.ServerVersion, ct, launch);
            evidence.Add(line);
            allPassed &= line["pass"] is true;
        }
        return (evidence, allPassed);
    }

    public static async Task<Dictionary<string, object?>> RunScenarioAsync(
        OpenedStore opened, CompiledEndpoint compiled, Scenario scenario, string serverVersion, CancellationToken ct,
        WorkerLaunch? launch = null)
    {
        var store = opened.Store;
        var projectId = $"spike-{Guid.NewGuid():N}";
        var slug = compiled.EndpointId;
        var gameValues = FinishCombatScenarios.GameValues();

        await store.UpsertProjectAsync(projectId, PlanReader.ToJsonElement(new Dictionary<string, object?>()), 1, ct);
        await store.UpsertEndpointAsync(projectId, slug, slug, "POST", true,
            PlanReader.ToJsonElement(compiled.Plan), compiled.PlanSha256, 1, ct);
        foreach (var name in DeclaredCollections(compiled.Plan))
        {
            await store.UpsertCollectionAsync(projectId, name, name, "private",
                PlanReader.ToJsonElement(new Dictionary<string, object?>()), 1, ct);
        }
        if (scenario.PlayerPayload is not null)
        {
            await store.UpsertRecordAsync(projectId, Collection, FinishCombatScenarios.SteamId,
                PlanReader.ToJsonElement(scenario.PlayerPayload), deleted: false, version: 1, ct);
        }

        var before = await PersistedAsync(store, projectId, ct);

        Dictionary<string, object?>? dry = null;
        if (scenario.DryRunAllowed)
        {
            var trace = new EndpointDryRunTrace();
            var executor = new EndpointExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance));
            var result = await executor.TryExecuteAsync(projectId, slug, scenario.Input,
                FinishCombatScenarios.SteamId, UserId, gameValues, hasSecretKey: false, isDedicatedServer: false, ct,
                liveServe: false, trace: trace,
                executionTime: DateTimeOffset.FromUnixTimeMilliseconds(FinishCombatScenarios.NowMs));
            dry = result is null
                ? new Dictionary<string, object?> { ["ok"] = false, ["status"] = 0.0, ["body"] = "null", ["pendingWrites"] = 0.0 }
                : new Dictionary<string, object?>
                {
                    ["ok"] = result.Ok,
                    ["status"] = (double)result.Status,
                    ["body"] = CanonicalJson.Serialize(result.Body),
                    ["pendingWrites"] = (double)trace.PendingWrites.Count,
                };
        }
        var afterDry = await PersistedAsync(store, projectId, ct);

        var wire = new Dictionary<string, object?>
        {
            ["plan"] = compiled.Plan,
            ["input"] = scenario.Input,
            ["steamId"] = FinishCombatScenarios.SteamId,
            ["playerKey"] = FinishCombatScenarios.SteamId,
            ["userId"] = UserId,
            ["projectId"] = projectId,
            ["gameValues"] = gameValues,
            ["hasSecretKey"] = false,
            ["isDedicatedServer"] = false,
            ["execTimeMs"] = (double)FinishCombatScenarios.NowMs,
        };
        var broker = new RecordBroker(store, projectId, DeclaredCollections(compiled.Plan));
        var sandbox = await Supervisor.RunAsync(wire, broker, scenario.Diagnostic, ct, launch);
        if (sandbox.Completed && sandbox.Ok)
        {
            await broker.CommitAsync(ct);
        }
        var after = await PersistedAsync(store, projectId, ct);

        var sandboxBody = sandbox.Body is null ? "null" : CanonicalJson.Serialize(sandbox.Body);
        var sandboxEnvelope = new Dictionary<string, object?>
        {
            ["ok"] = sandbox.Ok,
            ["status"] = (double)sandbox.Status,
            ["body"] = sandboxBody,
            ["writes"] = (double)sandbox.Writes.Count,
            ["limit"] = sandbox.LimitCode,
            ["error"] = sandbox.Error,
            ["wallMs"] = (double)sandbox.Stats.WallMs,
            ["cpuMs"] = (double)sandbox.Stats.CpuMs,
            ["peakRssBytes"] = (double)sandbox.Stats.PeakRssBytes,
            ["protocolBytes"] = (double)sandbox.Stats.ProtocolBytes,
        };

        var equal = dry is null
            ? (bool?)null
            : dry["ok"] is bool dryOk && dryOk == sandbox.Ok
              && (double)dry["status"]! == sandbox.Status
              && (string)dry["body"]! == sandboxBody;

        var pass = Verdict(scenario, sandbox, sandboxBody, before, after);
        var dryUnchanged = before == afterDry;

        return new Dictionary<string, object?>
        {
            ["scenario"] = scenario.Name,
            ["provider"] = opened.Provider,
            ["target"] = opened.Target,
            ["serverVersion"] = serverVersion,
            ["languageId"] = CompiledEndpoint.LanguageId,
            ["compilerVersion"] = CompiledEndpoint.CompilerVersion,
            ["sourceSha256"] = compiled.SourceSha256,
            ["planSha256"] = compiled.PlanSha256,
            ["projectId"] = projectId,
            ["dryRun"] = dry,
            ["dryRunPersistedUnchanged"] = dryUnchanged,
            ["sandbox"] = sandboxEnvelope,
            ["equal"] = equal,
            ["persistedBefore"] = before,
            ["persistedAfter"] = after,
            ["pass"] = pass && dryUnchanged && equal != false,
        };
    }

    /// <summary>Expected outcome per scenario. Limit scenarios must persist nothing; rejects must persist nothing.</summary>
    private static bool Verdict(Scenario scenario, SandboxResult sandbox, string sandboxBody, string before, string after)
    {
        if (scenario.ExpectLimit is not null)
        {
            return sandbox.LimitCode == scenario.ExpectLimit && !sandbox.Ok && before == after;
        }
        if (sandbox.LimitCode is not null || sandbox.Error is not null)
        {
            return false;
        }
        if (sandbox.Status != scenario.ExpectStatus || sandbox.Ok != scenario.ExpectOk)
        {
            return false;
        }
        if (scenario.ExpectCode is not null && !sandboxBody.Contains(scenario.ExpectCode, StringComparison.Ordinal))
        {
            return false;
        }
        return scenario.ExpectOk ? before != after : before == after;
    }

    private static IEnumerable<string> DeclaredCollections(Dictionary<string, object?> plan) =>
        (plan["collections"] as List<object?> ?? []).Cast<string>();

    private static async Task<string> PersistedAsync(INetworkStorageStore store, string projectId, CancellationToken ct)
    {
        var row = await store.ReadRecordAsync(projectId, Collection, FinishCombatScenarios.SteamId, ct);
        var payload = RecordBroker.Unwrap(row);
        return payload is null ? "null" : CanonicalJson.Serialize(PlanReader.Convert(payload.Value));
    }
}
