using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Service-level coverage for the Network Storage rate-limit read/save fixes:
/// endpoint limits live on the project (not a phantom rate-limits.json), field
/// rules round-trip through the authoritative rate-limit-rules.json (windows
/// schema), and saving endpoint limits never wipes the user's project list.
/// </summary>
public sealed class NetworkStorageRateLimitTests
{
    private static NetworkStorageProjectService CreateService(FakeBunny bunny)
        => new(
            bunny,
            bunnyStorageEnumerator: null!,
            new ConfigurationBuilder().Build(),
            keyCdnWriter: null!,
            scyllaStore: new InMemoryNetworkStorageStore(),
            NullLogger<NetworkStorageProjectService>.Instance);

    [Fact]
    public async Task GetProjectRateLimits_MapsWindowsSchemaRules()
    {
        var bunny = new FakeBunny();
        bunny.Resources["rate-limit-rules.json"] = """
        [
          { "id": "r1", "collection": "players", "field": "coins", "scope": "per_player",
            "windows": { "perMinute": 5, "perDay": 100 }, "action": "clamp", "enabled": true }
        ]
        """;
        var service = CreateService(bunny);

        var result = await service.GetProjectRateLimitsAsync(42, "proj_test", CancellationToken.None);

        // Endpoint limits come from the project field (controller-supplied), not this method.
        Assert.Null(result.EndpointRateLimits);

        var rule = Assert.Single(result.Rules!);
        Assert.Equal("players", rule.Collection);
        Assert.Equal("coins", rule.Field);
        Assert.Equal("clamp", rule.Action);
        Assert.Equal(5, rule.MaxPerMinute);
        Assert.Null(rule.MaxPerHour);
        Assert.Equal(100, rule.MaxPerDay);
        Assert.True(rule.Enabled);
        Assert.Equal("per_player", rule.Scope);
    }

    [Fact]
    public async Task GetProjectRateLimits_NoRulesFile_ReturnsEmpty()
    {
        var service = CreateService(new FakeBunny());

        var result = await service.GetProjectRateLimitsAsync(42, "proj_test", CancellationToken.None);

        Assert.Empty(result.Rules!);
    }

    [Fact]
    public async Task SaveRateLimitRules_WritesAuthoritativeResourceAndRoundTrips()
    {
        var bunny = new FakeBunny();
        var service = CreateService(bunny);
        var rules = new List<RateLimitRule>
        {
            new("r1", "players", "coins", "reject", MaxPerMinute: 10, MaxPerHour: null, MaxPerDay: 200, Enabled: true, Scope: "global"),
        };

        await service.SaveRateLimitRulesAsync(42, "proj_test", rules, CancellationToken.None);

        // It must persist to the authoritative resource (not the project's endpointRateLimits).
        var put = Assert.Single(bunny.Puts);
        Assert.Equal("rate-limit-rules.json", put.Path);

        using var doc = JsonDocument.Parse(put.Json);
        var written = doc.RootElement[0];
        Assert.Equal("global", written.GetProperty("scope").GetString());
        Assert.Equal("reject", written.GetProperty("action").GetString());
        Assert.Equal(10, written.GetProperty("windows").GetProperty("perMinute").GetInt32());
        Assert.Equal(200, written.GetProperty("windows").GetProperty("perDay").GetInt32());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("windows").GetProperty("perHour").ValueKind);

        // Round-trip: a subsequent read maps the windows schema back to the flat model.
        var reloaded = await service.GetProjectRateLimitsAsync(42, "proj_test", CancellationToken.None);
        var rule = Assert.Single(reloaded.Rules!);
        Assert.Equal(10, rule.MaxPerMinute);
        Assert.Equal(200, rule.MaxPerDay);
        Assert.Null(rule.MaxPerHour);
        Assert.Equal("global", rule.Scope);
    }

    [Fact]
    public async Task SaveEndpointRateLimits_DoesNotWipeProjectsAndPersistsOnProject()
    {
        var now = DateTimeOffset.UtcNow;
        var bunny = new FakeBunny();
        bunny.Projects.Add(new WorkspaceProject("proj_one", "One", null, true, now, now, null));
        bunny.Projects.Add(new WorkspaceProject("proj_two", "Two", null, true, now, now, null));
        var service = CreateService(bunny);

        var limits = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["perPlayer"] = new Dictionary<string, object> { ["perMinute"] = 60 },
        };

        await service.SaveEndpointRateLimitsAsync(42, "proj_one", limits, CancellationToken.None);

        // The full list is preserved (never wiped) and the target project carries the limits.
        Assert.Equal(2, bunny.Projects.Count);
        Assert.DoesNotContain(bunny.ProjectSaves, saved => saved.Count == 0);
        var target = bunny.Projects.Single(p => p.Id == "proj_one");
        Assert.NotNull(target.EndpointRateLimits);
        Assert.True(target.EndpointRateLimits!.ContainsKey("enabled"));
    }

    [Fact]
    public async Task SaveEndpointRateLimits_UnknownProject_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var bunny = new FakeBunny();
        bunny.Projects.Add(new WorkspaceProject("proj_one", "One", null, true, now, now, null));
        var service = CreateService(bunny);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveEndpointRateLimitsAsync(42, "missing", new Dictionary<string, object>(), CancellationToken.None));

        // A failed lookup must not have rewritten the project list.
        Assert.Empty(bunny.ProjectSaves);
        Assert.Single(bunny.Projects);
    }

    private sealed class FakeBunny : IWorkspaceStore
    {
        public Dictionary<string, string> Resources { get; } = new();
        public List<WorkspaceProject> Projects { get; } = new();
        public List<(string Path, string Json)> Puts { get; } = new();
        public List<IReadOnlyList<WorkspaceProject>> ProjectSaves { get; } = new();

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>(Projects.ToList());

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
        {
            ProjectSaves.Add(projects);
            Projects.Clear();
            Projects.AddRange(projects);
            return Task.CompletedTask;
        }

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(Resources.TryGetValue(resourcePath, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(Resources.TryGetValue(resourcePath, out var json) ? json : null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(data);
            Puts.Add((resourcePath, json));
            Resources[resourcePath] = json;
            return Task.CompletedTask;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
