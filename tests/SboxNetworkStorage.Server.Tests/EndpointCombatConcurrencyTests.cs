using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class EndpointCombatConcurrencyTests<TFactory>(TFactory factory) : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    [SkippableFact]
    public async Task ConcurrentFinishCombatPaysAndPersistsExactlyOnceWhenSaveVersionIsRequired()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        using var collection = JsonDocument.Parse("""{"collectionType":"per-steamid","requireSaveVersion":true}""");
        using var player = JsonDocument.Parse("""{"xp":0,"combat":{"started_ms":1,"weapon_id":"pistol","zombie_level":3,"zombie_hp":60}}""");
        using var endpoint = JsonDocument.Parse("""
        {
          "steps":[
            {"id":"player","type":"read","collection":"players","key":"{{steamId}}"},
            {"id":"in_combat","type":"condition","check":{"field":"player.combat.started_ms","op":"exists"},"onFail":{"status":409,"error":"NO_COMBAT","message":"Start combat first."}},
            {"id":"reward","type":"write","collection":"players","key":"{{steamId}}","ops":[
              {"op":"inc","path":"xp","value":45},
              {"op":"set","path":"combat","value":{"ended_ms":"{{_unixMs}}"}}
            ]}
          ],
          "response":{"status":200,"body":{"success":true,"xp":45}}
        }
        """);
        await store.UpsertCollectionAsync("combat", "players", "Players", "public", collection.RootElement, 1, default);
        await store.UpsertEndpointAsync("combat", "finish-combat", "finish-combat", "POST", true, endpoint.RootElement, null, 1, default);
        await store.UpsertRecordAsync("combat", "players", "76561198000000001", player.RootElement, false, 1, default);

        var inner = new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance);
        var source = new TwoReaderBarrierSource(inner);
        var executor = new EndpointExecutor(source);
        var calls = Enumerable.Range(0, 2).Select(_ => executor.TryExecuteAsync(
            "combat", "finish-combat", new Dictionary<string, object?>(), "76561198000000001", "owner",
            new Dictionary<string, object?>(), hasSecretKey: true, isDedicatedServer: false, ct: default, liveServe: true)).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, results.Count(result => result!.Status == 200));
        Assert.Equal(1, results.Count(result => result!.Status == 409));
        var saved = await store.ReadRecordAsync("combat", "players", "76561198000000001", default);
        Assert.NotNull(saved);
        Assert.Equal(45, saved.Value.GetProperty("payload_json").GetProperty("xp").GetInt32());
        Assert.Equal(2, saved.Value.GetProperty("version").GetInt64());
    }

    [SkippableFact]
    public async Task ProtectedEndpointTreatsInvalidInputRecordKeyAsMissing()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var store = await factory.NewStoreAsync();
        using var collection = JsonDocument.Parse("""{"collectionType":"per-steamid","requireSaveVersion":true}""");
        using var endpoint = JsonDocument.Parse("""
        {
          "steps":[
            {"id":"player","type":"read","collection":"players","key":"{{input.key}}"},
            {"id":"exists","type":"condition","check":{"field":"player.xp","op":"exists"},"onFail":{"status":404,"error":"PLAYER_NOT_FOUND","message":"Player not found."}}
          ],
          "response":{"status":200,"body":{"ok":true}}
        }
        """);
        await store.UpsertCollectionAsync("combat", "players", "Players", "public", collection.RootElement, 1, default);
        await store.UpsertEndpointAsync("combat", "read-player", "read-player", "POST", true, endpoint.RootElement, null, 1, default);

        var executor = new EndpointExecutor(new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance));
        var result = await executor.TryExecuteAsync(
            "combat", "read-player", new Dictionary<string, object?> { ["key"] = "invalid.key" },
            "76561198000000001", "owner", new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: false, ct: default, liveServe: true);

        Assert.NotNull(result);
        Assert.Equal(404, result!.Status);
    }
    private sealed class TwoReaderBarrierSource(StoreEndpointDataSource inner) : IEndpointDataSource, IEndpointSnapshotDataSource
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;

        public async Task<EndpointRecordSnapshot> ReadRecordSnapshotAsync(string projectId, string collectionId, string key, CancellationToken ct)
        {
            var snapshot = await inner.ReadRecordSnapshotAsync(projectId, collectionId, key, ct);
            if (Interlocked.Increment(ref _readers) == 2) _bothRead.TrySetResult();
            await _bothRead.Task.WaitAsync(ct);
            return snapshot;
        }

        public Task<IReadOnlyList<EndpointRecordRead>> ScanCollectionSnapshotsAsync(string projectId, string collectionId, CancellationToken ct)
            => inner.ScanCollectionSnapshotsAsync(projectId, collectionId, ct);
        public Task<IEndpointWriteTransaction> BeginCheckedWriteTransactionAsync(string projectId, EndpointReadSet readSet, CancellationToken ct)
            => inner.BeginCheckedWriteTransactionAsync(projectId, readSet, ct);
        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
            => inner.ReadEndpointDefinitionAsync(projectId, endpointSlug, ct);
        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => inner.ListCollectionsAsync(projectId, ct);
        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
            => inner.ReadRecordAsync(projectId, collectionId, key, ct);
        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => inner.ScanCollectionAsync(projectId, collectionId, ct);
        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => inner.ReadWorkflowDefinitionAsync(projectId, workflowId, ct);
        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct) => inner.BeginWriteTransactionAsync(ct);
        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => inner.ReadGlobalRecordAsync(projectId, collectionId, recordId, ct);
        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => inner.WriteGlobalRecordAsync(projectId, collectionId, recordId, payload, ct);
        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct)
            => inner.IsLegacyPlayerProjectionsEnabledAsync(projectId, ct);
    }
}

public sealed class SqliteEndpointCombatConcurrencyTests(SqliteHostFactory factory) : EndpointCombatConcurrencyTests<SqliteHostFactory>(factory);
public sealed class PostgresEndpointCombatConcurrencyTests(PostgresHostFactory factory) : EndpointCombatConcurrencyTests<PostgresHostFactory>(factory);
