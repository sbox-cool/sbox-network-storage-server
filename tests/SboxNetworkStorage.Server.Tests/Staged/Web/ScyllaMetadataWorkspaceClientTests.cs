using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Pins the metadata cutover: when Scylla:Primary is true, per-project metadata
// (projects, pages, collections, workflows, queries, endpoints, game-values,
// rate-limit rules) is authoritative in ScyllaDB and read ScyllaDB-first; Bunny
// is a best-effort mirror + legacy read fallback. When false, everything passes
// through to Bunny.
public sealed class ScyllaMetadataWorkspaceClientTests
{
    private const string Project = "proj_meta";
    private const long Owner = 77;

    private static ScyllaMetadataWorkspaceClient Create(InMemoryNetworkStorageStore store, FakeBunny inner, bool primary)
        => new(inner, store, Options.Create(new ScyllaDbOptions { Primary = primary }),
            NullLogger<ScyllaMetadataWorkspaceClient>.Instance);

    // ── Collections round-trip ──

    [Fact]
    public async Task Primary_PutCollections_WritesScyllaAndMirrorsBunny_AndRoundTrips()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "c1", ["name"] = "Players", ["visibility"] = "private", ["collectionType"] = "global" },
            new() { ["id"] = "c2", ["name"] = "Scores", ["visibility"] = "private" },
        };

        await client.PutProjectResourceAsync(Owner, Project, "collections.json", collections, CancellationToken.None);

        Assert.Equal(2, store.Collections.Count);
        Assert.True(inner.Has(Project, "collections.json"));

        var read = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "collections.json", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Count);
        var c1 = read.Single(e => e.GetProperty("id").GetString() == "c1");
        Assert.Equal("Players", c1.GetProperty("name").GetString());
        Assert.Equal("global", c1.GetProperty("collectionType").GetString());
    }

    [Fact]
    public async Task Primary_PutSmallerList_ReconcilesDeletes()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        await client.PutProjectResourceAsync(Owner, Project, "collections.json", new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "c1", ["name"] = "A" },
            new() { ["id"] = "c2", ["name"] = "B" },
        }, CancellationToken.None);
        Assert.Equal(2, store.Collections.Count);

        await client.PutProjectResourceAsync(Owner, Project, "collections.json", new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "c1", ["name"] = "A" },
        }, CancellationToken.None);

        Assert.Single(store.Collections);
        var remaining = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "collections.json", CancellationToken.None);
        Assert.NotNull(remaining);
        Assert.Equal("c1", Assert.Single(remaining!).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Primary_GetWhenScyllaEmpty_ReturnsEmptyList_DoesNotFallBackToBunny()
    {
        // ScyllaDB is authoritative for metadata. An empty list in ScyllaDB is a
        // valid answer ("this project has zero collections") and MUST NOT fall
        // back to Bunny. Falling back made a missing/500ing Bunny workflows.json
        // surface as a 500 to the game client — the production outage this pins.
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        await inner.PutProjectResourceAsync(Owner, Project, "collections.json", new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "legacy", ["name"] = "Legacy" },
        }, CancellationToken.None);
        var client = Create(store, inner, primary: true);

        var read = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "collections.json", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Empty(read!);
        Assert.Empty(store.Collections);
    }

    [Fact]
    public async Task Primary_GetWorkflowsEmpty_ReturnsEmptyArray_DoesNotFallBackToBunny()
    {
        // Reproduces the production incident: workflows.json read with zero
        // workflows in ScyllaDB previously returned null → fell back to Bunny →
        // Bunny 500 → InvalidOperationException → 500 to the game client. ScyllaDB
        // is now authoritative; an empty workflows list returns an empty array.
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var read = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "workflows.json", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Empty(read!);
    }

    [Fact]
    public async Task Primary_GetWorkflowsText_ReadsFromScyllaDB()
    {
        // ProjectActivityLoader reads workflows via the text path; it must route
        // through ScyllaDB (not Bunny) so activity timestamps match the
        // authoritative store.
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        await client.PutProjectResourceAsync(Owner, Project, "workflows.json", new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "wf1", ["name"] = "Daily Reset" },
        }, CancellationToken.None);

        var text = await client.GetProjectResourceTextAsync(Owner, Project, "workflows.json", CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Daily Reset", text);
    }

    [Fact]
    public async Task Primary_GetWorkflowsTextEmpty_ReturnsEmptyArrayText_DoesNotFallBackToBunny()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var text = await client.GetProjectResourceTextAsync(Owner, Project, "workflows.json", CancellationToken.None);

        Assert.Equal("[]", text);
    }

    [Fact]
    public async Task NotPrimary_PassesThroughToBunny_WithoutTouchingScylla()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: false);

        await client.PutProjectResourceAsync(Owner, Project, "collections.json", new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "c1", ["name"] = "A" },
        }, CancellationToken.None);

        Assert.Empty(store.Collections);
        Assert.True(inner.Has(Project, "collections.json"));
    }

    // ── Game-values singleton ──

    [Fact]
    public async Task Primary_GameValues_SingletonRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var payload = new Dictionary<string, object?> { ["items"] = new[] { "a", "b" }, ["version"] = "v3" };
        await client.PutProjectResourceAsync(Owner, Project, "game-values.json", payload, CancellationToken.None);

        Assert.Single(store.GameValues);
        var read = await client.GetProjectResourceAsync<JsonElement>(Owner, Project, "game-values.json", CancellationToken.None);
        Assert.Equal("v3", read.GetProperty("version").GetString());
        Assert.Equal(2, read.GetProperty("items").GetArrayLength());
    }

    // ── Queries secret-key column ──

    [Fact]
    public async Task Primary_Queries_ProjectsSecretKeyColumn_AndRoundTrips()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var queries = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "q1", ["name"] = "Top", ["requiresSecretKey"] = true, ["type"] = "leaderboard" },
        };
        await client.PutProjectResourceAsync(Owner, Project, "queries.json", queries, CancellationToken.None);

        var row = Assert.Single(store.Queries).Value;
        Assert.True(row.GetProperty("requires_secret_key").GetBoolean());

        var read = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "queries.json", CancellationToken.None);
        Assert.True(Assert.Single(read!).GetProperty("requiresSecretKey").GetBoolean());
    }

    // ── Non-metadata passthrough ──

    [Fact]
    public async Task NonMetadataPath_PassesThrough_WithoutTouchingScylla()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        await client.PutProjectResourceAsync(Owner, Project, "keys.json", new[] { "k1" }, CancellationToken.None);

        Assert.Empty(store.Collections);
        Assert.Empty(store.Queries);
        Assert.True(inner.Has(Project, "keys.json"));
    }

    // ── Project list ──

    [Fact]
    public async Task Primary_GetUserProjects_ReadsFromScyllaDB()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var projectPayload = new Dictionary<string, object?> { ["name"] = "My Game", ["enabled"] = true };
        await store.UpsertProjectAsync("p1", JsonSerializer.SerializeToElement(projectPayload), 1, CancellationToken.None);
        await store.UpsertProjectMembershipAsync("77", "p1", "owner", 1700000000, CancellationToken.None);

        var projects = await client.GetUserProjectsAsync(77, CancellationToken.None);
        Assert.Single(projects);
        Assert.Equal("p1", projects[0].Id);
        Assert.Equal("My Game", projects[0].Name);
    }

    [Fact]
    public async Task Primary_GetUserProjects_FallsBackToBunnyWhenEmpty()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        inner.ProjectsByUser[77] = new List<BunnyProject> { new("legacy", "Legacy", null, true, null, null, null) };
        var client = Create(store, inner, primary: true);

        var projects = await client.GetUserProjectsAsync(77, CancellationToken.None);
        Assert.Single(projects);
        Assert.Equal("legacy", projects[0].Id);
    }

    [Fact]
    public async Task Primary_SaveUserProjects_WritesToScyllaAndMirrors()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var projects = new List<BunnyProject>
        {
            new("p1", "Game One", null, true, null, null, null),
            new("p2", "Game Two", null, true, null, null, null),
        };
        await client.SaveUserProjectsAsync(77, projects, CancellationToken.None);

        Assert.Equal(2, store.Projects.Count);
        Assert.Equal(2, store.ProjectMembers.Count);
        Assert.True(inner.ProjectsByUser.ContainsKey(77));
    }

    [Fact]
    public async Task Primary_SaveUserProjects_DeletesRemovedMemberships()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        await client.SaveUserProjectsAsync(77, new List<BunnyProject>
        {
            new("p1", "A", null, true, null, null, null),
            new("p2", "B", null, true, null, null, null),
        }, CancellationToken.None);
        Assert.Equal(2, store.ProjectMembers.Count);

        await client.SaveUserProjectsAsync(77, new List<BunnyProject>
        {
            new("p1", "A", null, true, null, null, null),
        }, CancellationToken.None);
        Assert.Single(store.ProjectMembers);
    }

    [Fact]
    public async Task Primary_GetUserProjects_UsesMembershipId_WhenPayloadOmitsId()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var projectPayload = new Dictionary<string, object?> { ["name"] = "No Id Game", ["enabled"] = true };
        await store.UpsertProjectAsync("p_noid", JsonSerializer.SerializeToElement(projectPayload), 1, CancellationToken.None);
        await store.UpsertProjectMembershipAsync("77", "p_noid", "owner", 1700000000, CancellationToken.None);

        var projects = await client.GetUserProjectsAsync(77, CancellationToken.None);
        Assert.Single(projects);
        Assert.Equal("p_noid", projects[0].Id);
        Assert.Equal("No Id Game", projects[0].Name);
    }

    [Fact]
    public async Task Primary_SaveUserProjects_CanAddProject_WhenExistingPayloadOmitsId()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var projectPayload = new Dictionary<string, object?> { ["name"] = "No Id Game", ["enabled"] = true };
        await store.UpsertProjectAsync("p_noid", JsonSerializer.SerializeToElement(projectPayload), 1, CancellationToken.None);
        await store.UpsertProjectMembershipAsync("77", "p_noid", "owner", 1700000000, CancellationToken.None);

        var existing = (await client.GetUserProjectsAsync(77, CancellationToken.None)).ToList();
        existing.Add(new BunnyProject("p_new", "New Game", null, true, null, null, null));
        await client.SaveUserProjectsAsync(77, existing, CancellationToken.None);

        Assert.Equal(2, store.Projects.Count);
        Assert.Equal(2, store.ProjectMembers.Count);
    }

    [Fact]
    public async Task NonPrimary_ProjectList_PassesThroughToBunny()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        inner.ProjectsByUser[77] = new List<BunnyProject> { new("p1", "Game", null, true, null, null, null) };
        var client = Create(store, inner, primary: false);

        var projects = await client.GetUserProjectsAsync(77, CancellationToken.None);
        Assert.Single(projects);
        Assert.Equal("p1", projects[0].Id);
        Assert.Empty(store.Projects);
        Assert.Empty(store.ProjectMembers);
    }

    // ── Pages ──

    [Fact]
    public async Task Primary_Pages_ReadWriteRoundTrip()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var pageContent = new Dictionary<string, object?> { ["title"] = "Welcome", ["html"] = "<h1>Hi</h1>" };
        await client.PutProjectResourceAsync(Owner, Project, "pages/welcome.json", pageContent, CancellationToken.None);

        var read = await client.GetProjectResourceAsync<JsonElement>(Owner, Project, "pages/welcome.json", CancellationToken.None);
        Assert.Equal("Welcome", read.GetProperty("title").GetString());
        Assert.Equal("<h1>Hi</h1>", read.GetProperty("html").GetString());
    }

    [Fact]
    public async Task Primary_PagesIndex_WritesToScylla()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var pages = new List<Dictionary<string, object?>>
        {
            new() { ["slug"] = "welcome", ["title"] = "Welcome" },
            new() { ["slug"] = "faq", ["title"] = "FAQ" },
        };
        await client.PutProjectResourceAsync(Owner, Project, "pages.json", pages, CancellationToken.None);

        Assert.Equal(2, store.Pages.Count);

        var read = await client.GetProjectResourceAsync<List<JsonElement>>(Owner, Project, "pages.json", CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Count);
    }

    [Fact]
    public async Task Primary_DeleteRawAsync_PageContent_DeletesFromScyllaAndBunny()
    {
        // When a page slug is renamed or deleted, DeleteRawAsync is called with
        // the full Bunny path. ScyllaDB must also delete the page content so the
        // authoritative store stays consistent.
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        // Seed a page in ScyllaDB
        await store.UpsertPageAsync(Project, "old-slug", "Old Title", "{}", 1, 1, CancellationToken.None);
        Assert.Single(store.Pages);

        // Delete via raw path (as NetworkStorageController does)
        var rawPath = $"network-storage/users/{Owner}/{Project}/pages/old-slug.json";
        await client.DeleteRawAsync(rawPath, CancellationToken.None);

        // ScyllaDB page should be gone
        Assert.Empty(store.Pages);
    }

    [Fact]
    public async Task Primary_DeleteRawAsync_NonPagePath_OnlyDeletesBunny()
    {
        // Non-page raw paths (e.g. backup blobs, key files) should only delete from Bunny.
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);

        var rawPath = "network-storage-api/keys/projects/proj_meta/sk_test.json";
        await client.DeleteRawAsync(rawPath, CancellationToken.None);

        // ScyllaDB should be untouched
        Assert.Empty(store.Pages);
    }

    [Fact]
    public async Task NotPrimary_DeleteRawAsync_PassesThroughToBunny()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: false);

        await store.UpsertPageAsync(Project, "slug", "Title", "{}", 1, 1, CancellationToken.None);
        var rawPath = $"network-storage/users/{Owner}/{Project}/pages/slug.json";
        await client.DeleteRawAsync(rawPath, CancellationToken.None);

        // ScyllaDB should be untouched when not primary
        Assert.Single(store.Pages);
    }

    [Fact]
    public async Task Primary_UsageReadsMonthlyTrafficAndCumulativeStorage()
    {
        var store = new InMemoryNetworkStorageStore();
        var inner = new FakeBunny();
        var client = Create(store, inner, primary: true);
        var currentMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var previousMonth = DateTimeOffset.UtcNow.AddMonths(-1).ToString("yyyy-MM");

        await store.IncrementProjectUsageAsync(
            Project,
            previousMonth,
            DateTimeOffset.UtcNow.AddMonths(-1).ToString("yyyy-MM-dd"),
            null,
            new UsageDelta(0, 0, 0, 0, 0, 0, 0, 0, 0, 1_024),
            CancellationToken.None);
        await store.IncrementProjectUsageAsync(
            Project,
            currentMonth,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
            null,
            new UsageDelta(42, 40, 2, 0, 128, 4_096, 1, 250, 42, 2_048),
            CancellationToken.None);

        var usage = await client.GetProjectUsageAsync(Owner, Project, currentMonth, CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(42, usage.Requests);
        Assert.Equal(4_096, usage.BytesOut);
        Assert.Equal(3_072, usage.StorageBytes);
    }

    // ── Fake bunny ──

    private sealed class FakeBunny : IBunnyWorkspaceClient
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
        public readonly Dictionary<long, List<BunnyProject>> ProjectsByUser = new();

        public bool Has(string projectId, string path) => _resources.ContainsKey($"{projectId}:{path}");

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
            => Task.FromResult(_resources.TryGetValue($"{projectId}:{resourcePath}", out var json)
                ? JsonSerializer.Deserialize<T>(json, Camel)
                : default);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken ct)
        {
            _resources[$"{projectId}:{resourcePath}"] = JsonSerializer.Serialize(data, Camel);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BunnyProject>>(
                ProjectsByUser.TryGetValue(userId, out var list) ? list : []);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken ct)
        {
            ProjectsByUser[userId] = projects.ToList();
            return Task.CompletedTask;
        }

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken ct)
            => GetProjectResourceAsync<WorkspaceProjectUsage>(userId, projectId, $"usage/{monthKey}.json", ct);
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken ct)
            => Task.FromResult(_resources.TryGetValue($"{projectId}:{resourcePath}", out var json) ? json : null);
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken ct) => throw new NotSupportedException();
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteRawAsync(string absolutePath, CancellationToken ct) => Task.CompletedTask;
    }
}
