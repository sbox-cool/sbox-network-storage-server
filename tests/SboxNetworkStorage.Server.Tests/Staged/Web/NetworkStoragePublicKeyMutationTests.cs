using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Public keys use the same raw api_key for creation, resolution and management;
// secret keys remain masked in the store and in management responses.
public sealed class NetworkStoragePublicKeyMutationTests
{
    [Fact]
    public async Task ToggleAndRead_WorksWithRawPublicKeysAndMaskedSecretKeys()
    {
        var userId = 100L;
        var projectId = "proj_hash_" + Guid.NewGuid().ToString("N")[..12];
        var rawPublicKey = "sbox_ns_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var publicKeyIdentifier = "pkey_hash_1";
        var maskedSecretKey = "sbox_sk_abcd...wxyz";
        var secretKeyIdentifier = "sk_hash_1";

        var emptyPerms = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        var scyllaStore = new InMemoryNetworkStorageStore();

        // Public keys are addressable by the key supplied to game clients.
        await scyllaStore.UpsertApiKeyAsync(projectId, rawPublicKey, userId.ToString(CultureInfo.InvariantCulture),
            "public", "", publicKeyIdentifier, "Test Public", true, emptyPerms, 1, CancellationToken.None);
        await scyllaStore.UpsertApiKeyAsync(projectId, maskedSecretKey, userId.ToString(CultureInfo.InvariantCulture),
            "secret", "", secretKeyIdentifier, "Test Secret", true, emptyPerms, 1, CancellationToken.None);

        // CDN index mapping the public key identifier back to the redisplayable raw key.
        var cdnIndex = new CdnKeyIndex(new[]
        {
            new CdnKeyIndexEntry(rawPublicKey, "public", true, publicKeyIdentifier, DateTimeOffset.UtcNow)
        });

        var cdnWriter = new FakeCdnWriter(cdnIndex);
        var bunny = new StubBunnyWorkspaceClient();
        var service = new NetworkStorageProjectService(
            bunny, bunnyStorageEnumerator: null!, new ConfigurationBuilder().Build(), cdnWriter, scyllaStore: scyllaStore,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkStorageProjectService>.Instance);

        // Toggle using exactly the public key supplied to the client.
        await service.ToggleProjectKeyAsync(userId, projectId, rawPublicKey, CancellationToken.None);

        var keys = await service.GetProjectKeysAsync(userId, projectId, CancellationToken.None);
        var publicKey = Assert.Single(keys, k => k.KeyType == "public");
        Assert.False(publicKey.Enabled, "Public key should have been toggled off");
        Assert.Equal(rawPublicKey, publicKey.Key);
        Assert.Equal(publicKeyIdentifier, publicKey.KeyIdentifier);

        // Toggle back on.
        await service.ToggleProjectKeyAsync(userId, projectId, rawPublicKey, CancellationToken.None);
        keys = await service.GetProjectKeysAsync(userId, projectId, CancellationToken.None);
        publicKey = Assert.Single(keys, k => k.KeyType == "public");
        Assert.True(publicKey.Enabled, "Public key should have been toggled back on");

        // ── 2. Toggle secret key by masked key (raw match in ScyllaDB) ──
        await service.ToggleProjectKeyAsync(userId, projectId, maskedSecretKey, CancellationToken.None);
        keys = await service.GetProjectKeysAsync(userId, projectId, CancellationToken.None);
        var secretKey = Assert.Single(keys, k => k.KeyType == "secret");
        Assert.False(secretKey.Enabled, "Secret key should have been toggled off");
        Assert.Equal(maskedSecretKey, secretKey.Key);

        // Toggle back on.
        await service.ToggleProjectKeyAsync(userId, projectId, maskedSecretKey, CancellationToken.None);
        keys = await service.GetProjectKeysAsync(userId, projectId, CancellationToken.None);
        secretKey = Assert.Single(keys, k => k.KeyType == "secret");
        Assert.True(secretKey.Enabled, "Secret key should have been toggled back on");

        Assert.Equal(2, keys.Count);
    }

    // ── helpers ──

    private sealed class FakeCdnWriter : IStorageKeyCdnWriter
    {
        private readonly CdnKeyIndex? _index;

        public FakeCdnWriter(CdnKeyIndex? index) => _index = index;

        public Task WritePublicKeyFileAsync(string apiKey, long userId, string projectId, bool enabled, string keyType, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSecretKeyFileAsync(string identifier, string keyHash, long userId, string projectId, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UpdateSecretKeyFileAsync(string identifier, string projectId, Dictionary<string, object> updates, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeletePublicKeyFileAsync(string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteSecretKeyFileAsync(string identifier, string projectId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CdnKeyIndex?> ReadKeyIndexAsync(string projectId, CancellationToken cancellationToken)
            => Task.FromResult(_index);

        public Task WriteKeyIndexAsync(string projectId, CdnKeyIndex index, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubBunnyWorkspaceClient : IBunnyWorkspaceClient
    {
        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BunnyProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetRawAsync<T>(string path, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string path, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string path, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
