using System.Text;
using System.Text.Json;
using C = SboxNetworkStorage.Storage.Relational.StoreColumns;
using V = SboxNetworkStorage.Storage.Relational.StoreValidation;

namespace SboxNetworkStorage.Storage.Relational;

// Player data: records, idempotency, global records, ledger, checkpoint
// cursor, usage counters, workspace objects.
public abstract partial class RelationalNetworkStorageStore
{
    // ── records ─────────────────────────────────────────────────────

    public Task UpsertRecordAsync(string projectId, string collectionId, string recordKey, JsonElement payloadJson, bool deleted, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        var payload = Serialize(payloadJson, "records");
        return ExecuteAsync(_sql.UpsertRecord, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Text("payload_json", payload), Bool("deleted", deleted), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return QuerySingleAsync(_sql.ReadRecord, C.Record, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey));
    }

    public Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId);
        return QueryListAsync(_sql.ListRecords, C.Record, ct, Text("project_id", projectId), Text("collection_id", collectionId));
    }

    public Task DeleteRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return ExecuteAsync(_sql.DeleteRecord, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey));
    }

    public async Task<bool> TryMutateRecordAsync(string projectId, string collectionId, string recordKey,
        bool global, JsonElement payloadJson, bool delete, long? expectedVersion, CancellationToken ct,
        RecordMutationSnapshot? snapshot = null)
    {
        V.Id(projectId); V.Id(collectionId);
        if (global) V.Id(recordKey); else V.RecordKey(recordKey);
        if (expectedVersion is < 1 or long.MaxValue || (delete && expectedVersion is null))
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var table = TablePrefix + (global ? "global_records" : "records");
        var key = global ? "record_id" : "record_key";
        var timestamp = global ? "created_at_unix_ms" : "updated_at_unix_ms";
        var predicate = $"project_id = @project_id AND collection_id = @collection_id AND {key} = @record_key";
        var args = new List<Arg>
        {
            Text("project_id", projectId), Text("collection_id", collectionId),
            Text("record_key", recordKey), Int64("expected_version", expectedVersion)
        };
        string sql;
        if (global && delete)
            sql = $"DELETE FROM {table} WHERE {predicate} AND version = @expected_version";
        else
        {
            args.Add(Text("payload_json", Serialize(payloadJson, global ? "global_records" : "records")));
            args.Add(Int64("changed_at", UnixMs()));
            if (!global) args.Add(Bool("deleted", delete));
            var deletedColumn = global ? "" : ", deleted";
            var deletedValue = global ? "" : ", @deleted";
            var deletedSet = global ? "" : ", deleted = @deleted";
            if (expectedVersion is null)
            {
                // ON CONFLICT checks the locked row, including a concurrent insert.
                // Tombstones are logically absent but keep their monotonically increasing version.
                sql = $"INSERT INTO {table} (project_id, collection_id, {key}, payload_json, version, {timestamp}{deletedColumn}) " +
                    $"VALUES (@project_id, @collection_id, @record_key, @payload_json, 1, @changed_at{deletedValue}) " +
                    $"ON CONFLICT (project_id, collection_id, {key}) " +
                    (global ? "DO NOTHING" :
                        $"DO UPDATE SET payload_json = @payload_json, version = {table}.version + 1, {timestamp} = @changed_at, deleted = @deleted WHERE {table}.deleted = @tombstone");
                if (!global) args.Add(Bool("tombstone", true));
            }
            else
            {
                sql = $"UPDATE {table} SET payload_json = @payload_json, version = version + 1, {timestamp} = @changed_at{deletedSet} " +
                    $"WHERE {predicate} AND version = @expected_version";
                if (!global)
                {
                    sql += " AND deleted = @live";
                    args.Add(Bool("live", false));
                }
            }
        }
        if (snapshot is not null && expectedVersion is not null)
        {
            sql += " AND payload_json = @expected_payload";
            args.Add(Text("expected_payload", snapshot.PayloadJson));
            if (snapshot.ChangedAtUnixMs is { } changedAt)
            {
                sql += $" AND {timestamp} = @expected_changed_at";
                args.Add(Int64("expected_changed_at", changedAt));
            }
        }
        return await ExecuteCountAsync(sql, ct, args.ToArray()) == 1;
    }

    // ── record_idempotency ──────────────────────────────────────────

    public Task UpsertRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, long resultRecordVersion, string resultHash, JsonElement payloadJson, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        var payload = Serialize(payloadJson, "record_idempotency");
        return ExecuteAsync(_sql.UpsertRecordIdempotency, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Text("idempotency_key", idempotencyKey), Int64("result_record_version", resultRecordVersion),
            Text("result_hash", resultHash), Text("payload_json", payload), Int64("created_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return QuerySingleAsync(_sql.ReadRecordIdempotency, C.RecordIdempotency, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Text("idempotency_key", idempotencyKey));
    }

    public Task DeleteRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return ExecuteAsync(_sql.DeleteRecordIdempotency, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Text("idempotency_key", idempotencyKey));
    }

    // ── global_records ──────────────────────────────────────────────

    public Task UpsertGlobalRecordAsync(string projectId, string collectionId, string recordId, JsonElement payloadJson, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.Id(recordId);
        var payload = Serialize(payloadJson, "global_records");
        return ExecuteAsync(_sql.UpsertGlobalRecord, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_id", recordId),
            Text("payload_json", payload), Int64("version", version), Int64("created_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.Id(recordId);
        return QuerySingleAsync(_sql.ReadGlobalRecord, C.GlobalRecord, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_id", recordId));
    }

    public Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId);
        return QueryListAsync(_sql.ListGlobalRecords, C.GlobalRecord, ct, Text("project_id", projectId), Text("collection_id", collectionId));
    }

    public Task DeleteGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.Id(recordId);
        return ExecuteAsync(_sql.DeleteGlobalRecord, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_id", recordId));
    }

    // ── ledger_entries ──────────────────────────────────────────────

    public Task InsertLedgerEntryAsync(string projectId, string collectionId, string recordKey, long sequence, JsonElement entryJson, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        var entry = Serialize(entryJson, "ledger_entries");
        return ExecuteAsync(_sql.UpsertLedgerEntry, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Int64("sequence", sequence), Text("entry_json", entry), Int64("created_at_unix_ms", UnixMs()));
    }

    public Task<IReadOnlyList<JsonElement>> ListLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return QueryListAsync(_sql.ListLedgerEntries, C.LedgerEntry, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey));
    }

    public Task DeleteLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return ExecuteAsync(_sql.DeleteLedgerEntries, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey));
    }

    // ── checkpoint_cursor ───────────────────────────────────────────

    public Task UpsertCheckpointCursorAsync(string projectId, long latestSequence, string? manifestPath, long version, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.UpsertCheckpointCursor, ct,
            Text("project_id", projectId), Int64("latest_sequence", latestSequence), Text("manifest_path", manifestPath),
            Int64("updated_at_unix_ms", UnixMs()), Int64("version", version));
    }

    public Task<JsonElement?> ReadCheckpointCursorAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadCheckpointCursor, C.CheckpointCursor, ct, Text("project_id", projectId));
    }

    public Task DeleteCheckpointCursorAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteCheckpointCursor, ct, Text("project_id", projectId));
    }

    // ── usage counters ──────────────────────────────────────────────

    public async Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct)
    {
        V.Id(projectId);
        if (string.IsNullOrEmpty(month)) throw new ArgumentException("Month key is required.", nameof(month));
        if (string.IsNullOrEmpty(day)) throw new ArgumentException("Day key is required.", nameof(day));

        await using var lease = await LeaseAsync(ct);
        var connection = lease.Connection;
        await ExecuteAsync(connection, _sql.IncrementUsageMonthly, ct,
            Text("project_id", projectId), Text("month", month),
            Int64("requests", delta.Requests), Int64("reads", delta.Reads), Int64("writes", delta.Writes),
            Int64("endpoint_calls", delta.EndpointCalls), Int64("bytes_in", delta.BytesIn), Int64("bytes_out", delta.BytesOut),
            Int64("errors", delta.Errors), Int64("duration_ms_sum", delta.DurationMsSum), Int64("duration_samples", delta.DurationSamples),
            Int64("compute_units", delta.ComputeUnits), Int64("storage_delta_bytes", delta.StorageDeltaBytes));
        await ExecuteAsync(connection, _sql.IncrementUsageDaily, ct,
            Text("project_id", projectId), Text("month", month), Text("day", day),
            Int64("requests", delta.Requests), Int64("bytes_in", delta.BytesIn), Int64("bytes_out", delta.BytesOut),
            Int64("errors", delta.Errors), Int64("compute_units", delta.ComputeUnits));
        if (!string.IsNullOrEmpty(endpointSlug))
        {
            await ExecuteAsync(connection, _sql.IncrementUsageEndpoint, ct,
                Text("project_id", projectId), Text("month", month), Text("endpoint_slug", endpointSlug),
                Int64("calls", delta.EndpointCalls), Int64("errors", delta.Errors),
                Int64("duration_ms_sum", delta.DurationMsSum), Int64("compute_units", delta.ComputeUnits));
        }
    }

    public Task<JsonElement?> ReadProjectUsageMonthlyAsync(string projectId, string month, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadUsageMonthly, C.UsageMonthly, ct, Text("project_id", projectId), Text("month", month));
    }

    public async Task<long> ReadProjectStorageBytesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, _sql.ReadUsageStorage, [Text("project_id", projectId)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        long total = 0;
        while (await reader.ReadAsync(ct))
            total += reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        return Math.Max(0, total);
    }

    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageDailyAsync(string projectId, string month, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ReadUsageDaily, C.UsageDaily, ct, Text("project_id", projectId), Text("month", month));
    }

    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageEndpointsAsync(string projectId, string month, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Limit(limit);
        return QueryListAsync(_sql.ReadUsageEndpoints, C.UsageEndpoint, ct,
            Text("project_id", projectId), Text("month", month), Int32("limit", limit));
    }

    // ── workspace_objects ───────────────────────────────────────────

    public Task PutWorkspaceObjectAsync(string path, string content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = WorkspaceObjectPaths.NormalizeObjectPath(path);
        return ExecuteAsync(_sql.UpsertWorkspaceObject, ct,
            Text("path", normalized), Text("content", content),
            Int64("size_bytes", Encoding.UTF8.GetByteCount(content)), Int64("updated_at_unix_ms", UnixMs()));
    }

    public async Task<string?> ReadWorkspaceObjectAsync(string path, CancellationToken ct)
    {
        var (_, content) = await QueryTextAsync(_sql.ReadWorkspaceObject, ct, Text("path", WorkspaceObjectPaths.NormalizeObjectPath(path)));
        return content;
    }

    public Task DeleteWorkspaceObjectAsync(string path, CancellationToken ct)
        => ExecuteAsync(_sql.DeleteWorkspaceObject, ct, Text("path", WorkspaceObjectPaths.NormalizeObjectPath(path)));

    public async Task<IReadOnlyList<WorkspaceObjectEntry>> ListWorkspaceObjectsAsync(string directoryPath, CancellationToken ct)
    {
        var prefix = WorkspaceObjectPaths.NormalizeDirectoryPrefix(directoryPath);
        await using var lease = await LeaseAsync(ct);
        // A half-open byte range [prefix, prefix with '/' bumped to '0') selects
        // exactly the paths starting with the prefix. Unlike LIKE it cannot
        // over-match on '_' or '%' and is case-sensitive on every driver.
        await using var command = prefix.Length == 0
            ? Command(lease, _sql.ListAllWorkspaceObjects, [])
            : Command(lease, _sql.ListWorkspaceObjectsInRange,
                [Text("range_start", prefix), Text("range_end", string.Concat(prefix.AsSpan(0, prefix.Length - 1), "0"))]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var objects = new List<(string Path, long LengthBytes, DateTimeOffset LastChanged)>();
        while (await reader.ReadAsync(ct))
            objects.Add((reader.GetString(0), reader.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        return WorkspaceObjectPaths.ImmediateChildren(prefix, objects);
    }
}
