using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageRevisionSettingsTests
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
    public async Task UpdateProjectSettings_Revisions_PersistsRevisionPolicyFields()
    {
        var now = DateTimeOffset.UtcNow;
        var bunny = new FakeBunny();
        bunny.Projects.Add(new WorkspaceProject("proj_one", "One", null, true, now, now, null));
        var service = CreateService(bunny);

        await service.UpdateProjectSettingsAsync(
            42,
            "proj_one",
            "revisions",
            new Dictionary<string, string>
            {
                ["revisionEnforcementEnabled"] = "true",
                ["revisionShowDefaultMessage"] = "true",
                ["revisionEnforcementMode"] = "force_upgrade",
                ["revisionGracePeriodMinutes"] = "120",
                ["revisionPostGraceAction"] = "block_all",
                ["revisionForceEndpointUpgrade"] = "true",
                ["revisionNotifyMessage"] = "Update available.",
                ["revisionShowNewVersionBanner"] = "true",
                ["revisionShowUpdateOptions"] = "true",
                ["revisionEscalateAfterMinutes"] = "240",
                ["revisionShowPopupOnce"] = "true",
                ["revisionTestOutdatedEditor"] = "true",
                ["revisionTestOutdatedLive"] = "true",
            },
            CancellationToken.None);

        var saved = Assert.Single(bunny.Projects);
        Assert.True(saved.RevisionEnforcementEnabled);
        Assert.Equal("force_upgrade", saved.RevisionEnforcementMode);
        Assert.Equal(120, saved.RevisionGracePeriodMinutes);
        Assert.Equal("block_all", saved.RevisionPostGraceAction);
        Assert.True(saved.RevisionForceEndpointUpgrade);
        Assert.Equal("Update available.", saved.RevisionNotifyMessage);
        Assert.True(saved.RevisionShowDefaultMessage);
        Assert.True(saved.RevisionShowNewVersionBanner);
        Assert.True(saved.RevisionShowUpdateOptions);
        Assert.Equal(240, saved.RevisionEscalateAfterMinutes);
        Assert.True(saved.RevisionShowPopupOnce);
        Assert.True(saved.RevisionTestOutdatedEditor);
        Assert.True(saved.RevisionTestOutdatedLive);
        Assert.Single(bunny.ProjectSaves);
    }

    private sealed class FakeBunny : IWorkspaceStore
    {
        public List<WorkspaceProject> Projects { get; } = new();
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
            => Task.FromResult<T?>(default);
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
