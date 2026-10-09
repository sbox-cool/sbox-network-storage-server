using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>SQLite-only behavior: per-connection pragmas and the data-plane indexes.</summary>
public sealed class SqliteStoreTuningTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _path = TestDatabases.NewSqlitePath();
    private SqliteNetworkStorageStore? _store;

    private SqliteNetworkStorageStore Store => _store ??= TestDatabases.CreateSqlite(_path, null);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        TestDatabases.DeleteSqlite(_path);
    }

    [Fact]
    public async Task A_new_connection_runs_in_wal_mode_with_normal_synchronous()
    {
        // synchronous is per connection: 0 = OFF, 1 = NORMAL, 2 = FULL (the SQLite default).
        Assert.Equal("1", await Store.ReadPragmaAsync("synchronous", Ct));
        Assert.Equal("wal", await Store.ReadPragmaAsync("journal_mode", Ct));
    }

    [Fact]
    public async Task Every_connection_gets_the_pragmas_not_only_the_first()
    {
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.ReadPragmaAsync("synchronous", Ct)));
        Assert.All(values, value => Assert.Equal("1", value));
    }

    [Fact]
    public async Task Endpoint_slug_and_api_key_identifier_lookups_use_the_new_indexes()
    {
        await Store.MigrateAsync(Ct);
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();

        Assert.Contains("ix_endpoints_project_slug", await PlanAsync(connection,
            "SELECT endpoint_id FROM endpoints WHERE project_id = 'p1' AND slug = 'save'"));
        Assert.Contains("ix_api_keys_project_key_identifier", await PlanAsync(connection,
            "SELECT api_key FROM api_keys WHERE project_id = 'p1' AND key_identifier = 'abc'"));
    }

    private static async Task<string> PlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return string.Join("; ", plan);
    }

    [Fact]
    public async Task Same_millisecond_request_log_events_are_both_stored_in_the_database()
    {
        await Store.MigrateAsync(Ct);
        await Store.InsertStorageRequestLogAsync("p1", 777, "GET", "/a", 200, 1, null, Ct);
        await Store.InsertStorageRequestLogAsync("p1", 777, "GET", "/b", 200, 1, null, Ct);
        await Store.InsertStorageErrorAsync("p1", 777, "e1", "one", null, null, null, "error", Ct);
        await Store.InsertStorageErrorAsync("p1", 777, "e2", "two", null, null, null, "error", Ct);

        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM storage_request_log WHERE created_at_unix_ms = 777), (SELECT COUNT(*) FROM storage_errors WHERE created_at_unix_ms = 777)";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
    }
}
