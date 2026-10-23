using System.Data;
using System.Data.Common;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Media object rows and upload grant rows (schema v9).</summary>
public abstract partial class RelationalNetworkStorageStore : IMediaStore
{
    /// <summary>
    /// Atomic grant gate; see <see cref="IMediaStore.TryInsertMediaGrantAsync"/>. Joins the bound transaction when one
    /// is open; otherwise runs its own.
    /// </summary>
    public async Task<bool> TryInsertMediaGrantAsync(MediaGrantRecord grant, long windowStartUnixMs, int perOwnerLimit, CancellationToken ct)
    {
        StoreValidation.Id(grant.ProjectId);
        if (_transaction is { } bound)
            return await InsertMediaGrantWithinAsync(bound.Connection!, bound, grant, windowStartUnixMs, perOwnerLimit, ct);

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var inserted = await InsertMediaGrantWithinAsync(connection, transaction, grant, windowStartUnixMs, perOwnerLimit, ct);
        await transaction.CommitAsync(ct);
        return inserted;
    }

    /// <summary>
    /// Runs the gate inside one transaction. The owner lock comes first (PostgreSQL takes an advisory lock), so the
    /// count below sees every grant committed by a concurrent call. The prune is the first write statement, so SQLite
    /// takes its database write lock here and serialises the same way.
    /// </summary>
    private async Task<bool> InsertMediaGrantWithinAsync(DbConnection connection, DbTransaction transaction, MediaGrantRecord grant,
        long windowStartUnixMs, int perOwnerLimit, CancellationToken ct)
    {
        await LockMediaGrantOwnerAsync(connection, transaction, $"{grant.ProjectId}:{grant.OwnerSteamId}:{grant.Purpose}", ct);

        await using (var prune = Command(connection,
            $"DELETE FROM {TablePrefix}media_grants WHERE issued_at_unix_ms < @windowStart",
            [Int64("windowStart", windowStartUnixMs)], transaction))
        {
            await prune.ExecuteNonQueryAsync(ct);
        }

        await using var insert = Command(connection, $"""
            INSERT INTO {TablePrefix}media_grants (project_id, grant_id, purpose, owner_steam_id, issued_at_unix_ms, expires_at_unix_ms)
            SELECT @project, @grant, @purpose, @owner, @issued, @expires
            WHERE (SELECT COUNT(*) FROM {TablePrefix}media_grants
                   WHERE project_id = @project AND owner_steam_id = @owner AND purpose = @purpose
                     AND issued_at_unix_ms >= @windowStart) < @limit
            """,
            [
                Text("project", grant.ProjectId),
                Text("grant", grant.GrantId),
                Text("purpose", grant.Purpose),
                Text("owner", grant.OwnerSteamId),
                Int64("issued", grant.IssuedAtUnixMs),
                Int64("expires", grant.ExpiresAtUnixMs),
                Int64("windowStart", windowStartUnixMs),
                Int32("limit", perOwnerLimit)
            ], transaction);
        return await insert.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Provider hook that serialises concurrent grant gates for one owner and purpose. The base takes no lock; a
    /// provider with row-snapshot isolation (PostgreSQL) overrides it.
    /// </summary>
    protected virtual Task LockMediaGrantOwnerAsync(DbConnection connection, DbTransaction transaction, string key, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Provider hook: a lock on one (project, sha256) blob key, taken on <paramref name="transaction"/> and released when
    /// it ends. The base takes none. A provider whose lock excludes other server processes overrides this and returns
    /// true from <see cref="MediaBlobLocksSpanProcesses"/>.
    /// </summary>
    protected virtual Task LockMediaBlobKeyAsync(DbConnection connection, DbTransaction transaction, string key, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>True when <see cref="LockMediaBlobKeyAsync"/> excludes other server processes.</summary>
    protected virtual bool MediaBlobLocksSpanProcesses => false;

    public async Task<bool> TryConsumeGrantAsync(string projectId, string grantId, long nowUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        // One conditional UPDATE is the whole check: it matches only an unused, unexpired grant, so two
        // concurrent uploads on one grant cannot both see a row count of 1.
        await using var update = Command(lease, $"""
            UPDATE {TablePrefix}media_grants SET used_at_unix_ms = @now
            WHERE project_id = @project AND grant_id = @grant AND used_at_unix_ms IS NULL AND expires_at_unix_ms > @now
            """, [Text("project", projectId), Text("grant", grantId), Int64("now", nowUnixMs)]);
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct)
    {
        StoreValidation.Id(record.ProjectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            INSERT INTO {TablePrefix}media_objects
                (project_id, media_id, purpose, sha256, content_type, size_bytes, width, height, owner_steam_id, visibility,
                 created_at_unix_ms, expires_at_unix_ms)
            VALUES (@project, @media, @purpose, @sha, @type, @size, @width, @height, @owner, @visibility, @created, @expires)
            """,
            [Text("project", record.ProjectId), Text("media", record.MediaId), Text("purpose", record.Purpose),
                Text("sha", record.Sha256), Text("type", record.ContentType), Int64("size", record.SizeBytes),
                Int32("width", record.Width), Int32("height", record.Height), Text("owner", record.OwnerSteamId),
                Text("visibility", record.Visibility), Int64("created", record.CreatedAtUnixMs),
                Int64("expires", record.ExpiresAtUnixMs)]);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<MediaObjectRecord?> GetMediaObjectAsync(string projectId, string mediaId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            SELECT project_id, media_id, purpose, sha256, content_type, size_bytes, width, height, owner_steam_id, visibility,
                   created_at_unix_ms, expires_at_unix_ms
            FROM {TablePrefix}media_objects WHERE project_id = @project AND media_id = @media
            """, [Text("project", projectId), Text("media", mediaId)]);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadMediaObject(reader);
    }

    /// <summary>Every object row in the project, expired ones included; see <see cref="IMediaStore.ListMediaObjectsAsync"/>.</summary>
    public async Task<IReadOnlyList<MediaObjectRecord>> ListMediaObjectsAsync(string projectId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            SELECT project_id, media_id, purpose, sha256, content_type, size_bytes, width, height, owner_steam_id, visibility,
                   created_at_unix_ms, expires_at_unix_ms
            FROM {TablePrefix}media_objects WHERE project_id = @project ORDER BY media_id
            """, [Text("project", projectId)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<MediaObjectRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadMediaObject(reader));
        return rows;
    }

    public async Task UpsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct)
    {
        StoreValidation.Id(record.ProjectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            INSERT INTO {TablePrefix}media_objects
                (project_id, media_id, purpose, sha256, content_type, size_bytes, width, height, owner_steam_id, visibility,
                 created_at_unix_ms, expires_at_unix_ms)
            VALUES (@project, @media, @purpose, @sha, @type, @size, @width, @height, @owner, @visibility, @created, @expires)
            ON CONFLICT (project_id, media_id) DO UPDATE SET
                purpose = excluded.purpose, sha256 = excluded.sha256, content_type = excluded.content_type,
                size_bytes = excluded.size_bytes, width = excluded.width, height = excluded.height,
                owner_steam_id = excluded.owner_steam_id, visibility = excluded.visibility,
                created_at_unix_ms = excluded.created_at_unix_ms, expires_at_unix_ms = excluded.expires_at_unix_ms
            """,
            [Text("project", record.ProjectId), Text("media", record.MediaId), Text("purpose", record.Purpose),
                Text("sha", record.Sha256), Text("type", record.ContentType), Int64("size", record.SizeBytes),
                Int32("width", record.Width), Int32("height", record.Height), Text("owner", record.OwnerSteamId),
                Text("visibility", record.Visibility), Int64("created", record.CreatedAtUnixMs),
                Int64("expires", record.ExpiresAtUnixMs)]);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static MediaObjectRecord ReadMediaObject(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), (int)reader.GetInt64(6), (int)reader.GetInt64(7), reader.GetString(8),
            reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11));

    public Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, long nowUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        // Expired rows stop counting here, at read time; the retention sweeper deletes them later.
        return QueryInt64ScalarAsync($"""
            SELECT COALESCE(SUM(size_bytes), 0) FROM {TablePrefix}media_objects
            WHERE project_id = @project AND purpose = @purpose AND expires_at_unix_ms > @now
            """, ct, Text("project", projectId), Text("purpose", purpose), Int64("now", nowUnixMs));
    }

