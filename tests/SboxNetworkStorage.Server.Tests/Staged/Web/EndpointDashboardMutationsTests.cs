using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Regression coverage for the endpoint editor "Push to Live" save. Before the fix
/// the editor POST funnelled through UpdateProjectSettingsAsync, whose switch had no
/// endpoint case, so the edit was silently discarded (the controller still answered
/// ok:true and the page reloaded the old definition). These tests assert the new
/// EndpointDashboardMutations actually persist the edited definition to endpoints.json.
/// </summary>
public sealed class EndpointDashboardMutationsTests
{
    private const long OwnerId = 7;
    private const string ProjectId = "proj_test";

    [Fact]
    public async Task UpdateEndpoint_persists_newly_added_step()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", """
            [{"id":"ep1","slug":"save-all","name":"Save All","method":"POST","enabled":true,
              "input":{"type":"object","properties":{}},
              "steps":[{"id":"s1","type":"transform"}],
              "response":{"status":200,"body":{"ok":true}}}]
            """);

        // The editor sends the full compiled definition with the new step appended.
        var form = new Dictionary<string, string>
        {
            ["endpointId"] = "ep1",
            ["name"] = "Save All",
            ["enabled"] = "true",
            ["definition"] = """
                {"input":{"type":"object","properties":{}},
                 "steps":[{"id":"s1","type":"transform"},{"id":"s2","type":"write"}],
                 "response":{"status":200,"body":{"ok":true}}}
                """,
            ["publishTarget"] = "live",
        };

        await EndpointDashboardMutations.UpdateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None);

        var endpoint = SingleEndpoint(client);
        var steps = endpoint.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("s2", steps[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task UpdateEndpoint_preserves_fields_absent_from_payload()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", """
            [{"id":"ep1","slug":"lookup","name":"Lookup","method":"GET","enabled":true,
              "exposure":"internal","createdAt":"2020-01-01T00:00:00Z",
              "input":{"type":"object","properties":{}},
              "steps":[{"id":"s1","type":"lookup"}],
              "response":{"status":200,"body":{"ok":true}}}]
            """);

        // The editor's save payload never includes `method`; it must be preserved.
        var form = new Dictionary<string, string>
        {
            ["endpointId"] = "ep1",
            ["name"] = "Lookup",
            ["enabled"] = "true",
            ["definition"] = """{"steps":[{"id":"s1","type":"lookup"},{"id":"s2","type":"condition"}]}""",
            ["publishTarget"] = "live",
        };

        await EndpointDashboardMutations.UpdateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None);

        var endpoint = SingleEndpoint(client);
        Assert.Equal("GET", endpoint.GetProperty("method").GetString());
        Assert.Equal("internal", endpoint.GetProperty("exposure").GetString());
        Assert.Equal("2020-01-01T00:00:00Z", endpoint.GetProperty("createdAt").GetString());
        Assert.Equal(2, endpoint.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task UpdateEndpoint_missing_endpoint_throws()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", "[]");
        var form = new Dictionary<string, string> { ["endpointId"] = "nope", ["definition"] = "{}" };

        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => EndpointDashboardMutations.UpdateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None));
    }

    [Fact]
    public async Task CreateEndpoint_persists_definition()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", "[]");

        var form = new Dictionary<string, string>
        {
            ["name"] = "Award Coins",
            ["slug"] = "award-coins",
            ["method"] = "POST",
            ["definition"] = """{"steps":[{"id":"s1","type":"write","collection":"wallets"}]}""",
        };

        await EndpointDashboardMutations.CreateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None);

        var endpoint = SingleEndpoint(client);
        Assert.Equal("award-coins", endpoint.GetProperty("slug").GetString());
        Assert.Equal("POST", endpoint.GetProperty("method").GetString());
        Assert.False(string.IsNullOrEmpty(endpoint.GetProperty("id").GetString()));
        Assert.Equal(1, endpoint.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task CreateEndpoint_duplicate_slug_throws()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", """[{"id":"ep1","slug":"award-coins","name":"X","method":"POST"}]""");

        var form = new Dictionary<string, string>
        {
            ["name"] = "Another",
            ["slug"] = "award-coins",
            ["definition"] = "{}",
        };

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => EndpointDashboardMutations.CreateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task DeleteEndpoint_removes_it()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", """[{"id":"ep1","slug":"save-all","name":"X","method":"POST"}]""");

        await EndpointDashboardMutations.DeleteEndpointAsync(client, OwnerId, ProjectId, "ep1", CancellationToken.None);

        var stored = JsonSerializer.Deserialize<JsonElement>(client.Read("endpoints.json")!);
        Assert.Equal(0, stored.GetArrayLength());
    }

    [Fact]
    public async Task UpdateEndpoint_staged_target_writes_overrides_not_live()
    {
        var client = new StatefulWorkspaceClient();
        client.Seed("endpoints.json", """
            [{"id":"ep1","slug":"save-all","name":"Save All","method":"POST","enabled":true,
              "steps":[{"id":"s1","type":"transform"}],
              "response":{"status":200,"body":{"ok":true}}}]
            """);
        client.Seed("game-package.json", """{"currentRevisionId":3,"latestRevisionId":3}""");

        var form = new Dictionary<string, string>
        {
            ["endpointId"] = "ep1",
            ["name"] = "Save All",
            ["enabled"] = "true",
            ["definition"] = """{"steps":[{"id":"s1","type":"transform"},{"id":"s2","type":"write"}]}""",
            ["publishTarget"] = "staged",
        };

        await EndpointDashboardMutations.UpdateEndpointAsync(client, OwnerId, ProjectId, form, CancellationToken.None);

        // Live store untouched.
        var live = SingleEndpoint(client);
        Assert.Equal(1, live.GetProperty("steps").GetArrayLength());

        // Staged overlay carries the new step keyed by slug.
        var overrides = JsonSerializer.Deserialize<JsonElement>(client.Read("revision-overrides.json")!);
        var staged = overrides.GetProperty("endpoints").GetProperty("save-all");
        Assert.Equal(2, staged.GetProperty("steps").GetArrayLength());
    }

    private static JsonElement SingleEndpoint(StatefulWorkspaceClient client)
    {
        var stored = JsonSerializer.Deserialize<JsonElement>(client.Read("endpoints.json")!);
        Assert.Equal(1, stored.GetArrayLength());
        return stored[0];
    }

    // Faithfully round-trips through JSON like the real ScyllaMetadataWorkspaceClient /
    // BunnyWorkspaceClient (serialize on write, deserialize to the requested T on read),
    // so the JsonElement-valued dictionaries the mutation re-serializes behave as in prod.
    private sealed class StatefulWorkspaceClient : IWorkspaceStore
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly Dictionary<string, string> _store = new();

        public void Seed(string resourcePath, string json) => _store[resourcePath] = json;
        public string? Read(string resourcePath) => _store.TryGetValue(resourcePath, out var v) ? v : null;

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(_store.TryGetValue(resourcePath, out var json)
                ? JsonSerializer.Deserialize<T>(json, Options)
                : default);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        {
            _store[resourcePath] = JsonSerializer.Serialize(data, Options);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>(new List<WorkspaceProject>
            {
                new(Id: ProjectId, Name: "Test", Description: null, Enabled: true, CreatedAt: null, UpdatedAt: null, CompiledAt: null),
            });

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(Read(resourcePath));

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
