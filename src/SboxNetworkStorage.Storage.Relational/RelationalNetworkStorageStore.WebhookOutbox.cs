using System.Data;
using System.Data.Common;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Endpoint webhook outbox and per-target delivery state (schema v8).</summary>
public abstract partial class RelationalNetworkStorageStore : IWebhookOutboxStore
{
    private const string OutboxColumns = "project_id, outbox_id, target_url, payload_json, status, attempts, next_attempt_at_unix_ms, lease_until_unix_ms, last_status, last_error, created_at_unix_ms, updated_at_unix_ms";
    private const string TargetColumns = "project_id, target_url, consecutive_failures, failing_since_unix_ms, last_status, last_error, disabled_at_unix_ms, disabled_reason, updated_at_unix_ms";

    public async Task<bool> EnqueueAsync(WebhookOutboxRow row, int maxPendingPerTarget, CancellationToken ct)
    {
        StoreValidation.Id(row.ProjectId);
        // One statement counts and inserts, so the cap holds under concurrent enqueues.
        var inserted = await ExecuteCountAsync($"""
            INSERT INTO {T("webhook_outbox")} ({OutboxColumns})
            SELECT @project, @outbox, @target, @payload, @status, @attempts, @next, @lease, @last_status, @last_error, @created, @updated
            WHERE (SELECT COUNT(*) FROM {T("webhook_outbox")} WHERE project_id = @project AND target_url = @target AND status = 'pending') < @cap
            """, ct, [.. OutboxArgs(row), Int32("cap", maxPendingPerTarget)]);
        return inserted == 1;
    }

    public async Task<IReadOnlyList<WebhookOutboxRow>> ClaimDueAsync(long nowUnixMs, long leaseUntilUnixMs, int limit, CancellationToken ct)
    {
        StoreValidation.Limit(limit);
        var due = await ReadOutboxRowsAsync($"""
            SELECT {OutboxColumns} FROM {T("webhook_outbox")}
            WHERE status = 'pending' AND next_attempt_at_unix_ms <= @now
            ORDER BY next_attempt_at_unix_ms, outbox_id LIMIT @limit
            """, ct, Int64("now", nowUnixMs), Int32("limit", limit));
        var claimed = new List<WebhookOutboxRow>(due.Count);
        foreach (var row in due)
        {
            var won = await ExecuteCountAsync($"""
                UPDATE {T("webhook_outbox")} SET status = 'sending', lease_until_unix_ms = @lease, updated_at_unix_ms = @now
                WHERE project_id = @project AND outbox_id = @outbox AND status = 'pending'
                """, ct, Int64("lease", leaseUntilUnixMs), Int64("now", nowUnixMs), Text("project", row.ProjectId), Text("outbox", row.OutboxId));
            if (won == 1) claimed.Add(row with { Status = WebhookOutboxStatus.Sending, LeaseUntilUnixMs = leaseUntilUnixMs, UpdatedAtUnixMs = nowUnixMs });
        }
        return claimed;
    }

    public Task UpdateAsync(WebhookOutboxRow row, CancellationToken ct)
        => ExecuteAsync($"""
            UPDATE {T("webhook_outbox")} SET status = @status, attempts = @attempts, next_attempt_at_unix_ms = @next,
                lease_until_unix_ms = @lease, last_status = @last_status, last_error = @last_error, updated_at_unix_ms = @updated
            WHERE project_id = @project AND outbox_id = @outbox
            """, ct, OutboxArgs(row));

    public Task<int> ReapExpiredLeasesAsync(long nowUnixMs, CancellationToken ct)
        => ExecuteCountAsync($"""
            UPDATE {T("webhook_outbox")} SET status = 'failed', lease_until_unix_ms = NULL, last_error = 'interrupted before the delivery finished', updated_at_unix_ms = @now
            WHERE status = 'sending' AND lease_until_unix_ms < @now
            """, ct, Int64("now", nowUnixMs));

    public Task<int> PurgeFinishedAsync(long beforeUnixMs, CancellationToken ct)
        => ExecuteCountAsync($"""
            DELETE FROM {T("webhook_outbox")} WHERE status IN ('delivered', 'failed', 'suppressed') AND updated_at_unix_ms < @before
            """, ct, Int64("before", beforeUnixMs));

