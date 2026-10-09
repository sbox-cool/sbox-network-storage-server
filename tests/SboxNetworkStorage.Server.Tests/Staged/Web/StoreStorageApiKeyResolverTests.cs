using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class StoreStorageApiKeyResolverTests
{
    private const string ProjectId = "proj_auth_read";
    private const string PublicKey = "sbox_pk_test_public";
    private const string SecretKey = "sbox_sk_abcdefghijklmnopqrstuvwxyz0123456789abcdef";
    private const string EncryptionKey = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task ResolveApiKeyAsync_PublicKeyReadThrowsAfterAwait_ReturnsNull()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(new ThrowingApiKeyStore(readPublicException: new InvalidOperationException("Store unavailable")), cache);

        var result = await resolver.ResolveApiKeyAsync(PublicKey, ProjectId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_SecretKeyListThrowsAfterAwait_ReturnsNull()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(new ThrowingApiKeyStore(listSecretException: new InvalidOperationException("Store unavailable")), cache);

        var result = await resolver.ResolveApiKeyAsync(SecretKey, ProjectId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_OperationCanceledException_Propagates()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var cts = new CancellationTokenSource();
        var resolver = CreateResolver(new ThrowingApiKeyStore(readPublicException: new OperationCanceledException(cts.Token)), cache);

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveApiKeyAsync(PublicKey, ProjectId, cts.Token));
    }

    private static StoreStorageApiKeyResolver CreateResolver(INetworkStorageStore store, IMemoryCache cache)
    {
        var config = BuildConfig();

        return new StoreStorageApiKeyResolver(
            store,
            cache,
            config,
            NullLogger<StoreStorageApiKeyResolver>.Instance);
    }

    private static IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STORAGE_ENCRYPTION_KEY"] = EncryptionKey,
            })
            .Build();

    /// <summary>
    /// Seeds an api_keys row through the store's public upsert, so the row has the
    /// production shape (snake_case columns, permissions_json as a parsed object).
    /// The secret identifier is derived with the same STORAGE_ENCRYPTION_KEY the
    /// resolver uses, and the hash is the real SHA-256 of the raw key.
    /// </summary>
    private static Task SeedSecretKeyRow(InMemoryNetworkStorageStore store, IConfiguration config, string projectId, object permissions, string? keyHash = null)
    {
        var identifier = StorageKeyCrypto.DeriveSecretKeyIdentifier(SecretKey, projectId, config);
        var hash = keyHash ?? StorageKeyCrypto.HashSecretKey(SecretKey);
        return store.UpsertApiKeyAsync(projectId, SecretKey, "42", "secret", hash, identifier, "test", enabled: true,
            JsonSerializer.SerializeToElement(permissions), version: 1, CancellationToken.None);
    }

    // ── Happy-path resolution against seeded rows ───────────────────────────
    // The route tests fake IStorageApiKeyResolver; these pin the real
    // StoreStorageApiKeyResolver dispatch (sbox_sk_ prefix → secret path) and
    // the identifier/hash verification that gate every endpoint call.

    [Fact]
    public async Task ResolveApiKeyAsync_PublicKeyRow_ReturnsEnabledPublicAuth()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertApiKeyAsync(ProjectId, PublicKey, "42", "public", keyHash: "", keyIdentifier: "",
            label: "game", enabled: true, JsonSerializer.SerializeToElement(new { records = "rw" }), version: 1, CancellationToken.None);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await CreateResolver(store, cache).ResolveApiKeyAsync(PublicKey, ProjectId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("public", result.KeyType);
        Assert.True(result.Enabled);
        Assert.Equal(42, result.UserId);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_PublicKeyRow_Disabled_ReturnsDisabledAuth()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertApiKeyAsync(ProjectId, PublicKey, "42", "public", keyHash: "", keyIdentifier: "",
            label: "game", enabled: false, JsonSerializer.SerializeToElement(new { records = "rw" }), version: 1, CancellationToken.None);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await CreateResolver(store, cache).ResolveApiKeyAsync(PublicKey, ProjectId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_SecretKeyRow_IdentifierAndHashMatch_ReturnsSecretAuthWithPermissions()
    {
        var store = new InMemoryNetworkStorageStore();
        await SeedSecretKeyRow(store, BuildConfig(), ProjectId, new { endpoints = "x" });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await CreateResolver(store, cache).ResolveApiKeyAsync(SecretKey, ProjectId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("secret", result.KeyType);
        Assert.True(result.Enabled);
        Assert.Equal(42, result.UserId);
        Assert.NotNull(result.Permissions);
        Assert.Equal("x", result.Permissions["endpoints"]);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_SecretKeyRow_HashMismatch_ReturnsNull()
    {
        var store = new InMemoryNetworkStorageStore();
        await SeedSecretKeyRow(store, BuildConfig(), ProjectId, new { endpoints = "x" },
            keyHash: "0000000000000000000000000000000000000000000000000000000000000000");

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await CreateResolver(store, cache).ResolveApiKeyAsync(SecretKey, ProjectId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveApiKeyAsync_SecretKeyRow_OtherProject_ReturnsNull()
    {
        var store = new InMemoryNetworkStorageStore();
        await SeedSecretKeyRow(store, BuildConfig(), "proj_other", new { endpoints = "x" });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await CreateResolver(store, cache).ResolveApiKeyAsync(SecretKey, ProjectId, CancellationToken.None);

        // The identifier is derived per-project and the row is scoped to
        // proj_other, so resolving for ProjectId must miss.
        Assert.Null(result);
    }

    private sealed class ThrowingApiKeyStore : EmptyNetworkStorageStore
    {
        private readonly Exception? _readPublicException;
        private readonly Exception? _listSecretException;

        public ThrowingApiKeyStore(Exception? readPublicException = null, Exception? listSecretException = null)
        {
            _readPublicException = readPublicException;
            _listSecretException = listSecretException;
        }

        public override async Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct)
        {
            await Task.Yield();
            if (_readPublicException is not null)
                throw _readPublicException;
            return null;
        }

        public override async Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct)
        {
            await Task.Yield();
            if (_listSecretException is not null)
                throw _listSecretException;
            return Array.Empty<JsonElement>();
        }
    }
}
