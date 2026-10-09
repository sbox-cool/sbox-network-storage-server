using System;
using System.Collections.Generic;
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

// Parity tests for Network Storage key management.
// These tests verify byte-for-byte parity between the .NET C# implementation
// and the existing JS data plane for secret-key hashing, identifier derivation,
// and CDN file writes.
public sealed class NetworkStorageKeyParityTests
{
    [Fact]
    public async Task HashSecretKey_ProducesSameHashAsJs()
    {
        // The JS implementation: createHash("sha256").update(rawKey, "utf8").digest("hex")
        var rawKey = "sbox_sk_" + new string('a', 48);
        var expected = await ComputeJsStyleSha256Hex(rawKey);

        var actual = StorageKeyCrypto.HashSecretKey(rawKey);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task DeriveSecretKeyIdentifier_ProducesSameIdentifierAsJs()
    {
        var rawKey = "sbox_sk_" + new string('b', 48);
        var projectId = "proj_" + new string('c', 12);
        var encryptionKey = new string('d', 64); // 64-char hex = 32 bytes

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STORAGE_ENCRYPTION_KEY"] = encryptionKey
            })
            .Build();

        // JS: createHmac("sha256", keyBytes).update(`${rawKey}:${projectId}`, "utf8").digest("hex")
        var expected = await ComputeJsStyleHmacHex(encryptionKey, $"{rawKey}:{projectId}");

        var actual = StorageKeyCrypto.DeriveSecretKeyIdentifier(rawKey, projectId, config);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MaskSecretKey_MatchesJsMaskSecretKey()
    {
        var rawKey = "sbox_sk_" + new string('e', 48);
        var expected = $"sbox_sk_{rawKey[8..12]}...{rawKey[^4..]}";

        var actual = StorageKeyCrypto.MaskSecretKey(rawKey);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MaskSecretKey_ShortKey_ReturnsPlaceholder()
    {
        var actual = StorageKeyCrypto.MaskSecretKey("short");
        Assert.Equal("sbox_sk_****", actual);
    }

    [Fact]
    public async Task CreateProjectKeyAsync_SecretKey_StoresInScyllaAndWritesCdn()
    {
        var userId = 99L;
        var projectId = "proj_test_" + Guid.NewGuid().ToString("N")[..12];
        var encryptionKey = new string('1', 64);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STORAGE_ENCRYPTION_KEY"] = encryptionKey
            })
            .Build();
        var cdnWrites = new List<CdnWriteRecord>();
        var bunny = new RecordingBunnyWorkspaceClient(cdnWrites);

        var service = new NetworkStorageProjectService(bunny, bunnyStorageEnumerator: null!, config, new RecordingCdnWriter(cdnWrites), scyllaStore: new InMemoryNetworkStorageStore(), logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NetworkStorageProjectService>.Instance);

        var (key, rawKey) = await service.CreateProjectKeyAsync(userId, projectId, "Test Secret", "secret", null, CancellationToken.None);

        Assert.StartsWith("sbox_sk_", rawKey);
        Assert.Equal("secret", key.KeyType);
        Assert.NotNull(key.KeyIdentifier);
        Assert.StartsWith("sbox_sk_", key.Key);
        Assert.NotEqual(rawKey, key.Key); // stored key is masked

        // ScyllaDB is the sole API-key store: the new key is queryable from it.
        var keys = await service.GetProjectKeysAsync(userId, projectId, CancellationToken.None);
        Assert.Single(keys);
        Assert.Equal(key.KeyIdentifier, keys[0].KeyIdentifier);

        // The secret key's CDN file was written under its derived identifier.
        Assert.Contains(cdnWrites, w => w.Path == $"secret:{key.KeyIdentifier}");
    }

    private static async Task<string> ComputeJsStyleSha256Hex(string rawKey)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeJsStyleHmacHex(string hexKey, string message)
    {
        var keyBytes = Convert.FromHexString(hexKey[..64]);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var signature = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private sealed class RecordingBunnyWorkspaceClient(List<CdnWriteRecord> records) : IWorkspaceStore
    {
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
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
        {
            records.Add(new CdnWriteRecord(path, data));
            return Task.CompletedTask;
        }

        public Task DeleteRawAsync(string path, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingCdnWriter(List<CdnWriteRecord> records) : IStorageKeyCdnWriter
    {
        public Task WritePublicKeyFileAsync(string apiKey, long userId, string projectId, bool enabled, string keyType, CancellationToken cancellationToken)
        {
            records.Add(new CdnWriteRecord($"public:{apiKey}", new { apiKey, userId, projectId, enabled, keyType }));
            return Task.CompletedTask;
        }

        public Task WriteSecretKeyFileAsync(string identifier, string keyHash, long userId, string projectId, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
        {
            records.Add(new CdnWriteRecord($"secret:{identifier}", new { identifier, keyHash, userId, projectId, permissions }));
            return Task.CompletedTask;
        }

        public Task UpdateSecretKeyFileAsync(string identifier, string projectId, Dictionary<string, object> updates, CancellationToken cancellationToken)
        {
            records.Add(new CdnWriteRecord($"update:{identifier}", updates));
            return Task.CompletedTask;
        }

        public Task DeletePublicKeyFileAsync(string apiKey, string projectId, CancellationToken cancellationToken)
        {
            records.Add(new CdnWriteRecord($"delete-public:{apiKey}", null));
            return Task.CompletedTask;
        }

        public Task DeleteSecretKeyFileAsync(string identifier, string projectId, CancellationToken cancellationToken)
        {
            records.Add(new CdnWriteRecord($"delete-secret:{identifier}", null));
            return Task.CompletedTask;
        }

        public Task<SboxNetworkStorage.Infrastructure.NetworkStorage.CdnKeyIndex?> ReadKeyIndexAsync(string projectId, CancellationToken cancellationToken)
            => Task.FromResult<SboxNetworkStorage.Infrastructure.NetworkStorage.CdnKeyIndex?>(null);

        public Task WriteKeyIndexAsync(string projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.CdnKeyIndex index, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record CdnWriteRecord(string Path, object? Data);
}