    public Task<int> ExpirePendingAsync(long createdBeforeUnixMs, long nowUnixMs, CancellationToken ct)
        => ExecuteCountAsync($"""
            UPDATE {T("webhook_outbox")} SET status = 'failed', last_error = 'expired', updated_at_unix_ms = @now
            WHERE status = 'pending' AND created_at_unix_ms < @before
            """, ct, Int64("before", createdBeforeUnixMs), Int64("now", nowUnixMs));

    public async Task<int> CancelPendingAsync(string projectId, string reason, long nowUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return await ExecuteCountAsync($"""
            UPDATE {T("webhook_outbox")} SET status = 'suppressed', lease_until_unix_ms = NULL, last_error = @reason, updated_at_unix_ms = @now
            WHERE project_id = @project AND status = 'pending'
            """, ct, Text("project", projectId), Text("reason", reason), Int64("now", nowUnixMs));
    }

    public Task<int> PruneStaleTargetsAsync(long beforeUnixMs, CancellationToken ct)
        => ExecuteCountAsync($"""
            DELETE FROM {T("webhook_targets")} AS t
            WHERE t.disabled_at_unix_ms IS NULL AND t.updated_at_unix_ms < @before
              AND NOT EXISTS (SELECT 1 FROM {T("webhook_outbox")} AS o WHERE o.project_id = t.project_id AND o.target_url = t.target_url)
            """, ct, Int64("before", beforeUnixMs));

    public async Task<WebhookTargetState?> ReadTargetAsync(string projectId, string targetUrl, CancellationToken ct)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"SELECT {TargetColumns} FROM {T("webhook_targets")} WHERE project_id = @project AND target_url = @target",
            [Text("project", projectId), Text("target", targetUrl)]);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        return await reader.ReadAsync(ct) ? ReadTarget(reader) : null;
    }

    public async Task<IReadOnlyList<WebhookTargetState>> ListTargetsAsync(string projectId, CancellationToken ct)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"SELECT {TargetColumns} FROM {T("webhook_targets")} WHERE project_id = @project ORDER BY target_url",
            [Text("project", projectId)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WebhookTargetState>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadTarget(reader));
        return rows;
    }

    public async Task WriteTargetAsync(WebhookTargetState state, CancellationToken ct)
    {
        StoreValidation.Id(state.ProjectId);
        await ExecuteAsync($"""
            INSERT INTO {T("webhook_targets")} ({TargetColumns})
            VALUES (@project, @target, @failures, @failing_since, @last_status, @last_error, @disabled_at, @disabled_reason, @updated)
            ON CONFLICT (project_id, target_url) DO UPDATE SET
                consecutive_failures = excluded.consecutive_failures, failing_since_unix_ms = excluded.failing_since_unix_ms,
                last_status = excluded.last_status, last_error = excluded.last_error,
                disabled_at_unix_ms = excluded.disabled_at_unix_ms, disabled_reason = excluded.disabled_reason,
                updated_at_unix_ms = excluded.updated_at_unix_ms
            """, ct,
            Text("project", state.ProjectId), Text("target", state.TargetUrl), Int32("failures", state.ConsecutiveFailures),
            Int64("failing_since", state.FailingSinceUnixMs), Int32("last_status", state.LastStatus), Text("last_error", state.LastError),
            Int64("disabled_at", state.DisabledAtUnixMs), Text("disabled_reason", state.DisabledReason), Int64("updated", state.UpdatedAtUnixMs));
    }

    private async Task<List<WebhookOutboxRow>> ReadOutboxRowsAsync(string sql, CancellationToken ct, params Arg[] args)
    {
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WebhookOutboxRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new WebhookOutboxRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Enum.Parse<WebhookOutboxStatus>(reader.GetString(4), ignoreCase: true), reader.GetInt32(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10), reader.GetInt64(11)));
        return rows;
    }

    private static WebhookTargetState ReadTarget(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt64(8));

    private static Arg[] OutboxArgs(WebhookOutboxRow row) =>
    [
        Text("project", row.ProjectId), Text("outbox", row.OutboxId), Text("target", row.TargetUrl), Text("payload", row.PayloadJson),
        Text("status", row.Status.ToString().ToLowerInvariant()), Int32("attempts", row.Attempts), Int64("next", row.NextAttemptAtUnixMs),
        Int64("lease", row.LeaseUntilUnixMs), Int32("last_status", row.LastStatus), Text("last_error", row.LastError),
        Int64("created", row.CreatedAtUnixMs), Int64("updated", row.UpdatedAtUnixMs),
    ];
}
