using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>
/// ADO.NET implementation of <see cref="INetworkStorageStore"/> shared by the
/// SQLite and PostgreSQL drivers. Behavior mirrors the production the store
/// store: identical validation, row shapes, clustering order, overwrite
/// semantics, and counter arithmetic. Ordinary writes remain unconditional;
/// portable project restoration additionally exposes a single atomic transaction.
/// </summary>
public abstract partial class RelationalNetworkStorageStore : INetworkStorageStore, IProjectImportStore, INetworkStorageStoreAdmin, IAsyncDisposable, IDisposable
{
    /// <summary><c>query_run_logs</c> rows expire after 90 days (production <c>default_time_to_live = 7776000</c>).</summary>
    public static readonly TimeSpan QueryRunLogRetention = TimeSpan.FromSeconds(7_776_000);

    private readonly StoreSql _sql;
    private readonly int _maxPayloadBytes;
    private readonly TimeProvider _time;

    // Set only on the bound view returned by Bind; null on the shared singleton store.
    private DbTransaction? _transaction;

    protected RelationalNetworkStorageStore(string tablePrefix, int maxPayloadBytes, TimeProvider? time, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(tablePrefix);
        if (maxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), "Maximum payload size must be positive.");
        _sql = new StoreSql(tablePrefix);
        TablePrefix = tablePrefix;
        _maxPayloadBytes = maxPayloadBytes;
        _time = time ?? TimeProvider.System;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public int MaxPayloadBytes => _maxPayloadBytes;

    /// <summary>Prefix placed before every table name (empty, or a quoted schema followed by a dot).</summary>
    protected string TablePrefix { get; }

    protected ILogger Logger { get; }

    /// <inheritdoc />
    public abstract string ProviderName { get; }

    /// <inheritdoc />
    public abstract string RedactedTarget { get; }

    /// <summary>Opens a pooled connection to the database.</summary>
    protected abstract ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct);

    public abstract ValueTask DisposeAsync();

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private long UnixMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private string Serialize(JsonElement element, string resourceType)
        => StoreValidation.Serialize(element, resourceType, _maxPayloadBytes);

    public async Task<bool> TryImportProjectAsync(string projectId,
        Func<INetworkStorageStore, CancellationToken, Task> restore, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        ArgumentNullException.ThrowIfNull(restore);
        if (_transaction is not null)
            throw new InvalidOperationException("Project imports cannot run inside a transaction.");

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var claim = Command(connection, _sql.ClaimProject,
            [Text("project_id", projectId), Int64("updated_at_unix_ms", UnixMs())], transaction);
        if (await claim.ExecuteNonQueryAsync(ct) != 1)
            return false;

        // Disposal rolls back an uncommitted transaction even when the caller's token is canceled.
        await restore(Bind(transaction), ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<IStoreTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("Transactions cannot be nested.");

        var connection = await OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            return new StoreTransaction(Bind(transaction), connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>A shallow copy of this store whose every statement runs on <paramref name="transaction"/>.</summary>
    private RelationalNetworkStorageStore Bind(DbTransaction transaction)
    {
        var view = (RelationalNetworkStorageStore)MemberwiseClone();
        view._transaction = transaction;
        return view;
    }

    private sealed class StoreTransaction(RelationalNetworkStorageStore view, DbConnection connection, DbTransaction transaction) : IStoreTransaction
    {
        public INetworkStorageStore Store => view;

        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            // Disposing an uncommitted transaction rolls it back.
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// A connection for one operation: the transaction's connection on a bound view (not owned, never disposed
    /// here), otherwise a fresh pooled connection that is returned to the pool on dispose.
    /// </summary>
    private readonly struct Lease(DbConnection connection, DbTransaction? transaction, DbConnection? owned) : IAsyncDisposable
    {
        public DbConnection Connection => connection;
        public DbTransaction? Transaction => transaction;
        public ValueTask DisposeAsync() => owned is null ? ValueTask.CompletedTask : owned.DisposeAsync();
    }

    private async ValueTask<Lease> LeaseAsync(CancellationToken ct)
    {
        if (_transaction is { } transaction) return new Lease(transaction.Connection!, transaction, null);
        var owned = await OpenConnectionAsync(ct);
        return new Lease(owned, null, owned);
    }

    // ── command helpers ────────────────────────────────────────────────

    /// <summary>A typed command parameter; <see cref="Name"/> matches the <c>@name</c> placeholder.</summary>
    private readonly record struct Arg(string Name, object? Value, DbType Type);

    private static Arg Text(string name, string? value) => new(name, value, DbType.String);
    private static Arg Int64(string name, long? value) => new(name, value, DbType.Int64);
    private static Arg Int32(string name, int? value) => new(name, value, DbType.Int32);
    private static Arg Bool(string name, bool? value) => new(name, value, DbType.Boolean);

    private static DbCommand Command(DbConnection connection, string sql, Arg[] args, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var arg in args)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + arg.Name;
            parameter.DbType = arg.Type;
            parameter.Value = arg.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static DbCommand Command(Lease lease, string sql, Arg[] args) => Command(lease.Connection, sql, args, lease.Transaction);

    private async Task ExecuteAsync(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Runs on a connection obtained from <see cref="LeaseAsync"/>, joining the transaction when it is the transaction's connection.</summary>
    private async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct, params Arg[] args)
    {
        await using var command = Command(connection, sql, args,
            ReferenceEquals(_transaction?.Connection, connection) ? _transaction : null);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<JsonElement?> QuerySingleAsync(string sql, Column[] columns, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        return await reader.ReadAsync(ct) ? RowJson.Read(reader, columns) : null;
    }

    private async Task<IReadOnlyList<JsonElement>> QueryListAsync(string sql, Column[] columns, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<JsonElement>();
        while (await reader.ReadAsync(ct))
            rows.Add(RowJson.Read(reader, columns));
        return rows;
    }

    /// <summary>Reads the first column of the first row as UTF-8 bytes; <c>found</c> is false when no row exists.</summary>
    private async Task<(bool Found, byte[]? Value)> QueryUtf8Async(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return (false, null);
        return (true, reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0));
    }

    private async Task<long> QueryInt64ScalarAsync(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the first column of the first row as text; <c>found</c> is false when no row exists.</summary>
    private async Task<(bool Found, string? Value)> QueryTextAsync(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return (false, null);
        return (true, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    /// <summary>Executes a statement and returns the number of rows it changed.</summary>
    private async Task<int> ExecuteCountAsync(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