    /// <summary>
    /// Expired rows in (project, media id) order. The cursor is a row-value comparison, which SQLite and PostgreSQL both
    /// order the same way as the ORDER BY.
    /// </summary>
    public async Task<IReadOnlyList<MediaObjectRecord>> ListExpiredMediaObjectsAsync(
        long nowUnixMs, (string ProjectId, string MediaId)? after, int limit, CancellationToken ct)
    {
        var args = new List<Arg> { Int64("now", nowUnixMs), Int32("limit", limit) };
        var cursor = string.Empty;
        if (after is { } key)
        {
            cursor = "AND (project_id, media_id) > (@afterProject, @afterMedia)";
            args.Add(Text("afterProject", key.ProjectId));
            args.Add(Text("afterMedia", key.MediaId));
        }

        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            SELECT project_id, media_id, purpose, sha256, content_type, size_bytes, width, height, owner_steam_id, visibility,
                   created_at_unix_ms, expires_at_unix_ms
            FROM {TablePrefix}media_objects
            WHERE expires_at_unix_ms <= @now {cursor}
            ORDER BY project_id, media_id
            LIMIT @limit
            """, args.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<MediaObjectRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadMediaObject(reader));
        return rows;
    }

    /// <summary>Every object row in the project that stores these bytes, expired rows included, with each row's expiry.</summary>
    public async Task<IReadOnlyList<MediaReference>> ListMediaReferencesBySha256Async(string projectId, string sha256, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease,
            $"SELECT media_id, expires_at_unix_ms FROM {TablePrefix}media_objects WHERE project_id = @project AND sha256 = @sha",
            [Text("project", projectId), Text("sha", sha256)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var references = new List<MediaReference>();
        while (await reader.ReadAsync(ct)) references.Add(new MediaReference(reader.GetString(0), reader.GetInt64(1)));
        return references;
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the blob lock for (project, sha256). Inside a project import the lock joins the
    /// import transaction. Outside one, a provider with cross-process locks gets its own transaction, which holds the
    /// lock and the rows <paramref name="work"/> writes until it commits. Other providers run the work directly.
    /// </summary>
    public async Task RunUnderMediaBlobLockAsync(string projectId, string sha256, Func<IMediaStore, Task> work, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        var key = $"{projectId}:{sha256}";
        if (_transaction is { } bound)
        {
            await LockMediaBlobKeyAsync(bound.Connection!, bound, key, ct);
            await work(this);
            return;
        }
        if (!MediaBlobLocksSpanProcesses)
        {
            await work(this);
            return;
        }
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockMediaBlobKeyAsync(connection, transaction, key, ct);
        await work(Bind(transaction));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Waits until no project import runs in this process, and holds that place until the handle is disposed. The
    /// caller takes it before any blob lock, so a sweep waiting out an import never holds a blob lock.
    /// </summary>
    public async Task<IDisposable> EnterMediaSweepGateAsync(CancellationToken ct)
    {
        await _projectImportGate.WaitAsync(ct);
        return new GateRelease(_projectImportGate);
    }

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async Task<bool> DeleteExpiredMediaObjectAsync(string projectId, string mediaId, long nowUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease,
            $"DELETE FROM {TablePrefix}media_objects WHERE project_id = @project AND media_id = @media AND expires_at_unix_ms <= @now",
            [Text("project", projectId), Text("media", mediaId), Int64("now", nowUnixMs)]);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
