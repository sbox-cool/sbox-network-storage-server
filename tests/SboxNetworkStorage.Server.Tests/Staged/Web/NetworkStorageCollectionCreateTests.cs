using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageCollectionCreateTests
{
    private static NetworkStorageProjectService CreateService(FakeWorkspace workspace)
        => new(
            workspace,
            workspaceStorageEnumerator: null!,
            new ConfigurationBuilder().Build(),
            keyCdnWriter: null!,
            networkStore: new InMemoryNetworkStorageStore(),
            NullLogger<NetworkStorageProjectService>.Instance);

    [Fact]
    public async Task UpdateProjectSettings_CollectionCreate_WritesCollectionsJson()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new FakeWorkspace();
        workspace.Projects.Add(new WorkspaceProject("proj_one", "One", null, true, now, now, null));
        var service = CreateService(workspace);

        await service.UpdateProjectSettingsAsync(
            42,
            "proj_one",
            "collection-create",
            new Dictionary<string, string>
            {
                ["name"] = "player_stats",
                ["description"] = "Per-player stats",
                ["schema"] = """{"type":"object","properties":{}}""",
                ["collectionType"] = "per-steamid",
                ["initialConstants"] = "[]",
                ["initialTables"] = "[]",
                ["authoringMode"] = "source",
                ["sourceFormat"] = "yaml",
                ["sourceText"] = "sourceVersion: 1\nkind: collection\nname: player_stats",
            },
            CancellationToken.None);

        Assert.Single(workspace.ProjectResourceWrites);
        var write = workspace.ProjectResourceWrites[0];
        Assert.Equal("collections.json", write.ResourcePath);
        Assert.Single(write.Payload);
        Assert.Equal("player_stats", write.Payload[0]["name"]);
        Assert.Equal("per-steamid", write.Payload[0]["collectionType"]);
        Assert.Equal("endpoint", write.Payload[0]["accessMode"]);
    }

    [Fact]
    public async Task UpdateProjectSettings_CollectionCreate_RejectsDuplicateName()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new FakeWorkspace();
        workspace.Projects.Add(new WorkspaceProject("proj_one", "One", null, true, now, now, null));
        workspace.Collections.Add(new Dictionary<string, object?> { ["id"] = "abc", ["name"] = "players" });
        var service = CreateService(workspace);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProjectSettingsAsync(
            42,
            "proj_one",
            "collection-create",
            new Dictionary<string, string>
            {
                ["name"] = "players",
                ["schema"] = """{"type":"object","properties":{}}""",
            },
            CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeWorkspace : IWorkspaceStore
    {
        public List<WorkspaceProject> Projects { get; } = new();
        public List<IReadOnlyList<WorkspaceProject>> ProjectSaves { get; } = new();
        public List<Dictionary<string, object?>> Collections { get; } = new();
        public List<ProjectResourceWrite> ProjectResourceWrites { get; } = new();

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
        {
            if (resourcePath == "collections.json" && typeof(T) == typeof(List<Dictionary<string, object?>>))
                return Task.FromResult<T?>((T?)(object)Collections.ToList());
            return Task.FromResult<T?>(default);
        }

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        {
            if (resourcePath == "collections.json" && data is List<Dictionary<string, object?>> list)
            {
                Collections.Clear();
                Collections.AddRange(list);
                ProjectResourceWrites.Add(new ProjectResourceWrite(resourcePath, list));
            }
            return Task.CompletedTask;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record ProjectResourceWrite(string ResourcePath, List<Dictionary<string, object?>> Payload);
}
