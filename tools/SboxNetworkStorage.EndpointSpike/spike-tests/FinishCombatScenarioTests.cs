using SboxNetworkStorage.EndpointSpike.Compiler;
using SboxNetworkStorage.EndpointSpike.Runner;
using SboxNetworkStorage.EndpointSpike.Sandbox;
using SboxNetworkStorage.EndpointSpike.Stores;

namespace SboxNetworkStorage.EndpointSpike.Tests;

/// <summary>
/// End-to-end: the compiled finish-combat plan runs through the real dry-run executor and the sandboxed worker, against a
/// real SQLite store. The sandbox needs the spike worker in a separate process, so the tests launch the spike assembly
/// explicitly with the dotnet host that ran the tests.
/// </summary>
public sealed class FinishCombatScenarioTests
{
    private const int EightMiB = 8 * 1024 * 1024;

    private static WorkerLaunch Worker() =>
        new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [typeof(SboxNetworkStorage.EndpointSpike.Sandbox.Supervisor).Assembly.Location]);

    private static CompiledEndpoint CompileFixture()
    {
        var outcome = NsEndpointCompiler.Compile(SpikeFixtures.ReadFixture("finish-combat.ns.js"));
        Assert.True(outcome.Ok, outcome.Error?.Message);
        return outcome.Endpoint!;
    }

    [Fact]
    public async Task Every_scenario_meets_its_expectation_on_sqlite()
    {
        var compiled = CompileFixture();
        var opened = await StoreFactory.OpenSqliteAsync(SpikeFixtures.TempDb(), EightMiB, CancellationToken.None);

        var (evidence, allPassed) = await Harness.RunAllAsync(opened, compiled, CancellationToken.None, Worker());

        var failing = evidence.Where(line => line["pass"] is not true).Select(line => line["scenario"]?.ToString()).ToList();
        Assert.True(allPassed, "Failing scenarios: " + string.Join(", ", failing));
        Assert.Equal(FinishCombatScenarios.All().Count, evidence.Count);
    }

}
