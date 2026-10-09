using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Server.Tests.Support;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

public sealed class NativeEndpointShadowExecutorTests
{
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.Parse("2026-06-10T00:00:00Z");

    [Fact]
    public async Task TryExecute_ReadConditionResponse_ExecutesNatively()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("player", "players", "{{steamId}}"),
                    MakeConditionStep("afford", "player.coins", ">=", "{{input.price}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true, ["passed"] = "{{afford}}" }),
        };
        var records = new Dictionary<string, object?> { ["proj:players:steam1"] = new Dictionary<string, object?> { ["coins"] = 250d, ["name"] = "cerbralone" } };

        var ds = new FakeDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["price"] = 100d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(200, result.Status);
        var body = (Dictionary<string, object?>)result.Body!;
        Assert.True(body.ContainsKey("ok"));
    }
    [Fact]
    public async Task TryExecute_OversizedScanCollection_FailsClosed()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:big"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "f", ["type"] = "filter", ["collection"] = "big",
                        ["where"] = new Dictionary<string, object?> { ["field"] = "score", ["op"] = ">=", ["value"] = 0d },
                    },
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>();
        for (var i = 0; i <= EndpointExecutor.MaxScanRows; i++)
            records[$"proj:big:player-{i}"] = new Dictionary<string, object?> { ["score"] = (double)i };
        var ds = new FakeDataSource(store, records);
        var executor = new EndpointExecutor(ds);
        var result = await executor.TryExecuteAsync(
            "proj", "big",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal(400, result.Status);
        var error = (Dictionary<string, object?>)((Dictionary<string, object?>)result.Body!)["error"];
        Assert.Equal("SCAN_TOO_LARGE", error["code"]);
    }

    [Fact]
    public async Task TryExecute_ScanAtCap_FiltersNormally()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:big"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "f", ["type"] = "filter", ["collection"] = "big",
                        ["where"] = new Dictionary<string, object?> { ["field"] = "score", ["op"] = ">=", ["value"] = 0d },
                    },
                },
                body: new Dictionary<string, object?> { ["ok"] = true, ["matched"] = "{{f}}" }),
        };
        var records = new Dictionary<string, object?>();
        for (var i = 0; i < EndpointExecutor.MaxScanRows; i++)
            records[$"proj:big:player-{i}"] = new Dictionary<string, object?> { ["score"] = (double)i };
        var ds = new FakeDataSource(store, records);
        var executor = new EndpointExecutor(ds);
        var result = await executor.TryExecuteAsync(
            "proj", "big",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.Ok);
    }

    [Fact]
    public async Task TryExecute_UnsupportedStepType_ReturnsNullFallback()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("player", "players", "{{steamId}}"),
                    new Dictionary<string, object?> { ["id"] = "unk", ["type"] = "custom_unknown_type" },
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new FakeDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);
        Assert.Null(result); // falls back to Bun
    }

    [Fact]
    public async Task TryExecute_RoutedFlow_HandlesGotoRoute()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "c", ["type"] = "condition",
                        ["check"] = new Dictionary<string, object?> { ["field"] = "input.x", ["op"] = "==", ["value"] = 1d },
                        ["routes"] = new Dictionary<string, object?> { ["false"] = new Dictionary<string, object?> { ["action"] = "goto", ["step"] = "other" } },
                    },
                    new Dictionary<string, object?> { ["id"] = "other", ["type"] = "transform", ["value"] = "reached" },
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new FakeDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Ok);
    }


    [Fact]
    public async Task TryExecute_EndpointNotFound_ReturnsNull()
    {
        var ds = new FakeDataSource(new Dictionary<string, object?>(), new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "nonexistent",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryExecute_DataSourceUnavailable_ReturnsNull()
    {
        var ds = new ThrowOnDefinitionRead();
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryExecute_PrefetchesRecordsFromDataSource()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("player", "players", "{{steamId}}"),
                    MakeTransformStep("level", "{{player.totalLevel}} + 1"),
                },
                body: new Dictionary<string, object?> { ["level"] = "{{level}}" }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 399d, ["name"] = "cerbralone" },
        };

        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(400d, ((Dictionary<string, object?>)result.Body!)["level"]); // 399 + 1
        // Verify the data source was queried for the record
        Assert.Contains(("players", "steam1"), ds.RecordsRead);
    }

    [Fact]
    public async Task TryExecute_RecordMissing_NativeReturnsNullRecord()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?> { MakeReadStep("player", "players", "{{steamId}}") },
                body: new Dictionary<string, object?> { ["found"] = "{{player | null}}" }),
        };
        var ds = new FakeDataSource(store, new Dictionary<string, object?>()); // no records
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var body = (Dictionary<string, object?>)result.Body!;
        Assert.Equal("null", EndpointExpression.JsToString(body["found"]));
    }

    [Fact]
    public async Task TryExecute_LiveServe_WriteStep_FlushesDurably()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save"] = MakeEndpointDef(
                steps: new List<object?> { MakeWriteStep("save", "players", "{{steamId}}", "coins", 500d) },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "save",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(("players", "steam1"), (write.Collection, write.Key));
        Assert.Equal(500d, write.Payload["coins"]);
    }

    [Fact]
    public async Task TryExecute_ShadowMode_WriteStep_DoesNotFlushDurably()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save"] = MakeEndpointDef(
                steps: new List<object?> { MakeWriteStep("save", "players", "{{steamId}}", "coins", 500d) },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        // liveServe defaults to false (shadow): Bun's shadow adapter owns the write.
        var result = await executor.TryExecuteAsync(
            "proj", "save",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Empty(ds.RecordsWritten);
    }

    [Fact]
    public async Task TryExecute_LiveServe_DeleteStep_FlushesDurably()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:wipe"] = MakeEndpointDef(
                steps: new List<object?> { MakeDeleteStep("wipe", "players", "{{steamId}}") },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "wipe",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(("players", "steam1"), Assert.Single(ds.RecordsDeleted));
    }

    [Fact]
    public async Task TryExecute_LiveServe_WriteFails_ReturnsServerError_NotNull()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save"] = MakeEndpointDef(
                steps: new List<object?> { MakeWriteStep("save", "players", "{{steamId}}", "coins", 500d) },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new WriteThrowsDataSource(store);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "save",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        // Fail-closed: must NOT return null (that would trigger a Bun re-run / double-write).
        Assert.NotNull(result);
        Assert.Equal(500, result!.Status);
        var body = (Dictionary<string, object?>)result.Body!;
        var error = (Dictionary<string, object?>)body["error"]!;
        Assert.Equal("STORAGE_WRITE_FAILED", error["code"]);
    }

    [Fact]
    public async Task TryExecute_LiveServe_WebhookEndpoint_NoSender_FallsBackToBun()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:notify"] = MakeEndpointDef(
                steps: new List<object?> { MakeWebhookStep("notify") },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new FakeDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds); // no webhook sender

        var result = await executor.TryExecuteAsync(
            "proj", "notify",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        Assert.Null(result); // defer to Bun so the notification is not silently dropped
    }

    [Fact]
    public async Task TryExecute_LiveServe_WebhookEndpoint_WithSender_Sends()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:notify"] = MakeEndpointDef(
                steps: new List<object?> { MakeWebhookStep("notify") },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new FakeDataSource(store, new Dictionary<string, object?>());
        var sender = new FakeWebhookSender();
        var executor = new EndpointExecutor(ds, sender);

        var result = await executor.TryExecuteAsync(
            "proj", "notify",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: true, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var sent = Assert.Single(sender.Sent);
        Assert.StartsWith("https://discord.com/api/webhooks/", sent.Url);
    }

    [Fact]
    public async Task TryExecute_ReadStep_ResolvesCollectionNameAndStripsDefaultKey()
    {
        // Endpoint YAML uses collection NAME ("players") and "{{steamId}}_default"
        // as the key. The executor must: (1) resolve "players" → collection ID,
        // and (2) strip "_default" from the key. Both are needed or reads return null.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("player", "players", "{{steamId}}_default"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true, ["player"] = "{{player}}" }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:col-players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 15d },
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
        };
        var ds = new RecordingDataSource(store, records, collections);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var readKey = Assert.Single(ds.RecordsRead);
        Assert.Equal("col-players", readKey.Item1);
        Assert.Equal("steam1", readKey.Item2);
    }

    [Fact]
    public async Task TryExecute_LiveServe_WriteStep_ResolvesCollectionNameAndStripsDefaultKey()
    {
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeWriteStep("save", "players", "{{steamId}}_default", "totalLevel", "{{input.level}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>(), collections);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["level"] = 20d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var written = Assert.Single(ds.RecordsWritten);
        Assert.Equal("col-players", written.Collection);
        Assert.Equal("steam1", written.Key);
    }

    [Fact]
    public void JsNumber_LongValue_ReturnsCorrectDouble()
    {
        // ParseInput used to convert integers to long, but JsNumber had no
        // case for long — returning NaN. This caused num(input.totalLevel, 0)
        // to resolve to 0, blocking all saves with SAVE_REGRESSION_BLOCKED.
        Assert.Equal(81.0, EndpointExpression.JsNumber((object)81L));
        Assert.Equal(42.0, EndpointExpression.JsNumber((object)42));
        Assert.Equal(3.14, EndpointExpression.JsNumber((object)3.14m));
        Assert.Equal(1.5, EndpointExpression.JsNumber((object)1.5f));
    }

    [Fact]
    public async Task TryExecute_AssertWithIntegerInput_PassesWhenGreaterOrEqual()
    {
        // Reproduces the save-all guard: num(input.totalLevel, 0) >= num(existing.totalLevel, 0)
        // The input.totalLevel must resolve to the actual integer value (81), not 0.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    new Dictionary<string, object?>
                    {
                        ["id"] = "guard_level",
                        ["type"] = "assert",
                        ["check"] = new Dictionary<string, object?>
                        {
                            ["expression"] = "{{num(input.totalLevel, 0)}} >= {{num(existing.totalLevel, 0)}}"
                        },
                        ["errorCode"] = "SAVE_REGRESSION_BLOCKED",
                        ["message"] = "Refusing to overwrite totalLevel {{num(existing.totalLevel, 0)}} with lower value {{num(input.totalLevel, 0)}}.",
                        ["status"] = 409,
                    },
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 81d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Single(ds.RecordsWritten);
    }

    [Fact]
    public async Task TryExecute_AntiRollbackBelowStored_HealsAndPersistsHighWaterMark()
    {
        // The 2026-06-19 "satu" deadlock: players.totalLevel cache (80) drifted
        // ABOVE the client's recomputed input (79). The anti-rollback guard used to
        // 409 EVERY save forever. Now the executor heals the input UP to the stored
        // high-water mark so the guard passes, the save persists, and progress is
        // NEVER lowered.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 79d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(200, result.Status);
        var write = Assert.Single(ds.RecordsWritten);
        // Persisted the STORED high-water mark (80), never the client's lower 79.
        Assert.Equal(80d, write.Payload["totalLevel"]);
    }

    [Fact]
    public async Task TryExecute_AntiRollbackHeal_OnlyRunsLive_NotShadow()
    {
        // Shadow mode (liveServe=false) must stay a faithful parity executor: no
        // heal, so the guard blocks exactly as Bun would. The heal is a live-serve
        // recovery policy, not part of the deterministic engine.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 79d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: false);

        Assert.NotNull(result);
        Assert.Equal(409, result.Status);
    }

    [Fact]
    public async Task TryExecute_AntiRollbackHeal_DoesNotClampSpendableGold()
    {
        // CRITICAL SAFETY: spendable currency (totalGold) is NOT a monotonic guard
        // field, so the heal must NEVER clamp it upward. A player who spent gold
        // (100 -> 50) keeps the lower balance; only the guarded totalLevel is healed.
        var saveBoth = new Dictionary<string, object?>
        {
            ["id"] = "save", ["type"] = "write", ["collection"] = "players", ["key"] = "{{steamId}}",
            ["ops"] = new List<object?>
            {
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = "totalLevel", ["value"] = "{{input.totalLevel}}" },
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = "totalGold", ["value"] = "{{input.totalGold}}" },
            },
        };
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    saveBoth,
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d, ["totalGold"] = 100d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 79d, ["totalGold"] = 50d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(80d, write.Payload["totalLevel"]); // healed up
        Assert.Equal(50d, write.Payload["totalGold"]);  // spendable: stays lowered
    }

    [Fact]
    public async Task TryExecute_StaleTimestampGuard_NotHealed_StillBlocks()
    {
        // A STALE_SAVE-style guard compares a non-monotonic timestamp
        // (input.savedAt >= existing.savedAt). savedAt is NOT a monotonic guard
        // field, so the heal must leave it alone and the stale save is still rejected.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_fresh", "savedAt", "STALE_SAVE"),
                    MakeWriteStep("save", "players", "{{steamId}}", "savedAt", "{{input.savedAt}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["savedAt"] = 200d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["savedAt"] = 100d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.Equal(409, result.Status);
        Assert.Empty(ds.RecordsWritten);
    }

    [Fact]
    public async Task TryExecute_AntiRollbackHigherInput_WritesClientValue_NoHeal()
    {
        // A legitimate increase (input 90 > stored 80) is untouched: the client's
        // value is persisted, the heal never interferes.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 90d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(90d, write.Payload["totalLevel"]);
    }

    [Fact]
    public async Task TryExecute_AntiRollback_MultipleGuards_AllHealed()
    {
        // The real save-all guards totalLevel, totalKills, AND nodesMined. A drifted
        // cache (81/39/50) above the client's recompute (9/11/5) must heal all three
        // and persist the stored high-water marks.
        var saveAll = new Dictionary<string, object?>
        {
            ["id"] = "save", ["type"] = "write", ["collection"] = "players", ["key"] = "{{steamId}}",
            ["ops"] = new List<object?>
            {
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = "totalLevel", ["value"] = "{{input.totalLevel}}" },
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = "totalKills", ["value"] = "{{input.totalKills}}" },
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = "nodesMined", ["value"] = "{{input.nodesMined}}" },
            },
        };
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeRegressionGuard("guard_kills", "totalKills"),
                    MakeRegressionGuard("guard_nodes", "nodesMined"),
                    saveAll,
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?>
            {
                ["totalLevel"] = 81d, ["totalKills"] = 39d, ["nodesMined"] = 50d,
            },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 9d, ["totalKills"] = 11d, ["nodesMined"] = 5d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(81d, write.Payload["totalLevel"]);
        Assert.Equal(39d, write.Payload["totalKills"]);
        Assert.Equal(50d, write.Payload["nodesMined"]);
    }

    [Fact]
    public async Task TryExecute_AntiRollback_FieldOpValueForm_Healed()
    {
        // The guard may be authored in field/op/value form instead of an expression.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    new Dictionary<string, object?>
                    {
                        ["id"] = "guard_level", ["type"] = "assert",
                        ["check"] = new Dictionary<string, object?>
                        {
                            ["field"] = "{{num(input.totalLevel, 0)}}",
                            ["op"] = ">=",
                            ["value"] = "{{num(existing.totalLevel, 0)}}",
                        },
                        ["errorCode"] = "SAVE_REGRESSION_BLOCKED", ["status"] = 409,
                    },
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 79d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(80d, write.Payload["totalLevel"]);
    }

    [Fact]
    public async Task TryExecute_AntiRollback_NewPlayer_NoStoredRecord_SavesNormally()
    {
        // A brand-new player has no stored players record. The guard compares
        // against 0, so the save passes on its own — the heal is a no-op and the
        // client's real starting value (9) is persisted.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>());
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 9d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        var write = Assert.Single(ds.RecordsWritten);
        Assert.Equal(9d, write.Payload["totalLevel"]);
    }

    [Fact]
    public async Task TryExecute_MultiCollectionWrite_PartialFailure_RollsBackAll()
    {
        // Spec: cross-collection-save-consistency — partial write failure applies nothing.
        // A save-all writes players + skills. The skills write (2nd) fails.
        // Assert: the players write is NOT applied (rolled back), and the endpoint
        // returns 500 STORAGE_WRITE_FAILED.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                    MakeWriteStep("save_skills", "skills", "{{steamId}}", "level", "{{input.skillLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
            new() { ["collection_id"] = "col-skills", ["name"] = "skills" },
        };
        // WriteThrowsDataSource throws on ALL writes. We need a data source that
        // succeeds on the first write (players) but fails on the second (skills).
        var ds = new SelectiveThrowDataSource(store, collections, throwOnCollection: "col-skills");
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d, ["skillLevel"] = 50d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal(500, result.Status);
        // The players write was never committed: the transaction was disposed without a commit.
        Assert.Empty(ds.CommittedRecords);
    }

    [Fact]
    public async Task TryExecute_MultiCollectionWrite_Success_LeavesConsistentState()
    {
        // Spec: successful save advances all collections together.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                    MakeWriteStep("save_skills", "skills", "{{steamId}}", "level", "{{input.skillLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
            new() { ["collection_id"] = "col-skills", ["name"] = "skills" },
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>(), collections);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d, ["skillLevel"] = 50d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(2, ds.RecordsWritten.Count);
    }

    [Fact]
    public async Task TryExecute_SaveAll_UpdatesLeaderboardIncrementally()
    {
        // Spec: leaderboard-durability — save-all updates the leaderboard.
        // A save-all that writes the `players` collection must also update
        // leaderboard_global/default.entriesByPlayer.{steamId} incrementally.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save-all"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
            new() { ["collection_id"] = "col-lb", ["name"] = "leaderboard_global" },
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>(), collections);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "save-all",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        // The leaderboard projection fired: one global record write to col-lb/default.
        Assert.NotEmpty(ds.GlobalRecordsWritten);
        var lbWrite = ds.GlobalRecordsWritten.Single();
        Assert.Equal("col-lb", lbWrite.Collection);
        Assert.Equal("default", lbWrite.RecordId);
        // The entriesByPlayer map contains the saving player's entry.
        var payload = lbWrite.Payload;
        Assert.True(payload.TryGetValue("entriesByPlayer", out var ebpObj));
        Assert.NotNull(ebpObj);
    }

    [Fact]
    public async Task TryExecute_HostProxySave_KeysLeaderboardEntryByWrittenPlayer()
    {
        // A host/dedicated server saving another player's record must update that
        // player's leaderboard entry, never the requesting host's.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save-all"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeWriteStep("save_players", "players", "{{input.targetSteamId}}", "playerName", "{{input.playerName}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
            new() { ["collection_id"] = "col-lb", ["name"] = "leaderboard_global" },
        };
        var ds = new RecordingDataSource(store, new Dictionary<string, object?>(), collections);
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "save-all",
            input: new Dictionary<string, object?> { ["targetSteamId"] = "7656222222", ["playerName"] = "Guest" },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: true, CancellationToken.None, liveServe: true);

        Assert.True(result!.Ok);
        var entries = Assert.IsType<Dictionary<string, object?>>(ds.GlobalRecordsWritten.Single().Payload["entriesByPlayer"]);
        var guest = Assert.IsType<Dictionary<string, object?>>(Assert.Contains("7656222222", entries));
        Assert.Equal("Guest", guest["playerName"]);
        Assert.DoesNotContain("7656111111", entries.Keys);
    }

    [Fact]
    public async Task TryExecute_SaveAll_EmitsTrackedFieldDeltas()
    {
        // Spec: network-storage-player-analytics — totalLevel change produces a progression event.
        // A save-all that raises totalLevel from 90 to 100 must emit a tracked_field.totalLevel
        // delta with before=90, after=100, delta=10.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save-all"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    // Pre-read the existing players record (totalLevel=90).
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:col-players:7656111111"] = new Dictionary<string, object?> { ["totalLevel"] = 90d },
        };
        var ds = new RecordingDataSource(store, records, collections);
        var analytics = new FakeAnalyticsService();
        var executor = new EndpointExecutor(ds, webhookSender: null, analyticsService: analytics);

        var result = await executor.TryExecuteAsync(
            "proj", "save-all",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        // A tracked_field.totalLevel event was emitted with the correct delta.
        var tfEvent = analytics.EndpointEvents.FirstOrDefault(e =>
            e.TrackedFieldDeltas?.Any(d => d.Field == "totalLevel") == true);
        Assert.NotNull(tfEvent);
        var delta = tfEvent!.TrackedFieldDeltas!.First(d => d.Field == "totalLevel");
        Assert.Equal(90d, delta.Before);
        Assert.Equal(100d, delta.After);
        Assert.Equal(10d, delta.Delta);
    }

    [Fact]
    public async Task TryExecute_SaveAll_NoChange_EmitsNoSpuriousDelta()
    {
        // Spec: network-storage-player-analytics — no change produces no spurious delta.
        // A save-all that writes the same totalLevel already stored must not emit a delta.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save-all"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:col-players:7656111111"] = new Dictionary<string, object?> { ["totalLevel"] = 100d },
        };
        var ds = new RecordingDataSource(store, records, collections);
        var analytics = new FakeAnalyticsService();
        var executor = new EndpointExecutor(ds, webhookSender: null, analyticsService: analytics);

        await executor.TryExecuteAsync(
            "proj", "save-all",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        // No tracked_field event with a non-zero delta should have been emitted.
        var tfEvents = analytics.EndpointEvents.Where(e =>
            e.TrackedFieldDeltas?.Any(d => d.Field == "totalLevel" && Math.Abs(d.Delta) > double.Epsilon) == true);
        Assert.Empty(tfEvents);
    }

    [Fact]
    public async Task TryExecute_SaveAll_WithProjectionsOff_WritesNoLeaderboardAndEmitsNoTrackedFields()
    {
        // A new project (legacyPlayerProjections off) gets plain endpoint saves: no leaderboard
        // projection into leaderboard_global and no tracked_field analytics from `players`.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:save-all"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeWriteStep("save_players", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var collections = new List<Dictionary<string, object?>>
        {
            new() { ["collection_id"] = "col-players", ["name"] = "players" },
            new() { ["collection_id"] = "col-lb", ["name"] = "leaderboard_global" },
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:col-players:7656111111"] = new Dictionary<string, object?> { ["totalLevel"] = 90d },
        };
        var ds = new RecordingDataSource(store, records, collections) { LegacyPlayerProjections = false };
        var analytics = new FakeAnalyticsService();
        var executor = new EndpointExecutor(ds, webhookSender: null, analyticsService: analytics);

        var result = await executor.TryExecuteAsync(
            "proj", "save-all",
            input: new Dictionary<string, object?> { ["totalLevel"] = 100d },
            steamId: "7656111111", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.True(result!.Ok);
        Assert.Equal(100d, Assert.Single(ds.RecordsWritten).Payload["totalLevel"]);
        Assert.Empty(ds.GlobalRecordsWritten);
        Assert.DoesNotContain(analytics.EndpointEvents, e => e.TrackedFieldDeltas?.Count > 0);
    }

    [Fact]
    public async Task TryExecute_AntiRollback_WithProjectionsOff_IsNotHealed()
    {
        // Without the opt-in the executor never raises a lower client input to the stored value,
        // so the project's own guard rejects the regression.
        var store = new Dictionary<string, object?>
        {
            ["proj:ep:test"] = MakeEndpointDef(
                steps: new List<object?>
                {
                    MakeReadStep("existing", "players", "{{steamId}}"),
                    MakeRegressionGuard("guard_level", "totalLevel"),
                    MakeWriteStep("save", "players", "{{steamId}}", "totalLevel", "{{input.totalLevel}}"),
                },
                body: new Dictionary<string, object?> { ["ok"] = true }),
        };
        var records = new Dictionary<string, object?>
        {
            ["proj:players:steam1"] = new Dictionary<string, object?> { ["totalLevel"] = 80d },
        };
        var ds = new RecordingDataSource(store, records) { LegacyPlayerProjections = false };
        var executor = new EndpointExecutor(ds);

        var result = await executor.TryExecuteAsync(
            "proj", "test",
            input: new Dictionary<string, object?> { ["totalLevel"] = 79d },
            steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: true);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal(409, result.Status);
        Assert.Empty(ds.RecordsWritten);
    }

    // ── Helpers ──

    private static Dictionary<string, object?> MakeEndpointDef(
        List<object?> steps, Dictionary<string, object?> body, int status = 200)
    {
        return new Dictionary<string, object?>
        {
            ["steps"] = steps,
            ["response"] = new Dictionary<string, object?>
            {
                ["status"] = (double)status,
                ["body"] = body,
            },
        };
    }

    private static Dictionary<string, object?> MakeReadStep(string id, string collection, string key)
        => new() { ["id"] = id, ["type"] = "read", ["collection"] = collection, ["key"] = key };

    // An anti-rollback assert guard: `input.{field} >= existing.{field}` → 409.
    private static Dictionary<string, object?> MakeRegressionGuard(
        string id, string field, string errorCode = "SAVE_REGRESSION_BLOCKED")
        => new()
        {
            ["id"] = id, ["type"] = "assert",
            ["check"] = new Dictionary<string, object?>
            {
                ["expression"] = $"{{{{num(input.{field}, 0)}}}} >= {{{{num(existing.{field}, 0)}}}}",
            },
            ["errorCode"] = errorCode,
            ["message"] = $"Refusing to overwrite {field} {{{{num(existing.{field}, 0)}}}} with lower value {{{{num(input.{field}, 0)}}}}.",
            ["status"] = 409,
        };

    private static Dictionary<string, object?> MakeConditionStep(string id, string field, string op, string value)
        => new()
        {
            ["id"] = id, ["type"] = "condition",
            ["check"] = new Dictionary<string, object?> { ["field"] = field, ["op"] = op, ["value"] = value },
        };

    private static Dictionary<string, object?> MakeTransformStep(string id, string expression)
        => new() { ["id"] = id, ["type"] = "transform", ["expression"] = expression };

    private static Dictionary<string, object?> MakeWriteStep(string id, string collection, string key, string path, object value)
        => new()
        {
            ["id"] = id, ["type"] = "write", ["collection"] = collection, ["key"] = key,
            ["ops"] = new List<object?>
            {
                new Dictionary<string, object?> { ["op"] = "set", ["path"] = path, ["value"] = value },
            },
        };

    private static Dictionary<string, object?> MakeDeleteStep(string id, string collection, string key)
        => new() { ["id"] = id, ["type"] = "delete", ["collection"] = collection, ["key"] = key };

    private static Dictionary<string, object?> MakeWebhookStep(string id)
        => new()
        {
            ["id"] = id, ["type"] = "webhook",
            ["url"] = "https://discord.com/api/webhooks/123/abc",
            ["title"] = "Test", ["description"] = "Native serve webhook",
        };

    // ── Fake data sources ──

    private sealed class FakeDataSource : IEndpointDataSource
    {
        private readonly Dictionary<string, object?> _definitions;
        private readonly Dictionary<string, object?> _records;

        public FakeDataSource(Dictionary<string, object?> definitions, Dictionary<string, object?> records)
        {
            _definitions = definitions;
            _records = records;
        }

        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
        {
            var key = $"{projectId}:ep:{endpointSlug}";
            return Task.FromResult(_definitions.TryGetValue(key, out var def) ? def as Dictionary<string, object?> : null);
        }

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
        {
            var ck = $"{projectId}:{collectionId}:{key}";
            return Task.FromResult(_records.TryGetValue(ck, out var record) ? record : null);
        }

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        {
            // Return records whose key starts with the collection prefix
            var prefix = $"{projectId}:{collectionId}:";
            var results = _records.Where(kv => kv.Key.StartsWith(prefix)).Select(kv => kv.Value).ToList();
            return Task.FromResult<IReadOnlyList<object?>>(results);
        }

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => Task.FromResult<IEndpointWriteTransaction>(new DelegateEndpointWriteTransaction(
                write: (projectId, collectionId, key, payload) => _records[$"{projectId}:{collectionId}:{key}"] = new Dictionary<string, object?>(payload),
                delete: (projectId, collectionId, key) => _records[$"{projectId}:{collectionId}:{key}"] = null));

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        /// <summary>Existing-project behavior by default; tests switch it off to check new projects.</summary>
        public bool LegacyPlayerProjections { get; set; } = true;

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(LegacyPlayerProjections);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ThrowOnDefinitionRead : IEndpointDataSource
    {
        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(true);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => throw new Exception("ScyllaDB unavailable");
    }

    private sealed class RecordingDataSource : IEndpointDataSource
    {
        private readonly Dictionary<string, object?> _definitions;
        private readonly Dictionary<string, object?> _records;
        private readonly IReadOnlyList<Dictionary<string, object?>> _collections;
        public List<(string, string)> RecordsRead { get; } = new();

        public RecordingDataSource(
            Dictionary<string, object?> definitions,
            Dictionary<string, object?> records,
            IReadOnlyList<Dictionary<string, object?>>? collections = null)
        {
            _definitions = definitions;
            _records = records;
            _collections = collections ?? Array.Empty<Dictionary<string, object?>>();
        }

        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
        {
            var key = $"{projectId}:ep:{endpointSlug}";
            return Task.FromResult(_definitions.TryGetValue(key, out var def) ? def as Dictionary<string, object?> : null);
        }

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => Task.FromResult(_collections);

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
        {
            RecordsRead.Add((collectionId, key));
            var ck = $"{projectId}:{collectionId}:{key}";
            return Task.FromResult(_records.TryGetValue(ck, out var record) ? record : null);
        }

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        {
            var prefix = $"{projectId}:{collectionId}:";
            var results = _records.Where(kv => kv.Key.StartsWith(prefix)).Select(kv => kv.Value).ToList();
            return Task.FromResult<IReadOnlyList<object?>>(results);
        }

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public List<(string Collection, string Key, IReadOnlyDictionary<string, object?> Payload)> RecordsWritten { get; } = new();
        public List<(string Collection, string Key)> RecordsDeleted { get; } = new();
        public List<(string Collection, string RecordId, IReadOnlyDictionary<string, object?> Payload)> GlobalRecordsWritten { get; } = new();

        public bool LegacyPlayerProjections { get; set; } = true;


        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => Task.FromResult<IEndpointWriteTransaction>(new DelegateEndpointWriteTransaction(
                write: (projectId, collectionId, key, payload) =>
                {
                    RecordsWritten.Add((collectionId, key, payload));
                    _records[$"{projectId}:{collectionId}:{key}"] = new Dictionary<string, object?>(payload);
                },
                delete: (projectId, collectionId, key) =>
                {
                    RecordsDeleted.Add((collectionId, key));
                    _records[$"{projectId}:{collectionId}:{key}"] = null;
                }));

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
        {
            var key = $"{projectId}:{collectionId}:{recordId}";
            return Task.FromResult(GlobalRecordsStore.TryGetValue(key, out var record) ? record : null);
        }

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(LegacyPlayerProjections);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        {
            GlobalRecordsWritten.Add((collectionId, recordId, payload));
            var key = $"{projectId}:{collectionId}:{recordId}";
            GlobalRecordsStore[key] = new Dictionary<string, object?>(payload);
            return Task.CompletedTask;
        }

        public Dictionary<string, object?> GlobalRecordsStore { get; } = new();
    }

    private sealed class WriteThrowsDataSource : IEndpointDataSource
    {
        private readonly Dictionary<string, object?> _definitions;
        public WriteThrowsDataSource(Dictionary<string, object?> definitions) => _definitions = definitions;

        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
            => Task.FromResult(_definitions.TryGetValue($"{projectId}:ep:{endpointSlug}", out var def) ? def as Dictionary<string, object?> : null);

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<object?>>(Array.Empty<object?>());

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => Task.FromResult<IEndpointWriteTransaction>(new DelegateEndpointWriteTransaction(
                write: (_, _, _, _) => throw new Exception("ScyllaDB write failed"),
                delete: (_, _, _) => throw new Exception("ScyllaDB delete failed")));

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(true);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => throw new Exception("ScyllaDB global write failed");
    }

    /// <summary>
    /// A data source that succeeds on writes to all collections EXCEPT the one
    /// specified by <paramref name="throwOnCollection"/>. Used to test atomic
    /// multi-collection rollback: the first write succeeds, the second throws,
    /// and the first must be rolled back.
    /// Tracks written records so the test can assert what was (or wasn't) applied.
    /// </summary>
    private sealed class SelectiveThrowDataSource : IEndpointDataSource
    {
        private readonly Dictionary<string, object?> _definitions;
        private readonly IReadOnlyList<Dictionary<string, object?>> _collections;
        private readonly string _throwOnCollection;

        /// <summary>Records that were committed. Writes of a transaction that is not committed never land here.</summary>
        public Dictionary<string, IReadOnlyDictionary<string, object?>> CommittedRecords { get; } = new();

        public SelectiveThrowDataSource(
            Dictionary<string, object?> definitions,
            IReadOnlyList<Dictionary<string, object?>> collections,
            string throwOnCollection)
        {
            _definitions = definitions;
            _collections = collections;
            _throwOnCollection = throwOnCollection;
        }

        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
            => Task.FromResult(_definitions.TryGetValue($"{projectId}:ep:{endpointSlug}", out var def) ? def as Dictionary<string, object?> : null);

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => Task.FromResult(_collections);

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<object?>>(Array.Empty<object?>());

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
        {
            var staged = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            return Task.FromResult<IEndpointWriteTransaction>(new DelegateEndpointWriteTransaction(
                write: (_, collectionId, key, payload) =>
                {
                    if (collectionId == _throwOnCollection)
                        throw new Exception($"ScyllaDB write failed for {collectionId}");
                    staged[$"{collectionId}:{key}"] = payload;
                },
                delete: (_, collectionId, key) => staged.Remove($"{collectionId}:{key}"),
                commit: () =>
                {
                    foreach (var (key, payload) in staged) CommittedRecords[key] = payload;
                }));
        }

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(true);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeWebhookSender : IEndpointWebhookSender
    {
        public List<(string Url, IReadOnlyDictionary<string, object?> Payload)> Sent { get; } = new();
        public (bool Ok, int Status, string? Error) Result { get; set; } = (true, 204, null);

        public Task<(bool Ok, int Status, string? Error)> SendAsync(string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        {
            Sent.Add((webhookUrl, payload));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAnalyticsService : IPlayerAnalyticsService
    {
        public List<EndpointEventRecord> EndpointEvents { get; } = new();

        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RecordEndpointEventAsync(
            string projectId, string steamId, string endpointSlug,
            string eventType, IReadOnlyDictionary<string, object>? payload,
            IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
            CancellationToken cancellationToken)
        {
            EndpointEvents.Add(new EndpointEventRecord(
                projectId, steamId, endpointSlug, eventType, payload, trackedFieldDeltas));
            return Task.CompletedTask;
        }
    }

    private sealed record EndpointEventRecord(
        string ProjectId, string SteamId, string EndpointSlug, string EventType,
        IReadOnlyDictionary<string, object>? Payload,
        IReadOnlyList<TrackedFieldDelta>? TrackedFieldDeltas);
}

