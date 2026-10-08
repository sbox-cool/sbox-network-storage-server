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
        Assert.Equal(1, Store.SupportedSchemaVersion);
        Assert.Equal(0, await Store.GetSchemaVersionAsync(Ct));
        var ex = await Assert.ThrowsAsync<SchemaMigrationRequiredException>(() => Store.EnsureSchemaCompatibleAsync(Ct));
        Assert.Equal(0, ex.DatabaseVersion);
        Assert.Equal(1, ex.SupportedVersion);
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
