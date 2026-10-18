using System.Data;
using System.Data.Common;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Media object rows and upload grant rows (schema v8).</summary>
public abstract partial class RelationalNetworkStorageStore : IMediaStore
{
    public async Task InsertMediaGrantAsync(MediaGrantRecord grant, long pruneIssuedBeforeUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(grant.ProjectId);
        await using var lease = await LeaseAsync(ct);
        await using (var prune = Command(lease, $"DELETE FROM {TablePrefix}media_grants WHERE issued_at_unix_ms < @cutoff",
            [Int64("cutoff", pruneIssuedBeforeUnixMs)]))
        {
            await prune.ExecuteNonQueryAsync(ct);
        }
        await using var insert = Command(lease, $"""
            INSERT INTO {TablePrefix}media_grants (project_id, grant_id, purpose, owner_steam_id, issued_at_unix_ms, expires_at_unix_ms)
            VALUES (@project, @grant, @purpose, @owner, @issued, @expires)
            """,
            [Text("project", grant.ProjectId), Text("grant", grant.GrantId), Text("purpose", grant.Purpose),
                Text("owner", grant.OwnerSteamId), Int64("issued", grant.IssuedAtUnixMs), Int64("expires", grant.ExpiresAtUnixMs)]);
        await insert.ExecuteNonQueryAsync(ct);
    }

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

    public Task<long> CountMediaGrantsSinceAsync(string projectId, string ownerSteamId, string purpose, long sinceUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return QueryInt64ScalarAsync($"""
            SELECT COUNT(*) FROM {TablePrefix}media_grants
            WHERE project_id = @project AND owner_steam_id = @owner AND purpose = @purpose AND issued_at_unix_ms >= @since
            """, ct, Text("project", projectId), Text("owner", ownerSteamId), Text("purpose", purpose), Int64("since", sinceUnixMs));
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
        return new MediaObjectRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), (int)reader.GetInt64(6), (int)reader.GetInt64(7), reader.GetString(8),
            reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11));
    }

    public Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return QueryInt64ScalarAsync($"SELECT COALESCE(SUM(size_bytes), 0) FROM {TablePrefix}media_objects WHERE project_id = @project AND purpose = @purpose",
            ct, Text("project", projectId), Text("purpose", purpose));
    }
}
