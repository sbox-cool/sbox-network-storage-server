using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>
/// Search results for the same rows and filters must be identical on every relational driver. Each expectation below
/// is asserted by both the SQLite and the PostgreSQL subclass, so both providers return exactly the same entry ids.
/// </summary>
public abstract class GameLogStoreTests : IAsyncLifetime
{
    private const string Project = "proj_example";
    private const string Player = "76561198000000001";
    private const string Other = "76561198000000002";

    private RelationalNetworkStorageStore? _store;

    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();

    public async Task InitializeAsync()
    {
        _store = Create();
        await _store.MigrateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        await Cleanup();
    }

    private IGameLogStore Logs => (IGameLogStore)_store!;

    private static GameLogRow Row(string id, long ts, string stream, string? actor, string? target, string message, string attrsJson)
        => new(Project, id, ts, stream, "info", "game.event", actor, target, null, null, null, message, attrsJson, null,
            string.Join(' ', message.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>Seeds five rows. Expected results below are derived from this table alone.</summary>
    private async Task SeedAsync()
    {
        await Logs.InsertGameLogsAsync(
        [
            Row("a", 1000, "default", Player, null, "spawned at bridge", """{"team":"red"}"""),
            Row("b", 2000, "default", Player, null, "spawned at bridge", """{"team":"blue"}"""),
            Row("c", 3000, "default", Other, Player, "captured flag", """{"team":"red"}"""),
            Row("d", 4000, "default", Player, null, "captured flag", """{"team":"red","score":10}"""),
            Row("e", 5000, "chat", Player, null, "captured flag", """{"team":"red"}"""),
        ], CancellationToken.None);
    }

    private static GameLogQuery Query(
        IReadOnlyList<string>? streams = null, long? from = null, long? to = null, string? player = null,
        IReadOnlyList<GameLogAttrFilter>? attrs = null, IReadOnlyList<string>? tokens = null, GameLogCursor? cursor = null, int limit = 100)
        => new(Project, streams ?? ["default"], from, to, null, null, player, attrs ?? [], tokens ?? [], cursor, limit);

    private async Task<string[]> SearchIdsAsync(GameLogQuery query)
        => (await Logs.SearchGameLogsAsync(query, CancellationToken.None)).Select(r => r.EntryId).ToArray();

    [SkippableFact]
    public async Task Player_time_attr_and_text_filters_return_the_expected_rows()
    {
        await SeedAsync();

        // Player matches actor or target; chat is excluded because only the default stream is searched.
        Assert.Equal(["d", "c"], await SearchIdsAsync(Query(player: Player, attrs: [new("team", "red")], tokens: ["captured"])));
        // Time window is [from, to): a at 1000 is before 1500, c at 3000 is after the window end of 2500.
        Assert.Equal(["b"], await SearchIdsAsync(Query(player: Player, from: 1500, to: 2500, tokens: ["spawned"])));
        // Numeric attribute compares by canonical text.
        Assert.Equal(["d"], await SearchIdsAsync(Query(attrs: [new("score", "10")])));
        // Text on the wrong value does not match.
        Assert.Empty(await SearchIdsAsync(Query(attrs: [new("team", "blue")], tokens: ["captured"])));
        // Multiple streams are searched together and the result is newest first.
        Assert.Equal(["e", "d", "c"], await SearchIdsAsync(Query(streams: ["default", "chat"], attrs: [new("team", "red")], tokens: ["captured"])));
        // Free text is whole-token: a partial word does not match.
        Assert.Empty(await SearchIdsAsync(Query(tokens: ["capture"])));
    }

    [SkippableFact]
    public async Task Cursor_pages_through_results_without_gaps_or_repeats()
    {
        await SeedAsync();
        var first = await Logs.SearchGameLogsAsync(Query(streams: ["default", "chat"], limit: 2), CancellationToken.None);
        Assert.Equal(["e", "d"], first.Select(r => r.EntryId));
        var last = first[^1];
        var second = await SearchIdsAsync(Query(streams: ["default", "chat"], limit: 2, cursor: new GameLogCursor(last.TsUnixMs, last.EntryId)));
        Assert.Equal(["c", "b"], second);
    }

    [SkippableFact]
    public async Task Purge_removes_only_the_named_stream_older_than_the_cutoff()
    {
        await SeedAsync();
        Assert.Equal(3, await Logs.PurgeGameLogsAsync(Project, "default", 3500, CancellationToken.None));
        Assert.Equal(["d"], await SearchIdsAsync(Query(streams: ["default"])));
        Assert.Equal(["e"], await SearchIdsAsync(Query(streams: ["chat"])));

        var counts = await Logs.CountGameLogStreamsAsync(CancellationToken.None);
        Assert.Contains(counts, c => c.ProjectId == Project && c.Stream == "default" && c.Entries == 1);
        Assert.Contains(counts, c => c.ProjectId == Project && c.Stream == "chat" && c.Entries == 1);
    }

    [SkippableFact]
    public async Task Other_projects_are_never_returned()
    {
        await SeedAsync();
        await Logs.InsertGameLogsAsync([Row("x", 9000, "default", Player, null, "captured flag", "{}") with { ProjectId = "proj_other" }], CancellationToken.None);
        Assert.DoesNotContain("x", await SearchIdsAsync(Query(tokens: ["captured"])));
    }
}

public sealed class SqliteGameLogStoreTests : GameLogStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}

public sealed class PostgresGameLogStoreTests : GameLogStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
