namespace SboxNetworkStorage.Storage;

/// <summary>
/// Durable delivery queue for endpoint webhook steps (schema v8) and the per-target delivery state the owner
/// Webhooks page shows. Rows are claimed one at a time with a conditional update, so a row is sent by at most
/// one claim.
/// </summary>
public interface IWebhookOutboxStore
{
    /// <summary>Stores a pending row unless its (project, target) already holds <paramref name="maxPendingPerTarget"/> pending rows. Returns false, storing nothing, when full.</summary>
    Task<bool> EnqueueAsync(WebhookOutboxRow row, int maxPendingPerTarget, CancellationToken ct);

    /// <summary>Claims up to <paramref name="limit"/> pending rows due at <paramref name="nowUnixMs"/> (pending to sending, leased until <paramref name="leaseUntilUnixMs"/>).</summary>
    Task<IReadOnlyList<WebhookOutboxRow>> ClaimDueAsync(long nowUnixMs, long leaseUntilUnixMs, int limit, CancellationToken ct);

    /// <summary>Writes the outcome of a claimed row (status, attempts, next attempt, last result).</summary>
    Task UpdateAsync(WebhookOutboxRow row, CancellationToken ct);

    /// <summary>Marks sending rows whose lease expired as failed ("interrupted"). They are never resent: the send may have landed.</summary>
    Task<int> ReapExpiredLeasesAsync(long nowUnixMs, CancellationToken ct);

    /// <summary>Deletes finished rows (delivered, failed, suppressed) last updated before <paramref name="beforeUnixMs"/>.</summary>
    Task<int> PurgeFinishedAsync(long beforeUnixMs, CancellationToken ct);

    /// <summary>Marks pending rows created before <paramref name="createdBeforeUnixMs"/> failed ("expired"). They are never sent late.</summary>
    Task<int> ExpirePendingAsync(long createdBeforeUnixMs, long nowUnixMs, CancellationToken ct);

    /// <summary>Suppresses a project's pending rows with <paramref name="reason"/> (outbox turned off, project deleted).</summary>
    Task<int> CancelPendingAsync(string projectId, string reason, long nowUnixMs, CancellationToken ct);

    /// <summary>Deletes target state last updated before the cutoff that has no outbox rows and is not disabled.</summary>
    Task<int> PruneStaleTargetsAsync(long beforeUnixMs, CancellationToken ct);

    Task<WebhookTargetState?> ReadTargetAsync(string projectId, string targetUrl, CancellationToken ct);
    Task<IReadOnlyList<WebhookTargetState>> ListTargetsAsync(string projectId, CancellationToken ct);
    Task WriteTargetAsync(WebhookTargetState state, CancellationToken ct);
}

/// <summary>One queued webhook send. <see cref="PayloadJson"/> is the step payload as the game sent it.</summary>
public sealed record WebhookOutboxRow(
    string ProjectId,
    string OutboxId,
    string TargetUrl,
    string PayloadJson,
    WebhookOutboxStatus Status,
    int Attempts,
    long NextAttemptAtUnixMs,
    long? LeaseUntilUnixMs,
    int? LastStatus,
    string? LastError,
    long CreatedAtUnixMs,
    long UpdatedAtUnixMs);

/// <summary>Delivery health of one webhook URL within a project. Disabled targets are not sent to until re-enabled.</summary>
public sealed record WebhookTargetState(
    string ProjectId,
    string TargetUrl,
    int ConsecutiveFailures,
    long? FailingSinceUnixMs,
    int? LastStatus,
    string? LastError,
    long? DisabledAtUnixMs,
    string? DisabledReason,
    long UpdatedAtUnixMs);

/// <summary>Lifecycle of one outbox row. Sending rows hold a lease; expired leases are reaped to Failed.</summary>
public enum WebhookOutboxStatus
{
    Pending,
    Sending,
    Delivered,
    Failed,
    Suppressed,
}
