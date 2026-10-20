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
        // Queue off: this test characterizes the CAS conflict the barrier forces. With the queue on, the first caller
        // would hold the record while it waits at the barrier for a second reader that never arrives.
        var source = new ReadBarrierDataSource(inner, participants: 2);
        var executor = new EndpointExecutor(source, writeQueue: new EndpointWriteQueue(enabled: false));
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
}

public sealed class SqliteEndpointCombatConcurrencyTests(SqliteHostFactory factory) : EndpointCombatConcurrencyTests<SqliteHostFactory>(factory);
public sealed class PostgresEndpointCombatConcurrencyTests(PostgresHostFactory factory) : EndpointCombatConcurrencyTests<PostgresHostFactory>(factory);
