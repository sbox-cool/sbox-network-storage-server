using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class ClientVersionStoreTests : IAsyncLifetime
{
    private RelationalNetworkStorageStore? _store;
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();
    public async Task InitializeAsync() { _store = Create(); await _store.MigrateAsync(CancellationToken.None); }
    public async Task DisposeAsync() { if (_store is not null) await _store.DisposeAsync(); await Cleanup(); }
    [SkippableFact]
    public async Task Flushes_accumulate_requests_merge_players_and_prune_only_expired_days()
    {
        var store = (IClientVersionStore)_store!;
        var key = new ClientVersionKey("proj_example", 1000, "1.1.0", 42, "game", 11);
        var sketch = new byte[PlayerCountSketch.Size];
        PlayerCountSketch.Add(sketch, key.ProjectId, "76561198000000001");
        await store.WriteClientVersionsAsync([new(key, 10000, 10, sketch)], CancellationToken.None);
        await store.WriteClientVersionsAsync([new(key, 2, 20, sketch), new(key with { Day = 999 }, 1, 15, sketch)], CancellationToken.None);
        var recent = Assert.Single(await store.ReadClientVersionsAsync(key.ProjectId, 1000, CancellationToken.None));
        Assert.Equal(10002, recent.Requests);
        Assert.Equal(20, recent.LastSeenUnixMs);
        Assert.Equal(1, recent.EstimatedPlayers);
        await store.PurgeClientVersionsAsync(1000, CancellationToken.None);
        Assert.Equal(recent.Requests, Assert.Single(await store.ReadClientVersionsAsync(key.ProjectId, 0, CancellationToken.None)).Requests);
    }

    [SkippableFact]
    public async Task Reads_are_bounded_by_the_documented_limit_with_a_probe_row()
    {
        var store = (IClientVersionStore)_store!;
        var sketch = new byte[PlayerCountSketch.Size];
        var batch = Enumerable.Range(0, RelationalNetworkStorageStore.ReadLimit + 100)
            .Select(i => new ClientVersionDelta(new ClientVersionKey("proj_example", 1000, "1.1.0", null, "game", i), 1, 1_700_000_000_000, sketch))
            .ToArray();
        await store.WriteClientVersionsAsync(batch, CancellationToken.None);
        var rows = await store.ReadClientVersionsAsync("proj_example", 0, CancellationToken.None);
        // One row past the limit: the report layer tells a truncated tail from a complete one.
        Assert.Equal(RelationalNetworkStorageStore.ReadLimit + 1, rows.Count);
    }

    [SkippableFact]
    public async Task Writes_above_the_chunk_size_persist_across_transactions()
    {
        var store = (IClientVersionStore)_store!;
        var sketch = new byte[PlayerCountSketch.Size];
        PlayerCountSketch.Add(sketch, "proj_example", "76561198000000001");
        var batch = Enumerable.Range(0, RelationalNetworkStorageStore.WriteChunkGroups * 2 + 50)
            .Select(i => new ClientVersionDelta(new ClientVersionKey("proj_example", 1000, "1.1.0", null, "game", i), 2, 1_700_000_000_000, sketch))
            .ToArray();
        await store.WriteClientVersionsAsync(batch, CancellationToken.None);
        var rows = await store.ReadClientVersionsAsync("proj_example", 0, CancellationToken.None);
        Assert.Equal(batch.Length, rows.Count);
        Assert.Equal(2L * batch.Length, rows.Sum(row => row.Requests));
        Assert.All(rows, row => Assert.Equal(1, row.EstimatedPlayers));
    }

    [SkippableFact]
    public async Task Outcome_deltas_add_up_per_hour_and_totals_sum_across_projects()
    {
        var store = (IClientVersionStore)_store!;
        var early = new ClientOutcomeKey("proj_a", 500_000, "1.0.0", "1.1.0", 42, "game");
        var late = early with { Hour = 500_010 };
        await store.WriteClientOutcomesAsync([new(early, 10, 1, 2), new(late, 5, 0, 1), new(early with { ProjectId = "proj_b" }, 7, 3, 0)], CancellationToken.None);
        // A second flush for the same hour and project adds to the existing bucket rather than replacing it.
        await store.WriteClientOutcomesAsync([new(early, 4, 0, 0)], CancellationToken.None);
        var row = Assert.Single(await store.ReadClientOutcomeTotalsAsync(500_000, CancellationToken.None));
        Assert.Equal("1.0.0", row.ServerVersion);
        Assert.Equal("1.1.0", row.LibraryVersion);
        Assert.Equal(10 + 4 + 5 + 7, row.Requests);
        Assert.Equal(1 + 3, row.Rejected);
        Assert.Equal(2 + 1, row.ServerErrors);
        Assert.Equal(500_010, row.LastHour);
        Assert.Empty(await store.ReadClientOutcomeTotalsAsync(500_011, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Purge_removes_only_hours_before_the_cutoff()
    {
        var store = (IClientVersionStore)_store!;
        var early = new ClientOutcomeKey("proj_a", 500_000, "1.0.0", "1.1.0", 42, "game");
        await store.WriteClientOutcomesAsync([new(early, 10, 1, 2), new(early with { Hour = 500_010 }, 5, 0, 1)], CancellationToken.None);
        await store.PurgeClientOutcomesAsync(500_005, CancellationToken.None);
        var remaining = Assert.Single(await store.ReadClientOutcomeTotalsAsync(0, CancellationToken.None));
        Assert.Equal(5, remaining.Requests);
        Assert.Equal(1, remaining.ServerErrors);
    }

    [SkippableFact]
    public async Task Observed_libraries_group_the_daily_census_by_version_and_revision()
    {
        var store = (IClientVersionStore)_store!;
        var sketch = new byte[PlayerCountSketch.Size];
        await store.WriteClientVersionsAsync([new(new ClientVersionKey("proj_example", 1000, "1.1.0", 42, "game", 11), 10, 1, sketch)], CancellationToken.None);
        var observed = await store.ReadObservedLibrariesAsync(1000, CancellationToken.None);
        Assert.Contains(observed, library => library.LibraryVersion == "1.1.0" && library.LibraryRevision == 42 && library.Requests == 10);
        Assert.Empty(await store.ReadObservedLibrariesAsync(1001, CancellationToken.None));
    }
}

public sealed class SqliteClientVersionStoreTests : ClientVersionStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}
public sealed class PostgresClientVersionStoreTests : ClientVersionStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
