using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageRouteCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record BunRoute(string Method, string Template, List<string> Sources);

    private static List<BunRoute> LoadInventory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-fixtures", "route-inventory.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<BunRoute>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Expected the route inventory fixture to deserialize.");
    }

    [Fact]
    public void EveryBunRouteIsCatalogedOrExplicitlyExcluded()
    {
        var catalog = NetworkStorageRouteCatalog.Entries
            .Select(entry => (entry.Method, entry.Template))
            .ToHashSet();

        var missing = LoadInventory()
            .Where(route => !catalog.Contains((route.Method, route.Template)))
            .Select(route => $"{route.Method} {route.Template}")
            .ToList();

        Assert.True(missing.Count == 0, $"Legacy routes missing from the .NET catalog: {string.Join(", ", missing)}");
    }

    [Fact]
    public void CatalogHasNoRoutesAbsentFromBun()
    {
        var inventory = LoadInventory()
            .Select(route => (route.Method, route.Template))
            .ToHashSet();

        var phantom = NetworkStorageRouteCatalog.Entries
            .Where(entry => !inventory.Contains((entry.Method, entry.Template)))
            .Select(entry => $"{entry.Method} {entry.Template}")
            .ToList();

        Assert.True(phantom.Count == 0, $"Catalog entries not present in the route inventory: {string.Join(", ", phantom)}");
    }

    [Fact]
    public void CatalogEntriesAreUniqueAndInternallyConsistent()
    {
        var seen = new HashSet<(string, string)>();
        foreach (var entry in NetworkStorageRouteCatalog.Entries)
        {
            Assert.True(seen.Add((entry.Method, entry.Template)), $"Duplicate catalog entry {entry.Method} {entry.Template}");

            if (entry.Disposition == NetworkStorageRouteDisposition.Excluded)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.ExclusionReason), $"{entry.Method} {entry.Template} is excluded without a reason.");
            }
            else
            {
                Assert.Null(entry.ExclusionReason);
            }

            var expectedMutating = entry.Method is not ("GET" or "HEAD" or "OPTIONS");
            Assert.Equal(expectedMutating, entry.IsMutating);
        }
    }

    [Fact]
    public void SpacetimePrototypeAndInternalFamiliesAreExcludedEverythingElseMirrored()
    {
        foreach (var entry in NetworkStorageRouteCatalog.Entries)
        {
            var expected = entry.Family is NetworkStorageRouteFamily.SpacetimePrototype or NetworkStorageRouteFamily.Internal
                ? NetworkStorageRouteDisposition.Excluded
                : NetworkStorageRouteDisposition.Mirror;
            Assert.Equal(expected, entry.Disposition);
        }
    }

    [Theory]
    [InlineData("GET", "/v3/storage/p/coll/list", "/v3/storage/:projectId/:collectionId/list")]
    [InlineData("GET", "/v3/storage/p/coll/some-key", "/v3/storage/:projectId/:collectionId/:key")]
    [InlineData("GET", "/v3/storage/p/coll/record/r1", "/v3/storage/:projectId/:collectionId/record/:recordId")]
    [InlineData("GET", "/api/storage/p/queries/q1", "/api/storage/:projectId/queries/:queryId")]
    [InlineData("GET", "/api/storage/p/coll/some-key", "/api/storage/:projectId/:collectionId/:key")]
    [InlineData("POST", "/v3/endpoints/p/slug", "/v3/endpoints/:projectId/:endpointSlug")]
    [InlineData("GET", "/v3/values/42", "/v3/values/:projectId")]
    public void TryMatchSelectsMostSpecificTemplate(string method, string path, string expectedTemplate)
    {
        Assert.True(NetworkStorageRouteCatalog.TryMatch(method, path, out var entry));
        Assert.Equal(expectedTemplate, entry!.Template);
    }

    [Fact]
    public void TryMatchReturnsFalseForUnknownPath()
    {
        Assert.False(NetworkStorageRouteCatalog.TryMatch("GET", "/totally/unknown/path", out var entry));
        Assert.Null(entry);
    }

    [Theory]
    [InlineData("/api/v3/storage/p/c/k", "/v3/storage/p/c/k", NetworkStorageAlias.ApiV3, true)]
    [InlineData("/api/v3", "/v3", NetworkStorageAlias.ApiV3, true)]
    [InlineData("/v3/values/1", "/v3/values/1", NetworkStorageAlias.V3, false)]
    [InlineData("/v1/storage/p/c/k", "/v1/storage/p/c/k", NetworkStorageAlias.V1, true)]
    [InlineData("/api/storage/p/c/k", "/api/storage/p/c/k", NetworkStorageAlias.ApiStorage, true)]
    [InlineData("/pages/p/slug", "/pages/p/slug", NetworkStorageAlias.Pages, true)]
    [InlineData("/api/pages/p/slug", "/api/pages/p/slug", NetworkStorageAlias.ApiPages, true)]
    [InlineData("/v3x/foo", "/v3x/foo", NetworkStorageAlias.Unknown, false)]
    [InlineData("/something-else", "/something-else", NetworkStorageAlias.Unknown, false)]
    public void NormalizesAliasAndCanonicalPath(string input, string expectedCanonical, NetworkStorageAlias expectedAlias, bool isCompatibilityAlias)
    {
        var normalized = NetworkStorageAliasNormalizer.Normalize(input);
        Assert.Equal(expectedAlias, normalized.Alias);
        Assert.Equal(expectedCanonical, normalized.CanonicalPath);
        Assert.Equal(isCompatibilityAlias, normalized.IsCompatibilityAlias);
    }

    [Fact]
    public void NormalizeStripsQueryStringFromCanonicalPath()
    {
        var normalized = NetworkStorageAliasNormalizer.Normalize("/v3/values/1?apiKey=abc&steamId=123");
        Assert.Equal(NetworkStorageAlias.V3, normalized.Alias);
        Assert.Equal("/v3/values/1", normalized.CanonicalPath);
    }

    [Fact]
    public void ApiV3IsExplicitCompatibilityAliasForCanonicalV3()
    {
        var apiV3 = NetworkStorageRouteClassifier.Classify("GET", "/api/v3/values/42");
        var canonical = NetworkStorageRouteClassifier.Classify("GET", "/v3/values/42");

        Assert.Equal(NetworkStorageAlias.ApiV3, apiV3.Alias);
        Assert.True(apiV3.IsCompatibilityAlias);
        Assert.True(apiV3.IsKnownRoute);
        Assert.Equal("/v3/values/42", apiV3.CanonicalPath);

        Assert.Equal(NetworkStorageAlias.V3, canonical.Alias);
        Assert.False(canonical.IsCompatibilityAlias);

        // The alias and the canonical route resolve to the very same catalog entry.
        Assert.Same(canonical.Entry, apiV3.Entry);
    }

    [Fact]
    public void InternalRoutesAreNotPublicAliases()
    {
        var classification = NetworkStorageRouteClassifier.Classify("GET", "/_internal/health");
        Assert.Equal(NetworkStorageAlias.Internal, classification.Alias);
        Assert.False(classification.IsPublicAlias);
    }
}
