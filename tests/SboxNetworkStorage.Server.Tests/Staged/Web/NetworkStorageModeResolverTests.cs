using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageModeResolverTests
{
    private static NetworkStorageRouteClassification Classify(string method, string path) =>
        NetworkStorageRouteClassifier.Classify(method, path);

    [Theory]
    [InlineData("GET", "/v3/values/1")]
    [InlineData("GET", "/v3/storage/p/c/k")]
    [InlineData("POST", "/v3/storage/p/c/k")]
    [InlineData("PUT", "/v3/manage/p/settings")]
    [InlineData("POST", "/v3/endpoints/p")]
    [InlineData("GET", "/api/v3/values/1")]
    public void DefaultModeIsBunProxyOnlyWithNoCandidateAndNoWrites(string method, string path)
    {
        var resolver = new NetworkStorageModeResolver(new NetworkStorageShadowOptions());
        var decision = resolver.Resolve(Classify(method, path));

        Assert.Equal(NetworkStorageRuntimeMode.ProxyOnly, decision.EffectiveMode);
        Assert.Equal(NetworkStorageDispatchTarget.Bun, decision.Target);
        Assert.False(decision.ExecuteShadowCandidate);
        Assert.False(decision.ProductionWritesAllowed);
    }

    [Fact]
    public void ShadowModeRunsCandidateDiagnosticsButNeverWritesProduction()
    {
        var resolver = new NetworkStorageModeResolver(new NetworkStorageShadowOptions { Mode = NetworkStorageRuntimeMode.Shadow });

        var read = resolver.Resolve(Classify("GET", "/v3/storage/p/c/k"));
        Assert.Equal(NetworkStorageDispatchTarget.Bun, read.Target);
        Assert.True(read.ExecuteShadowCandidate);
        Assert.False(read.ProductionWritesAllowed);

        var write = resolver.Resolve(Classify("POST", "/v3/storage/p/c/k"));
        Assert.Equal(NetworkStorageDispatchTarget.Bun, write.Target);
        Assert.True(write.ExecuteShadowCandidate);
        Assert.False(write.ProductionWritesAllowed);
    }

    [Fact]
    public void ExcludedRoutesAlwaysProxyEvenWhenServeIsConfigured()
    {
        var resolver = new NetworkStorageModeResolver(new NetworkStorageShadowOptions { Mode = NetworkStorageRuntimeMode.Serve });
        var decision = resolver.Resolve(Classify("GET", "/v3/prototype/spacetimedb/p/status"));

        Assert.Equal(NetworkStorageDispatchTarget.Bun, decision.Target);
        Assert.False(decision.ProductionWritesAllowed);
        Assert.Equal("route-not-mirrored", decision.Reason);
    }

    [Fact]
    public void UnknownRoutesAlwaysProxyEvenWhenServeIsConfigured()
    {
        var resolver = new NetworkStorageModeResolver(new NetworkStorageShadowOptions { Mode = NetworkStorageRuntimeMode.Serve });
        var decision = resolver.Resolve(Classify("GET", "/totally/unknown"));

        Assert.Equal(NetworkStorageDispatchTarget.Bun, decision.Target);
        Assert.False(decision.ProductionWritesAllowed);
    }

    [Fact]
    public void CanaryReadServesAllowlistedReadsAndNeverServesMutations()
    {
        var options = new NetworkStorageShadowOptions
        {
            Mode = NetworkStorageRuntimeMode.CanaryRead,
            CanaryProjectIds = { "demo-project" }
        };
        var resolver = new NetworkStorageModeResolver(options);

        var allowedRead = resolver.Resolve(Classify("GET", "/v3/storage/demo-project/c/k"), projectId: "demo-project");
        Assert.Equal(NetworkStorageDispatchTarget.Candidate, allowedRead.Target);
        Assert.False(allowedRead.ProductionWritesAllowed);

        var unlistedRead = resolver.Resolve(Classify("GET", "/v3/storage/other/c/k"), projectId: "other");
        Assert.Equal(NetworkStorageDispatchTarget.Bun, unlistedRead.Target);

        var allowlistedMutation = resolver.Resolve(Classify("POST", "/v3/storage/demo-project/c/k"), projectId: "demo-project");
        Assert.Equal(NetworkStorageDispatchTarget.Bun, allowlistedMutation.Target);
        Assert.False(allowlistedMutation.ProductionWritesAllowed);
    }

    [Fact]
    public void CanaryWriteAllowsProductionWriteOnlyWhenAllowlisted()
    {
        var options = new NetworkStorageShadowOptions
        {
            Mode = NetworkStorageRuntimeMode.CanaryWrite,
            CanarySteamIds = { "76561198000000000" }
        };
        var resolver = new NetworkStorageModeResolver(options);

        var allowed = resolver.Resolve(Classify("POST", "/v3/storage/p/c/k"), steamId: "76561198000000000");
        Assert.Equal(NetworkStorageDispatchTarget.Candidate, allowed.Target);
        Assert.True(allowed.ProductionWritesAllowed);

        var unlisted = resolver.Resolve(Classify("POST", "/v3/storage/p/c/k"), steamId: "999");
        Assert.Equal(NetworkStorageDispatchTarget.Bun, unlisted.Target);
        Assert.False(unlisted.ProductionWritesAllowed);
    }

    [Fact]
    public void ServeModeOwnsResponsesAndPermitsMutationWrites()
    {
        var resolver = new NetworkStorageModeResolver(new NetworkStorageShadowOptions { Mode = NetworkStorageRuntimeMode.Serve });

        var read = resolver.Resolve(Classify("GET", "/v3/values/1"));
        Assert.Equal(NetworkStorageDispatchTarget.Candidate, read.Target);
        Assert.False(read.ProductionWritesAllowed);

        var write = resolver.Resolve(Classify("POST", "/v3/storage/p/c/k"));
        Assert.Equal(NetworkStorageDispatchTarget.Candidate, write.Target);
        Assert.True(write.ProductionWritesAllowed);
    }

    [Fact]
    public void RouteFamilyModeOverridesGlobalMode()
    {
        var options = new NetworkStorageShadowOptions
        {
            Mode = NetworkStorageRuntimeMode.ProxyOnly,
            RouteFamilyModes = { ["Values"] = NetworkStorageRuntimeMode.Shadow }
        };
        var resolver = new NetworkStorageModeResolver(options);

        var values = resolver.Resolve(Classify("GET", "/v3/values/1"));
        Assert.Equal(NetworkStorageRuntimeMode.Shadow, values.EffectiveMode);
        Assert.True(values.ExecuteShadowCandidate);

        var storage = resolver.Resolve(Classify("GET", "/v3/storage/p/c/k"));
        Assert.Equal(NetworkStorageRuntimeMode.ProxyOnly, storage.EffectiveMode);
        Assert.False(storage.ExecuteShadowCandidate);
    }
}
