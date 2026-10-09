using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Sqlite;

/// <summary>
/// SQLite driver: one database file in WAL mode with <c>synchronous=NORMAL</c>, a pooled connection per
/// operation (safe as a singleton under concurrent requests), and a busy
/// timeout so concurrent writers queue instead of failing.
/// </summary>
public sealed class SqliteNetworkStorageStore : RelationalNetworkStorageStore
{
    private readonly string _connectionString;
    private readonly string _openPragmas;
    private readonly string _databasePath;
    private int _walWarned;

    public SqliteNetworkStorageStore(SqliteStoreOptions options, TimeProvider? time = null, ILogger<SqliteNetworkStorageStore>? logger = null)
        : base(string.Empty, (options ?? throw new ArgumentNullException(nameof(options))).MaxPayloadBytes, time, logger)
    {
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            throw new ArgumentException("SQLite database path is required.", nameof(options));
        if (options.BusyTimeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "SQLite busy timeout must not be negative.");

        _databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = Math.Max(30, (options.BusyTimeoutMilliseconds / 1000) + 1),
        }.ToString();
        _openPragmas = string.Create(CultureInfo.InvariantCulture,
            $"PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = {options.BusyTimeoutMilliseconds};");
    }

    public override string ProviderName => "sqlite";

    public override string RedactedTarget => _databasePath;

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath => _databasePath;

    protected override IReadOnlyList<SchemaMigration> Migrations => SqliteMigrations.All;

    protected override async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            // Applied on every open. journal_mode=WAL is persistent in the file but cheap to re-assert;
            // synchronous=NORMAL is per connection and is what makes WAL commits skip the per-commit fsync.
            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                var mode = Convert.ToString(await wal.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)
                    && Interlocked.Exchange(ref _walWarned, 1) == 0)
                    Logger.LogWarning("SQLite database {Path} could not switch to WAL mode (journal_mode={Mode})", _databasePath, mode);
            }
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = _openPragmas;
                await pragma.ExecuteNonQueryAsync(ct);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs <c>PRAGMA &lt;name&gt;</c> on a connection opened the way every operation opens one.</summary>
    internal async Task<string?> ReadPragmaAsync(string name, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    protected override async Task<bool> SchemaVersionTableExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public override ValueTask DisposeAsync()
    {
        // Release pooled handles so the file is no longer held open.
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }
}
