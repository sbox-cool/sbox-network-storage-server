using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The documented <c>/api</c> root mirrors <c>/v3</c>: reserved <c>/api</c> families keep their own
/// routes, everything else normalizes to the canonical <c>/v3</c> path for classification so
/// metering and analytics never split by root.
/// </summary>
public sealed class ApiRootTests
{
    [Theory]
    [InlineData("/api/values/p1", "/v3/values/p1")]
    [InlineData("/api/manage/p1/validate", "/v3/manage/p1/validate")]
    [InlineData("/api/auth-sessions/p1/create", "/v3/auth-sessions/p1/create")]
    [InlineData("/api/sessions/p1/refresh", "/v3/sessions/p1/refresh")]
    [InlineData("/api/endpoints/p1/slug", "/v3/endpoints/p1/slug")]
    [InlineData("/api/queries/p1/q1", "/v3/queries/p1/q1")]
    [InlineData("/api/server-info", "/v3/server-info")]
    [InlineData("/api/nothing-here", "/v3/nothing-here")]
    public void MirrorPathsRewriteToCanonical(string input, string expected)
    {
        Assert.True(NetworkStorageRoots.TryRewriteApiRoot(input, out var rewritten));
        Assert.Equal(expected, rewritten);
    }

    [Theory]
    [InlineData("/api/storage/p/c/k")]
    [InlineData("/api/storage")]
    [InlineData("/api/pages/p/slug")]
    [InlineData("/api/network-storage/p/save-failure")]
    [InlineData("/api/v3/values/p")]
    [InlineData("/api/v3")]
    [InlineData("/api")]
    [InlineData("/v3/values/p")]
    [InlineData("/v1/values/p")]
    [InlineData("/dashboard")]
    [InlineData("/health")]
    [InlineData("")]
    [InlineData(null)]
    public void ReservedAndOtherPathsKeepTodaysBehavior(string? input)
    {
        Assert.False(NetworkStorageRoots.TryRewriteApiRoot(input, out _));
    }

    [Theory]
    [InlineData("/api/values/42", "/v3/values/42")]
    [InlineData("/api/manage/p/validate", "/v3/manage/p/validate")]
    [InlineData("/api/endpoints/p/slug", "/v3/endpoints/p/slug")]
    public void ApiNormalizesToCanonicalV3AsACompatibilityAlias(string input, string expectedCanonical)
    {
        var normalized = NetworkStorageAliasNormalizer.Normalize(input);
        Assert.Equal(NetworkStorageAlias.Api, normalized.Alias);
        Assert.Equal(expectedCanonical, normalized.CanonicalPath);
        Assert.True(normalized.IsCompatibilityAlias);
        Assert.True(normalized.IsPublicAlias);
        Assert.Equal("api", normalized.Alias.ToToken());
    }

    [Theory]
    [InlineData("/api/storage/p/c/k", NetworkStorageAlias.ApiStorage)]
    [InlineData("/api/pages/p/slug", NetworkStorageAlias.ApiPages)]
    [InlineData("/api/v3/values/p", NetworkStorageAlias.ApiV3)]
    [InlineData("/api", NetworkStorageAlias.Unknown)]
    public void ReservedApiFamiliesKeepTheirAlias(string input, NetworkStorageAlias expected)
    {
        Assert.Equal(expected, NetworkStorageAliasNormalizer.Normalize(input).Alias);
    }

    [Fact]
    public void EveryMirroredV3CatalogRouteHasAnApiTwinResolvingToTheSameHandlerShape()
    {
        var mirrored = NetworkStorageRouteCatalog.Entries
            .Where(e => e.Disposition == NetworkStorageRouteDisposition.Mirror && e.Template.StartsWith("/v3/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(mirrored);
        foreach (var entry in mirrored)
        {
            var segments = entry.Template.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.StartsWith(':') ? "x" : s)
                .ToList();
            var apiPath = "/api/" + string.Join('/', segments.Skip(1));
            var v3Path = "/v3/" + string.Join('/', segments.Skip(1));
            var twin = NetworkStorageRouteClassifier.Classify(entry.Method, apiPath);
            Assert.True(twin.IsKnownRoute);
            if (twin.Alias == NetworkStorageAlias.Api)
            {
                // Generic mirror: the very same catalog entry, so the same handler shape.
                Assert.Same(entry, twin.Entry);
                Assert.Equal(v3Path, twin.CanonicalPath);
            }
            else
            {
                // Storage-family twins keep their explicit /api/storage alias entries (same handlers).
                Assert.Equal(NetworkStorageAlias.ApiStorage, twin.Alias);
                Assert.Equal(apiPath, twin.CanonicalPath);
            }
            Assert.Equal(entry.Method, twin.Entry!.Method);
            Assert.Equal(entry.Family, twin.Entry.Family);
            Assert.Equal(entry.Plane, twin.Entry.Plane);
        }
    }

    [Theory]
    [InlineData("/v3/manage/p/sync", true)]
    [InlineData("/api/manage/p/sync", true)]
    [InlineData("/v3/values/p", false)]
    [InlineData("/api/values/p", false)]
    public void ManagementPathsMatchOnBothRoots(string path, bool expected)
    {
        Assert.Equal(expected, NetworkStorageRoots.IsManagementPath(path));
    }

    [Fact]
    public void RequestRootReportsTheCallerRootAndMapsGeneratedUrlsToIt()
    {
        var v3 = new DefaultHttpContext();
        v3.Request.Path = "/v3/values/p";
        Assert.Equal("/v3/values/p", NetworkStorageRequestRoot.OriginalPath(v3));
        Assert.Equal("/v3", NetworkStorageRequestRoot.Root(v3));
        Assert.Equal("/v3/values/p", NetworkStorageRequestRoot.ForRequest(v3, "/v3/values/p"));

        var api = new DefaultHttpContext();
        api.Request.Path = "/v3/values/p";
        api.Items[NetworkStorageRequestRoot.OriginalPathItemKey] = "/api/values/p";
        Assert.Equal("/api/values/p", NetworkStorageRequestRoot.OriginalPath(api));
        Assert.Equal("/api", NetworkStorageRequestRoot.Root(api));
        Assert.Equal("/api/values/p", NetworkStorageRequestRoot.ForRequest(api, "/v3/values/p"));
        Assert.Equal("/pages/p/s", NetworkStorageRequestRoot.ForRequest(api, "/pages/p/s"));
    }

    // The mirror resolves to canonical /v3 templates before routing, so limits and
    // rate-limit partitions follow from this template mapping on both roots.
    [Theory]
    [InlineData("/v3/values/{projectId}", RouteGroup.Game)]
    [InlineData("/v3/endpoints/{projectId}/{endpointSlug}", RouteGroup.Game)]
    [InlineData("/v3/manage/{projectId}/revision-init", RouteGroup.Game)]
    [InlineData("/v3/manage/{projectId}/endpoints", RouteGroup.Management)]
    [InlineData("/v3/auth-sessions/{projectId}/create", RouteGroup.AuthSession)]
    [InlineData("/v3/storage/{projectId}/rate-limits", RouteGroup.Game)]
    public void CanonicalTemplatesMapToTheirLimitGroups(string template, RouteGroup expected)
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            new EndpointMetadataCollection(),
            template);
        Assert.Equal(expected, RouteGroups.Of(endpoint));
    }
}
