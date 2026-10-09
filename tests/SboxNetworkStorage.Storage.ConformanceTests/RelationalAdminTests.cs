using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>Migration, version guard, ping and DI behavior shared by the relational drivers.</summary>
public abstract class RelationalAdminTests : IAsyncLifetime
{
    protected static readonly CancellationToken Ct = CancellationToken.None;

    private RelationalNetworkStorageStore? _store;

    protected abstract RelationalNetworkStorageStore CreateStore();

    /// <summary>Writes a schema_version row directly, simulating a newer binary having migrated the database.</summary>
    protected abstract Task InsertSchemaVersionRowAsync(int version);

    protected abstract Task CleanupAsync();

    protected RelationalNetworkStorageStore Store => _store ??= CreateStore();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        await CleanupAsync();
    }

    [SkippableFact]
    public async Task Fresh_database_reports_version_zero_and_requires_migration()
    {
        Assert.True(Store.SupportedSchemaVersion >= 1);
        Assert.Equal(0, await Store.GetSchemaVersionAsync(Ct));
        var ex = await Assert.ThrowsAsync<SchemaMigrationRequiredException>(() => Store.EnsureSchemaCompatibleAsync(Ct));
        Assert.Equal(0, ex.DatabaseVersion);
        Assert.Equal(Store.SupportedSchemaVersion, ex.SupportedVersion);
        Assert.Contains("sbox-ns db migrate", ex.Message);
    }

    [SkippableFact]
    public async Task Migrate_applies_all_versions_once_and_is_idempotent()
    {
        var first = await Store.MigrateAsync(Ct);
        Assert.Equal(0, first.FromVersion);
        Assert.Equal(Store.SupportedSchemaVersion, first.ToVersion);
        Assert.Equal(Enumerable.Range(1, Store.SupportedSchemaVersion), first.AppliedVersions);
        Assert.Equal(Store.SupportedSchemaVersion, await Store.GetSchemaVersionAsync(Ct));
        await Store.EnsureSchemaCompatibleAsync(Ct);

        var second = await Store.MigrateAsync(Ct);
        Assert.Empty(second.AppliedVersions);
        Assert.Equal(Store.SupportedSchemaVersion, second.FromVersion);
    }

    [SkippableFact]
    public async Task Concurrent_migrations_apply_each_version_exactly_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Store.MigrateAsync(Ct))));
        Assert.Equal(Store.SupportedSchemaVersion, results.Sum(r => r.AppliedVersions.Count));
        Assert.Equal(Store.SupportedSchemaVersion, await Store.GetSchemaVersionAsync(Ct));
    }

    [SkippableFact]
    public async Task Newer_database_schema_is_refused_naming_both_versions()
    {
        await Store.MigrateAsync(Ct);
        await InsertSchemaVersionRowAsync(Store.SupportedSchemaVersion + 41);

        var guard = await Assert.ThrowsAsync<SchemaVersionTooNewException>(() => Store.EnsureSchemaCompatibleAsync(Ct));
        Assert.Equal(Store.SupportedSchemaVersion + 41, guard.DatabaseVersion);
        Assert.Equal(Store.SupportedSchemaVersion, guard.SupportedVersion);
        Assert.Contains($"version {Store.SupportedSchemaVersion + 41}", guard.Message);
        Assert.Contains($"version {Store.SupportedSchemaVersion} at most", guard.Message);

        var migrate = await Assert.ThrowsAsync<SchemaVersionTooNewException>(() => Store.MigrateAsync(Ct));
        Assert.Equal(Store.SupportedSchemaVersion + 41, migrate.DatabaseVersion);
    }

    [SkippableFact]
    public async Task Ping_reports_server_version()
    {
        var ping = await Store.PingAsync(Ct);
        Assert.False(string.IsNullOrWhiteSpace(ping.ServerVersion));
        Assert.True(ping.RoundTrip >= TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task Telemetry_usage_counts_owned_projects_distinct_players_and_recent_activity()
    {
        await Store.MigrateAsync(Ct);
        Assert.Equal(new StoreUsageCounts(0, 0, 0), await Store.CountUsageAsync(0, Ct));

        const long now = 1_800_000_000_000;
        const long since = now - 30L * 24 * 60 * 60 * 1000;
        await Store.UpsertProjectMembershipAsync("1", "proj_a", "owner", now, Ct);
        await Store.UpsertProjectMembershipAsync("1", "proj_b", "owner", now, Ct);
        async Task Seen(string project, string steamId, long lastSeen)
            => await Store.UpsertPlayerProfileAsync(project, steamId, "p", false, null, lastSeen, null, null, null, 0, 0, null, null, "{}", lastSeen, Ct);
        await Seen("proj_a", "76561198000000001", now);
        await Seen("proj_b", "76561198000000001", since - 1); // same player in a second project: counted once, active via proj_a
        await Seen("proj_b", "76561198000000002", since);     // boundary is inclusive
        await Seen("proj_b", "76561198000000003", since - 1); // inactive
        await Seen("proj_deleted", "76561198000000004", now); // no membership: not counted

        Assert.Equal(new StoreUsageCounts(2, 3, 2), await Store.CountUsageAsync(since, Ct));
    }
}

public sealed class SqliteAdminTests : RelationalAdminTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();

    protected override RelationalNetworkStorageStore CreateStore() => TestDatabases.CreateSqlite(_path, null);

    protected override async Task InsertSchemaVersionRowAsync(int version)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO schema_version (version, applied_at_unix_ms, description) VALUES ({version}, 0, 'future')";
        await command.ExecuteNonQueryAsync();
    }

    protected override Task CleanupAsync()
    {
        TestDatabases.DeleteSqlite(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Migration_opts_existing_projects_into_player_projections_and_leaves_new_ones_off()
    {
        await Store.MigrateAsync(Ct);
        await RevertToVersion1Async("""
            INSERT INTO projects (project_id, payload_json, version, updated_at_unix_ms) VALUES ('old1', '{"name":"Old"}', 1, 0), ('old2', '{}', 1, 0);
            """);

        var migration = await Store.MigrateAsync(Ct);

        Assert.Equal([2, 3], migration.AppliedVersions);
        var old = (await Store.ReadProjectAsync("old1", Ct))!.Value;
        Assert.True(old.GetProperty("legacyPlayerProjections").GetBoolean());
        Assert.Equal("Old", old.GetProperty("name").GetString());
        Assert.True((await Store.ReadProjectAsync("old2", Ct))!.Value.GetProperty("legacyPlayerProjections").GetBoolean());

        await Store.UpsertProjectAsync("fresh", System.Text.Json.JsonDocument.Parse("""{"name":"New"}""").RootElement.Clone(), 1, Ct);
        Assert.False((await Store.ReadProjectAsync("fresh", Ct))!.Value.TryGetProperty("legacyPlayerProjections", out _));
    }

    /// <summary>
    /// Puts a migrated database back into its version 1 shape (version 3 objects removed, original
    /// WITHOUT ROWID log tables), then runs <paramref name="seed"/> against it.
    /// </summary>
    private async Task RevertToVersion1Async(string seed)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_endpoints_project_slug;
            DROP INDEX ix_api_keys_project_key_identifier;
            DROP TABLE storage_errors;
            DROP TABLE storage_request_log;
            CREATE TABLE storage_errors (project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, error_id TEXT, message TEXT, stack_trace TEXT, source TEXT, request_path TEXT, severity TEXT, PRIMARY KEY (project_id, created_at_unix_ms DESC)) STRICT, WITHOUT ROWID;
            CREATE TABLE storage_request_log (project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, method TEXT, path TEXT, status_code INTEGER, duration_ms INTEGER, api_key_identifier TEXT, PRIMARY KEY (project_id, created_at_unix_ms DESC)) STRICT, WITHOUT ROWID;
            DELETE FROM schema_version WHERE version >= 2;
            """ + seed;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migration_v3_rebuilds_the_log_tables_keeping_existing_rows_and_adds_the_indexes()
    {
        await Store.MigrateAsync(Ct);
        await RevertToVersion1Async("""
            INSERT INTO storage_request_log (project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier) VALUES ('p1', 100, 'GET', '/a', 200, 3, 'pk'), ('p1', 200, 'POST', '/b', 500, 9, NULL), ('p2', 150, 'GET', '/c', 404, 1, NULL);
            INSERT INTO storage_errors (project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity) VALUES ('p1', 100, 'e1', 'boom', 'trace', 'worker', '/a', 'error');
            """);

        var migration = await Store.MigrateAsync(Ct);

        Assert.Equal([2, 3], migration.AppliedVersions);
        var log = await Store.ListStorageRequestLogAsync("p1", 10, Ct);
        Assert.Equal(new[] { "/b", "/a" }, log.Select(r => Json.Str(r, "path")));
        Json.Equal("""{"created_at_unix_ms":100,"method":"GET","path":"/a","status_code":200,"duration_ms":3,"api_key_identifier":"pk"}""", log[1]);
        Assert.Single(await Store.ListStorageRequestLogAsync("p2", 10, Ct));
        Json.Equal("""{"created_at_unix_ms":100,"error_id":"e1","message":"boom","stack_trace":"trace","source":"worker","request_path":"/a","severity":"error"}""", (await Store.ListStorageErrorsAsync("p1", 10, Ct)).Single());

        await Store.InsertStorageRequestLogAsync("p1", 200, "GET", "/d", 200, 1, null, Ct); // same millisecond as a migrated row
        Assert.Equal(3, (await Store.ListStorageRequestLogAsync("p1", 10, Ct)).Count);
    }
    [Fact]
    public async Task Database_uses_wal_journal_and_target_is_the_file_path()
    {
        await Store.MigrateAsync(Ct);
        Assert.Equal("sqlite", Store.ProviderName);
        Assert.Equal(Path.GetFullPath(_path), Store.RedactedTarget);
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Di_registers_one_singleton_for_store_and_admin()
    {
        await using var provider = new ServiceCollection()
            .AddSqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = _path })
            .BuildServiceProvider();
        var store = provider.GetRequiredService<INetworkStorageStore>();
        Assert.IsType<SqliteNetworkStorageStore>(store);
        Assert.Same(store, provider.GetRequiredService<INetworkStorageStoreAdmin>());
        Assert.Same(store, provider.GetRequiredService<SqliteNetworkStorageStore>());
        Assert.Equal(0, await provider.GetRequiredService<INetworkStorageStoreAdmin>().GetSchemaVersionAsync(Ct)); // no auto-migration
    }
}

public sealed class PostgresAdminTests : RelationalAdminTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();

    protected override RelationalNetworkStorageStore CreateStore()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.CreatePostgres(_schema, null);
    }

    protected override Task InsertSchemaVersionRowAsync(int version)
        => TestDatabases.ExecutePostgresAsync($"INSERT INTO \"{_schema}\".schema_version (version, applied_at_unix_ms, description) VALUES ({version}, 0, 'future')");

    protected override Task CleanupAsync()
        => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);

    [SkippableFact]
    public async Task Migrate_creates_the_configured_schema_and_keeps_tables_inside_it()
    {
        await Store.MigrateAsync(Ct);
        Assert.Equal("postgres", Store.ProviderName);
        Assert.EndsWith($"?schema={_schema}", Store.RedactedTarget);
        await using var connection = new Npgsql.NpgsqlConnection(TestDatabases.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @s", connection);
        command.Parameters.AddWithValue("s", _schema);
        Assert.Equal(30L, (long)(await command.ExecuteScalarAsync())!); // 29 data tables + schema_version
    }

    [Fact]
    public void Options_build_connection_string_from_parts_and_password_file_without_leaking_secrets()
    {
        var passwordFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(passwordFile, "s3cret!\n");
            var options = new PostgresStoreOptions
            {
                Host = "db.internal", Port = 6543, Database = "game", Username = "ns user", PasswordFile = passwordFile,
                SslMode = "require", MaxPoolSize = 7, ConnectTimeoutSeconds = 3, Schema = "ns_prod",
            };
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(options.BuildConnectionString());
            Assert.Equal("db.internal", builder.Host);
            Assert.Equal(6543, builder.Port);
            Assert.Equal("game", builder.Database);
            Assert.Equal("ns user", builder.Username);
            Assert.Equal("s3cret!", builder.Password);
            Assert.Equal(Npgsql.SslMode.Require, builder.SslMode);
            Assert.Equal(7, builder.MaxPoolSize);
            Assert.Equal(3, builder.Timeout);

            Assert.Equal("postgres://ns%20user@db.internal:6543/game?schema=ns_prod", options.RedactedTarget);
            Assert.DoesNotContain("s3cret", options.ToString());
            Assert.Equal("postgres://admin@h:5432/d?schema=network_storage",
                new PostgresStoreOptions { ConnectionString = "Host=h;Database=d;Username=admin;Password=pw" }.RedactedTarget);

            Assert.Throws<ArgumentException>(() => (options with { Password = "x" }).BuildConnectionString());
            Assert.Throws<ArgumentException>(() => (options with { SslMode = "sometimes" }).BuildConnectionString());
            Assert.Throws<ArgumentException>(() => (options with { Database = "" }).BuildConnectionString());
            Assert.Throws<ArgumentException>(() => (options with { Schema = "bad-schema" }).ValidateSchema());
        }
        finally
        {
            File.Delete(passwordFile);
        }
    }

    [Fact]
    public void Di_registers_one_singleton_for_store_and_admin()
    {
        using var provider = new ServiceCollection()
            .AddPostgresNetworkStorageStore(new PostgresStoreOptions { Host = "localhost", Database = "x", Username = "y" })
            .BuildServiceProvider();
        var store = provider.GetRequiredService<INetworkStorageStore>();
        Assert.IsType<PostgresNetworkStorageStore>(store);
        Assert.Same(store, provider.GetRequiredService<INetworkStorageStoreAdmin>());
    }
}

