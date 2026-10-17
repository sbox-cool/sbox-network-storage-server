using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ApiRootTests
{
    [Theory]
    [InlineData("/api/values/p1", "/v3/values/p1")]
    [InlineData("/api/manage/p1/validate", "/v3/manage/p1/validate")]
    [InlineData("/api/v3/values/p1", "/v3/values/p1")]
    [InlineData("/api/v3/storage/p/c/list", "/v3/storage/p/c/list")]
    [InlineData("/API/V3/storage/p/stats/s", "/v3/storage/p/stats/s")]
    [InlineData("/api/storage-browse/p", "/v3/storage-browse/p")]
    public void NewRootsResolveToCanonicalPaths(string input, string expected)
    {
        Assert.True(NetworkStorageRoots.TryRewriteApiRoot(input, out var rewritten));
        Assert.Equal(expected, rewritten);
    }

    [Theory]
    [InlineData("/api/storage/p/c/list")]
    [InlineData("/api/storage/p/stats/player")]
    [InlineData("/api/storage")]
    [InlineData("/api/pages/p/slug")]
    [InlineData("/api/network-storage/p/save-failure")]
    [InlineData("/api")]
    [InlineData("/v3/values/p")]
    [InlineData("/v1/values/p")]
    [InlineData("/dashboard")]
    [InlineData("/health")]
    [InlineData(null)]
    public void ExistingRoutesAreNeverRepurposed(string? input)
    {
        Assert.False(NetworkStorageRoots.TryRewriteApiRoot(input, out _));
    }

    [Theory]
    [InlineData("/api/storage/p/c/k", NetworkStorageAlias.ApiStorage)]
    [InlineData("/api/pages/p/slug", NetworkStorageAlias.ApiPages)]
    [InlineData("/api/network-storage/p/save-failure", NetworkStorageAlias.Unknown)]
    [InlineData("/api/v3/storage/p/c/list", NetworkStorageAlias.ApiV3)]
    public void ClassificationPreservesExistingAliasMeaning(string input, NetworkStorageAlias expected)
        => Assert.Equal(expected, NetworkStorageAliasNormalizer.Normalize(input).Alias);

    [Fact]
    public async Task ApiOnlyFeaturesRemainNativeAndDoNotAcquireLegacyRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseMiddleware<ApiRootRewriteMiddleware>();
        app.MapGet("/api/new-feature", () => Results.NoContent());
        app.MapApiRootAliases();
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/new-feature")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v3/new-feature")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v3/new-feature")).StatusCode);
    }
}
