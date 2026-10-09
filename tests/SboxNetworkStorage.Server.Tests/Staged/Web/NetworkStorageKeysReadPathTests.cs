using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Regression coverage for the API Keys read path. The page previously rendered
// an empty key list whenever the Postgres read failed for ANY reason — a missing
// table, a misconfigured pool, or a connection error all surfaced to the user as
// a misleading "you have no API keys" page. These tests pin the corrected
// behavior: genuine database errors must propagate (so the exception middleware
// captures them in /admin/errors), and a fully unconfigured database falls back
// to the workspace keys.json resource instead of crashing.
public sealed class NetworkStorageKeysReadPathTests
{
    [Fact]
    public async Task GetProjectKeysAsync_DoesNotSwallowBackendErrors()
    {
        // The store returns no keys; the workspace fallback throws. The read path
        // must propagate the error rather than silently returning an empty list.
        var workspace = new ThrowingWorkspaceClient();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = new NetworkStorageProjectService(workspace, workspaceStorageEnumerator: null!, config, new ThrowingCdnWriter(),
            networkStore: new InMemoryNetworkStorageStore(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkStorageProjectService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.GetProjectKeysAsync(42, "ec13d753a7ea4d66", CancellationToken.None));
    }

    [Fact]
    public async Task GetProjectKeysAsync_StoreOutage_ReturnsEmptyInsteadOfThrowing()
    {
        // When a store node is down, ListApiKeysAsync throws (NoHostAvailable,
        // socket, etc.). The project page loads during access resolution before
        // the dashboard try/catch, so a throw here would surface as HTTP 500.
        // The read path must degrade to an empty key list (logged) instead.
        var workspace = new ThrowingWorkspaceClient();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = new NetworkStorageProjectService(workspace, workspaceStorageEnumerator: null!, config, new ThrowingCdnWriter(),
            networkStore: new ThrowingStore(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkStorageProjectService>.Instance);

        var keys = await service.GetProjectKeysAsync(42, "ec13d753a7ea4d66", CancellationToken.None);

        Assert.Empty(keys);
    }

    [Fact]
    public async Task GetProjectKeysAsync_NoDatabaseConfigured_FallsBackToKeysJson()
    {
        // No pool has a connection string, so none is registered. The read path
        // must degrade to the workspace keys.json resource rather than throw.

        var fallbackKey = new ApiKeyInfo(
            Key: "sbox_ns_fallback",
            Label: "Fallback",
            KeyType: "public",
            Enabled: true,
            KeyIdentifier: "pkey_fallback",
            CreatedAt: DateTimeOffset.UtcNow,
            Permissions: null);
        var workspace = new KeysJsonWorkspaceClient(new List<ApiKeyInfo> { fallbackKey });
        var config2 = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = new NetworkStorageProjectService(workspace, workspaceStorageEnumerator: null!, config2, new ThrowingCdnWriter(), networkStore: new InMemoryNetworkStorageStore(), logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkStorageProjectService>.Instance);

        var keys = await service.GetProjectKeysAsync(42, "ec13d753a7ea4d66", CancellationToken.None);

        Assert.Single(keys);
        Assert.Equal("sbox_ns_fallback", keys[0].Key);
    }

    private sealed class ThrowingWorkspaceClient : IWorkspaceStore
    {
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingCdnWriter : SboxNetworkStorage.Infrastructure.NetworkStorage.IStorageKeyCdnWriter
    {
        public Task WritePublicKeyFileAsync(string apiKey, long userId, string projectId, bool enabled, string keyType, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task WriteSecretKeyFileAsync(string identifier, string keyHash, long userId, string projectId, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task UpdateSecretKeyFileAsync(string identifier, string projectId, Dictionary<string, object> updates, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task DeletePublicKeyFileAsync(string apiKey, string projectId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task DeleteSecretKeyFileAsync(string identifier, string projectId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task<SboxNetworkStorage.Infrastructure.NetworkStorage.CdnKeyIndex?> ReadKeyIndexAsync(string projectId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
        public Task WriteKeyIndexAsync(string projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.CdnKeyIndex index, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");
    }

    private sealed class KeysJsonWorkspaceClient(List<ApiKeyInfo> keys) : IWorkspaceStore
    {
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (resourcePath == "keys.json" && keys is T typed)
            {
                return Task.FromResult<T?>(typed);
            }

            return Task.FromResult<T?>(default);
        }

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("keys.json fallback must not be reached when the database errors.");

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
    /// <summary>
    /// A store store that throws on <c>ListApiKeysAsync</c>, simulating a node
    /// outage (e.g. <c>NoHostAvailableException</c> when all nodes are unreachable).
    /// </summary>
    private sealed class ThrowingStore : InMemoryNetworkStorageStore
    {
        public override Task<IReadOnlyList<System.Text.Json.JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct)
            => throw new InvalidOperationException("No host available");
    }
}
