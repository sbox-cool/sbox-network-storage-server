using System.Data;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Hourly per-library request outcomes (schema v7), read by the post-update regression verdict.</summary>
public abstract partial class RelationalNetworkStorageStore
{
    public async Task WriteClientOutcomesAsync(IReadOnlyList<ClientOutcomeDelta> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await using var connection = await OpenConnectionAsync(ct);
        foreach (var chunk in batch.Chunk(WriteChunkGroups))
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            foreach (var delta in chunk)
            {
                StoreValidation.Id(delta.Key.ProjectId);
                if (delta.Requests <= 0 || delta.Rejected < 0 || delta.ServerErrors < 0) throw new ArgumentOutOfRangeException(nameof(batch));
                await using var write = Command(connection, $"""
                    INSERT INTO {TablePrefix}client_outcomes_hourly
                        (project_id, hour, server_version, group_key, library_version, library_revision, client_type, requests, rejected, server_errors)
                    VALUES (@project, @hour, @server, @group, @version, @revision, @type, @requests, @rejected, @errors)
                    ON CONFLICT (project_id, hour, server_version, group_key) DO UPDATE SET
                        requests = {TablePrefix}client_outcomes_hourly.requests + excluded.requests,
                        rejected = {TablePrefix}client_outcomes_hourly.rejected + excluded.rejected,
                        server_errors = {TablePrefix}client_outcomes_hourly.server_errors + excluded.server_errors
                    """, [Text("project", delta.Key.ProjectId), Int64("hour", delta.Key.Hour), Text("server", delta.Key.ServerVersion),
                        Text("group", delta.Key.GroupKey), Text("version", delta.Key.LibraryVersion), Int64("revision", delta.Key.LibraryRevision),
                        Text("type", delta.Key.ClientType), Int64("requests", delta.Requests), Int64("rejected", delta.Rejected),
                        Int64("errors", delta.ServerErrors)], transaction);
                await write.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ClientOutcomeTotals>> ReadClientOutcomeTotalsAsync(long sinceHour, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection, $"""
            SELECT server_version, library_version, CAST(SUM(requests) AS BIGINT), CAST(SUM(rejected) AS BIGINT),
                   CAST(SUM(server_errors) AS BIGINT), CAST(MAX(hour) AS BIGINT)
            FROM {TablePrefix}client_outcomes_hourly WHERE hour >= @since GROUP BY server_version, library_version
            """, [Int64("since", sinceHour)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ClientOutcomeTotals>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ClientOutcomeTotals(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5)));
        return rows;
    }

    public Task PurgeClientOutcomesAsync(long beforeHour, CancellationToken ct)
        => ExecuteAsync($"DELETE FROM {TablePrefix}client_outcomes_hourly WHERE hour < @hour", ct, Int64("hour", beforeHour));

    /// <summary>Per-library-build request totals from the daily table, for update preflight (no per-project breakdown).</summary>
    public async Task<IReadOnlyList<ObservedLibrary>> ReadObservedLibrariesAsync(int sinceDay, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection, $"""
            SELECT library_version, library_revision, CAST(SUM(requests) AS BIGINT)
            FROM {TablePrefix}client_versions_daily WHERE day >= @day GROUP BY library_version, library_revision
            """, [Int32("day", sinceDay)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ObservedLibrary>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ObservedLibrary(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }
}
