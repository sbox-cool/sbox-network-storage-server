using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// v0.4.0 stored PUT rate-limit-rules bodies in the documented {rules:[...]} wrapper,
/// but readers bind a bare array. Rows already stored that way must still load after upgrade.
/// </summary>
public sealed class RateLimitRulesWrapperReadTests
{
    private const string Rule = """{"id":"r1","collection":"players","field":"gold","windowSeconds":60,"max":10}""";

    [Theory]
    [InlineData("""{"rules":[__RULE__]}""")]
    [InlineData("""[__RULE__]""")]
    public async Task Wrapped_and_bare_rules_both_bind_to_a_list(string stored)
    {
        var store = new InMemoryNetworkStorageStore();
        using var doc = JsonDocument.Parse(stored.Replace("__RULE__", Rule));
        await store.UpsertRateLimitRulesAsync("p1", doc.RootElement.Clone(), 1, default);
        var client = new StoreMetadataWorkspaceClient(
            DispatchProxy.Create<IWorkspaceStore, NoopWorkspace>(), store, NullLogger<StoreMetadataWorkspaceClient>.Instance);

        var rules = await client.GetProjectResourceAsync<List<JsonElement>>(1, "p1", "rate-limit-rules.json", default);

        var rule = Assert.Single(rules!);
        Assert.Equal("gold", rule.GetProperty("field").GetString());
    }

    public class NoopWorkspace : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
