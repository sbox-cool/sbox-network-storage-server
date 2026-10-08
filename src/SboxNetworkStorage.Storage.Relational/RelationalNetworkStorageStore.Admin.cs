using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Storage.Relational;

public abstract partial class RelationalNetworkStorageStore
{
    /// <summary>Forward-only migrations, numbered contiguously from 1.</summary>
    protected abstract IReadOnlyList<SchemaMigration> Migrations { get; }

    /// <summary>True when the <c>schema_version</c> table exists (without creating anything).</summary>
    protected abstract Task<bool> SchemaVersionTableExistsAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Runs before the migration transaction starts (e.g. create the PostgreSQL schema).</summary>
    protected virtual Task PrepareForMigrationAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Serializes concurrent migrators inside the migration transaction.</summary>
    protected virtual Task LockForMigrationAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public int SupportedSchemaVersion => Migrations.Count == 0 ? 0 : Migrations.Max(m => m.Version);

    /// <inheritdoc />
    public async Task<StorePingResult> PingAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT 1", []);
        await command.ExecuteScalarAsync(ct);
        stopwatch.Stop();
        return new StorePingResult(stopwatch.Elapsed, connection.ServerVersion);
    }

    /// <inheritdoc />
    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await SchemaVersionTableExistsAsync(connection, ct)) return 0;
        await using var command = Command(connection, _sql.ReadSchemaVersion, []);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task EnsureSchemaCompatibleAsync(CancellationToken ct)
    {
        var current = await GetSchemaVersionAsync(ct);
        var supported = SupportedSchemaVersion;
        if (current > supported) throw new SchemaVersionTooNewException(current, supported);
        if (current < supported) throw new SchemaMigrationRequiredException(current, supported);
    }

    /// <inheritdoc />
    public async Task<SchemaMigrationResult> MigrateAsync(CancellationToken ct)
    {
        var migrations = Migrations.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"{ProviderName} migrations must be numbered contiguously from 1; found version {migrations[i].Version} at position {i + 1}.");
        }
        var supported = migrations.Count;

        await using var connection = await OpenConnectionAsync(ct);
        await PrepareForMigrationAsync(connection, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockForMigrationAsync(connection, transaction, ct);

        await using (var create = Command(connection, _sql.CreateSchemaVersionTable, [], transaction))
            await create.ExecuteNonQueryAsync(ct);

        int current;
        await using (var read = Command(connection, _sql.ReadSchemaVersion, [], transaction))
            current = Convert.ToInt32(await read.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);

        if (current > supported) throw new SchemaVersionTooNewException(current, supported);

        var applied = new List<int>();
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            Logger.LogInformation("Applying {Provider} schema migration v{Version}: {Description}", ProviderName, migration.Version, migration.Description);
            await using (var apply = Command(connection, migration.BuildSql(TablePrefix), [], transaction))
                await apply.ExecuteNonQueryAsync(ct);
            await using (var record = Command(connection, _sql.InsertSchemaVersion,
                [Int32("version", migration.Version), Int64("applied_at_unix_ms", UnixMs()), Text("description", migration.Description)], transaction))
                await record.ExecuteNonQueryAsync(ct);
            applied.Add(migration.Version);
        }

        await transaction.CommitAsync(ct);
        if (applied.Count == 0)
            Logger.LogInformation("{Provider} schema already at version {Version}", ProviderName, current);
        else
            Logger.LogInformation("{Provider} schema migrated from version {From} to {To}", ProviderName, current, supported);
        return new SchemaMigrationResult(current, Math.Max(current, supported), applied);
    }
}
