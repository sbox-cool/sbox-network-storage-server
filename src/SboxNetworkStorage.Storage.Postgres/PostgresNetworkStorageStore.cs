using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Postgres;

/// <summary>
/// PostgreSQL driver. Every table lives in <see cref="PostgresStoreOptions.Schema"/>
/// (created by <see cref="RelationalNetworkStorageStore.MigrateAsync"/> when
/// missing). Connections come from a pooled <see cref="NpgsqlDataSource"/>, so
/// one instance is safe as a singleton under concurrent requests.
/// </summary>
public sealed class PostgresNetworkStorageStore : RelationalNetworkStorageStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _redactedTarget;

    public PostgresNetworkStorageStore(PostgresStoreOptions options, TimeProvider? time = null, ILogger<PostgresNetworkStorageStore>? logger = null)
        : base(SchemaPrefix(options), options.MaxPayloadBytes, time, logger)
    {
        _schema = options.Schema;
        _redactedTarget = options.RedactedTarget;
        _dataSource = NpgsqlDataSource.Create(options.BuildConnectionString());
    }

    public override string ProviderName => "postgres";

    public override string RedactedTarget => _redactedTarget;

    public override bool IsSerializationConflict(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected };

    /// <summary>Schema holding every table.</summary>
    public string Schema => _schema;

    protected override IReadOnlyList<SchemaMigration> Migrations => PostgresMigrations.All;

    protected override string GameLogAttrsValueSql => "CAST(@attrs_json AS jsonb)";

    // GIN on search_tokens (array containment); tokens are the same whitespace-split values the ingest writes.
    protected override string GameLogTextPredicateSql(int tokenCount) =>
        $"e.search_tokens @> ARRAY[{string.Join(", ", Enumerable.Range(0, tokenCount).Select(static i => $"@tok{i}"))}]::text[]";

    // GIN (jsonb_path_ops) containment, so the index applies: the string form always, the literal form for numbers and booleans.
    protected override string GameLogAttrPredicateSql(int index) =>
        $"(e.attrs_json @> @attr_json{index}::jsonb OR e.attrs_json @> @attr_typed{index}::jsonb)";

    protected override async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct)
        => await _dataSource.OpenConnectionAsync(ct);

    protected override async Task PrepareForMigrationAsync(DbConnection connection, CancellationToken ct)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)";
        AddText(exists, "schema", _schema);
        if (await exists.ExecuteScalarAsync(ct) is true) return;

        Logger.LogInformation("Creating PostgreSQL schema {Schema}", _schema);
        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(_schema)}";
        try
        {
            await create.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DuplicateSchema)
        {
            // A concurrent migrator created it between the check and the CREATE.
        }
    }

    protected override async Task LockForMigrationAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        // Transaction-scoped advisory lock: concurrent migrators of the same schema queue here.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@lock_key))";
        AddText(command, "lock_key", "sbox-ns:migrate:" + _schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Transaction-scoped advisory lock: concurrent grant gates for one owner and purpose run one at a time.</summary>
    protected override async Task LockMediaGrantOwnerAsync(DbConnection connection, DbTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@lock_key))";
        AddText(command, "lock_key", $"sbox-ns:media-grant:{_schema}:{key}");
        await command.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<bool> SchemaVersionTableExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL";
        AddText(command, "table_name", TablePrefix + "schema_version");
        return await command.ExecuteScalarAsync(ct) is true;
    }

    protected override async Task<bool> SchemaVersionHasFloorColumnAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'schema_version' AND column_name = 'min_binary_schema')";
        AddText(command, "schema", _schema);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static string SchemaPrefix(PostgresStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateSchema();
        return QuoteIdentifier(options.Schema) + ".";
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void AddText(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
