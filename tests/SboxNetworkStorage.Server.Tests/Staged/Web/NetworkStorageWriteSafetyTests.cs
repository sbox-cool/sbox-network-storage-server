using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageWriteSafetyTests
{
    private static NetworkStorageRouteClassification Classify(string method, string path) =>
        NetworkStorageRouteClassifier.Classify(method, path);

    [Fact]
    public void NonMirroredRouteDoesNotExecuteCandidate()
    {
        var service = new NetworkStorageWriteSafetyService(new NetworkStorageShadowOptions());
        var decision = service.Evaluate(
            Classify("GET", "/totally/unknown"),
            new NetworkStorageDispatchDecision(NetworkStorageRuntimeMode.ProxyOnly, NetworkStorageDispatchTarget.Bun, false, false, "proxy-only"));

        Assert.False(decision.ExecuteCandidate);
        Assert.False(decision.SideEffectsSuppressed);
    }

    [Fact]
    public void ReadRouteExecutesWithoutSuppression()
    {
        var service = new NetworkStorageWriteSafetyService(new NetworkStorageShadowOptions());
        var decision = service.Evaluate(
            Classify("GET", "/v3/values/demo-project"),
            new NetworkStorageDispatchDecision(NetworkStorageRuntimeMode.ProxyOnly, NetworkStorageDispatchTarget.Bun, false, false, "proxy-only"));

        Assert.True(decision.ExecuteCandidate);
        Assert.False(decision.ProductionWritesAllowed);
        Assert.False(decision.SideEffectsSuppressed);
    }

    [Fact]
    public void MutatingRouteDefaultsToSuppressedDryRun()
    {
        var service = new NetworkStorageWriteSafetyService(new NetworkStorageShadowOptions());
        var decision = service.Evaluate(
            Classify("POST", "/v3/storage/demo-project/saves/player-1"),
            new NetworkStorageDispatchDecision(NetworkStorageRuntimeMode.ProxyOnly, NetworkStorageDispatchTarget.Bun, false, false, "proxy-only"));

        Assert.True(decision.ExecuteCandidate);
        Assert.False(decision.ProductionWritesAllowed);
        Assert.True(decision.SideEffectsSuppressed);
        Assert.False(decision.IsolatedReplayEnabled);
        Assert.Equal("Shadow/candidate mode computes intended operations only; production writers are suppressed.", decision.SuppressionReason);
    }

    [Fact]
    public void IsolatedReplayRewritesIntendedWritePaths()
    {
        var service = new NetworkStorageWriteSafetyService(new NetworkStorageShadowOptions
        {
            IsolatedReplayEnabled = true,
            IsolatedReplayPrefix = "isolated-ns"
        });
        var decision = service.Evaluate(
            Classify("POST", "/v3/storage/demo-project/saves/player-1"),
            new NetworkStorageDispatchDecision(NetworkStorageRuntimeMode.ProxyOnly, NetworkStorageDispatchTarget.Bun, false, false, "proxy-only"));

        Assert.True(decision.ExecuteCandidate);
        Assert.False(decision.ProductionWritesAllowed);
        Assert.False(decision.SideEffectsSuppressed);
        Assert.True(decision.IsolatedReplayEnabled);

        var rewritten = service.RewriteIntendedWritePaths(
            ["network-storage/users/42/demo-project/saves/data/player-1/saved.json"],
            decision);
        Assert.Single(rewritten);
        Assert.Equal("isolated-ns:network-storage/users/42/demo-project/saves/data/player-1/saved.json", rewritten[0]);
    }

    [Fact]
    public void ProductionWriteModeDisablesSuppression()
    {
        var service = new NetworkStorageWriteSafetyService(new NetworkStorageShadowOptions());
        var decision = service.Evaluate(
            Classify("POST", "/v3/storage/demo-project/saves/player-1"),
            new NetworkStorageDispatchDecision(NetworkStorageRuntimeMode.Serve, NetworkStorageDispatchTarget.Candidate, false, true, "served"));

        Assert.True(decision.ExecuteCandidate);
        Assert.True(decision.ProductionWritesAllowed);
        Assert.False(decision.SideEffectsSuppressed);
    }
}
