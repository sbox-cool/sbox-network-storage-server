namespace SboxNetworkStorage.Storage;

/// <summary>Process-local <see cref="IWebhookOutboxStore"/> for stores without a relational backend and for tests. Nothing survives a restart.</summary>
public sealed class InMemoryWebhookOutboxStore : IWebhookOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Project, string Outbox), WebhookOutboxRow> _rows = [];
    private readonly Dictionary<(string Project, string Target), WebhookTargetState> _targets = [];

    public Task<bool> EnqueueAsync(WebhookOutboxRow row, int maxPendingPerTarget, CancellationToken ct)
    {
        lock (_gate)
        {
            var pending = _rows.Values.Count(existing => existing.ProjectId == row.ProjectId
                && existing.TargetUrl == row.TargetUrl && existing.Status == WebhookOutboxStatus.Pending);
            if (pending >= maxPendingPerTarget) return Task.FromResult(false);
            _rows[(row.ProjectId, row.OutboxId)] = row;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<WebhookOutboxRow>> ClaimDueAsync(long nowUnixMs, long leaseUntilUnixMs, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var due = _rows.Values
                .Where(row => row.Status == WebhookOutboxStatus.Pending && row.NextAttemptAtUnixMs <= nowUnixMs)
                .OrderBy(row => row.NextAttemptAtUnixMs).ThenBy(row => row.OutboxId, StringComparer.Ordinal)
                .Take(limit).ToList();
            var claimed = new List<WebhookOutboxRow>(due.Count);
            foreach (var row in due)
            {
                var next = row with { Status = WebhookOutboxStatus.Sending, LeaseUntilUnixMs = leaseUntilUnixMs, UpdatedAtUnixMs = nowUnixMs };
                _rows[(row.ProjectId, row.OutboxId)] = next;
                claimed.Add(next);
            }
            return Task.FromResult<IReadOnlyList<WebhookOutboxRow>>(claimed);
        }
    }

    public Task UpdateAsync(WebhookOutboxRow row, CancellationToken ct)
    {
        lock (_gate) _rows[(row.ProjectId, row.OutboxId)] = row;
        return Task.CompletedTask;
    }

    public Task<int> ReapExpiredLeasesAsync(long nowUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var expired = _rows.Values.Where(row => row.Status == WebhookOutboxStatus.Sending && row.LeaseUntilUnixMs < nowUnixMs).ToList();
            foreach (var row in expired)
                _rows[(row.ProjectId, row.OutboxId)] = row with
                {
                    Status = WebhookOutboxStatus.Failed, LeaseUntilUnixMs = null,
                    LastError = "interrupted before the delivery finished", UpdatedAtUnixMs = nowUnixMs,
                };
            return Task.FromResult(expired.Count);
        }
    }

    public Task<int> PurgeFinishedAsync(long beforeUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var stale = _rows.Values.Where(row => row.Status is WebhookOutboxStatus.Delivered or WebhookOutboxStatus.Failed or WebhookOutboxStatus.Suppressed
                && row.UpdatedAtUnixMs < beforeUnixMs).ToList();
            foreach (var row in stale) _rows.Remove((row.ProjectId, row.OutboxId));
            return Task.FromResult(stale.Count);
        }
    }

    public Task<int> ExpirePendingAsync(long createdBeforeUnixMs, long nowUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var expired = _rows.Values.Where(row => row.Status == WebhookOutboxStatus.Pending && row.CreatedAtUnixMs < createdBeforeUnixMs).ToList();
            foreach (var row in expired)
                _rows[(row.ProjectId, row.OutboxId)] = row with { Status = WebhookOutboxStatus.Failed, LastError = "expired", UpdatedAtUnixMs = nowUnixMs };
            return Task.FromResult(expired.Count);
        }
    }

    public Task<int> CancelPendingAsync(string projectId, string reason, long nowUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var pending = _rows.Values.Where(row => row.ProjectId == projectId && row.Status == WebhookOutboxStatus.Pending).ToList();
            foreach (var row in pending)
                _rows[(row.ProjectId, row.OutboxId)] = row with { Status = WebhookOutboxStatus.Suppressed, LastError = reason, UpdatedAtUnixMs = nowUnixMs };
            return Task.FromResult(pending.Count);
        }
    }

    public Task<int> PruneStaleTargetsAsync(long beforeUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var stale = _targets.Values.Where(state => state.DisabledAtUnixMs is null && state.UpdatedAtUnixMs < beforeUnixMs
                && !_rows.Values.Any(row => row.ProjectId == state.ProjectId && row.TargetUrl == state.TargetUrl)).ToList();
            foreach (var state in stale) _targets.Remove((state.ProjectId, state.TargetUrl));
            return Task.FromResult(stale.Count);
        }
    }

    public Task<WebhookTargetState?> ReadTargetAsync(string projectId, string targetUrl, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_targets.GetValueOrDefault((projectId, targetUrl)));
    }

    public Task<IReadOnlyList<WebhookTargetState>> ListTargetsAsync(string projectId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<WebhookTargetState> targets = _targets.Values.Where(state => state.ProjectId == projectId)
                .OrderBy(state => state.TargetUrl, StringComparer.Ordinal).ToList();
            return Task.FromResult(targets);
        }
    }

    public Task WriteTargetAsync(WebhookTargetState state, CancellationToken ct)
    {
        lock (_gate) _targets[(state.ProjectId, state.TargetUrl)] = state;
        return Task.CompletedTask;
    }
}
