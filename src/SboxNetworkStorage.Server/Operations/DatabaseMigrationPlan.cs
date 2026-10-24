using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Npgsql;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>Reads schema metadata without constructing the server graph or initializing its database.</summary>
internal static class DatabaseMigrationPlan
{
    internal sealed record Plan(string Provider, bool Initialized, SchemaState State,
        int Supported, int ProjectedFloor, IReadOnlyList<SchemaMigration> Pending)
    {
        public bool Compatible => !State.IsTooNewFor(Supported);
        public JsonObject ToJson() => new()
        {
            ["provider"] = Provider,
            ["initialized"] = Initialized,
            ["schemaVersion"] = State.Version,
            ["minBinarySchema"] = State.MinBinarySchema,
            ["supportedSchemaVersion"] = Supported,
            ["projectedMinBinarySchema"] = ProjectedFloor,
            ["compatible"] = Compatible,
            ["raisesRollbackFloor"] = ProjectedFloor > State.MinBinarySchema,
            ["pending"] = new JsonArray(Pending.Select(m => (JsonNode)new JsonObject
            {
                ["version"] = m.Version,
                ["description"] = m.Description,
                ["compat"] = m.Compat == SchemaChange.Additive ? "additive" : "breaking"
            }).ToArray())
        };
    }

    internal static Plan Create(string provider, bool initialized, SchemaState state, IReadOnlyList<SchemaMigration> known)
    {
        var supported = known.Count == 0 ? 0 : known.Max(m => m.Version);
        var pending = known.Where(m => m.Version > state.Version).OrderBy(m => m.Version).ToArray();
        var floor = pending.Where(m => m.Compat != SchemaChange.Additive)
            .Select(m => m.Version).Append(state.MinBinarySchema).Max();
        return new Plan(provider, initialized, state, supported, floor, pending);
    }

    public static async Task<Plan> ReadAsync(EffectiveConfig config, CancellationToken ct)
    {
        var postgres = config.GetString("database.provider") == "postgres";
        IReadOnlyList<SchemaMigration> known = postgres ? PostgresMigrations.All : SqliteMigrations.All;
        if (!postgres && !File.Exists(StoreRegistration.SqlitePath(config)))
            return Create("sqlite", false, new SchemaState(0, 0), known);

        await using DbConnection connection = postgres
            ? new NpgsqlConnection(StoreRegistration.BuildPostgresOptions(config).BuildConnectionString())
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = StoreRegistration.SqlitePath(config), Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
        await connection.OpenAsync(ct);
        await using DbTransaction transaction = connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: true)
            : await connection.BeginTransactionAsync(ct);
        var schema = postgres ? StoreRegistration.BuildPostgresOptions(config).Schema : "";
        if (postgres)
        {
            StoreRegistration.BuildPostgresOptions(config).ValidateSchema();
            await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", ct);
        }
        var table = postgres ? $"\"{schema}\".schema_version" : "schema_version";
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = postgres
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'schema_version'"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'";
        if (postgres) Add(exists, "schema", schema);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0)
            return Create(postgres ? "postgres" : "sqlite", false, new SchemaState(0, 0), known);

        await using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = postgres
            ? "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'schema_version'"
            : "SELECT name FROM pragma_table_info('schema_version')";
        if (postgres) Add(columns, "schema", schema);
        var hasFloor = false;
        await using (var reader = await columns.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) hasFloor |= reader.GetString(0) == "min_binary_schema";
        await using var rows = connection.CreateCommand();
        rows.Transaction = transaction;
        rows.CommandText = $"SELECT version{(hasFloor ? ", min_binary_schema" : "")} FROM {table}";
        var versions = new List<(int Version, int? MinBinarySchema)>();
        await using (var reader = await rows.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                versions.Add((reader.GetInt32(0), hasFloor && !reader.IsDBNull(1) ? reader.GetInt32(1) : null));
        return Create(postgres ? "postgres" : "sqlite", versions.Count > 0, SchemaCompatibility.Resolve(versions, known), known);
    }

    private static void Add(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
