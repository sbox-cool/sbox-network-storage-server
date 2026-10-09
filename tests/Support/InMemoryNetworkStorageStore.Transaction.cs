using System.Collections.Concurrent;

namespace SboxNetworkStorage.Storage;

public partial class InMemoryNetworkStorageStore
{
    private readonly object _stateGate = new();
    private bool _transactionView;

    /// <summary>
    /// Stages writes in a private store. Commit validates changed rows against the opening snapshot, then
    /// publishes every change under the same gate used by ordinary operations. A rollback never touches live
    /// rows, including writes made by other callers while the transaction was open.
    /// </summary>
    public Task<IStoreTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_transactionView) throw new InvalidOperationException("Transactions cannot be nested.");
        lock (_stateGate)
        {
            var staged = new InMemoryNetworkStorageStore(_time) { _transactionView = true, _logSequence = _logSequence };
            List<Func<Action>> changes =
            [
                Stage(Projects, staged.Projects), Stage(Collections, staged.Collections), Stage(Endpoints, staged.Endpoints),
                Stage(Workflows, staged.Workflows), Stage(GameValues, staged.GameValues), Stage(RateLimitRules, staged.RateLimitRules),
                Stage(Queries, staged.Queries), Stage(QueryLastRuns, staged.QueryLastRuns), Stage(QueryRunLogs, staged.QueryRunLogs),
                Stage(Records, staged.Records), Stage(RecordIdempotency, staged.RecordIdempotency), Stage(GlobalRecords, staged.GlobalRecords),
                Stage(LedgerEntries, staged.LedgerEntries), Stage(CheckpointCursors, staged.CheckpointCursors), Stage(ApiKeys, staged.ApiKeys),
                Stage(AuditLogs, staged.AuditLogs), Stage(PlayerAnalytics, staged.PlayerAnalytics), Stage(PlayerAnalyticsEvents, staged.PlayerAnalyticsEvents),
                Stage(PlayerProfiles, staged.PlayerProfiles), Stage(PlayerSessions, staged.PlayerSessions), Stage(ProjectAnalyticsIssues, staged.ProjectAnalyticsIssues),
                Stage(ProjectMembers, staged.ProjectMembers), Stage(Pages, staged.Pages), Stage(StorageErrors, staged.StorageErrors),
                Stage(StorageRequestLog, staged.StorageRequestLog), Stage(WorkspaceObjects, staged.WorkspaceObjects),
                Stage(UsageMonthly, staged.UsageMonthly, CloneCounters, EqualCounters),
                Stage(UsageDaily, staged.UsageDaily, CloneCounters, EqualCounters),
                Stage(UsageEndpoints, staged.UsageEndpoints, CloneCounters, EqualCounters),
            ];
            return Task.FromResult<IStoreTransaction>(new Transaction(this, staged, changes));
        }
    }

    private static ConcurrentDictionary<string, long> CloneCounters(ConcurrentDictionary<string, long> row)
        => new(row, StringComparer.Ordinal);

    private static bool EqualCounters(ConcurrentDictionary<string, long> left, ConcurrentDictionary<string, long> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static Func<Action> Stage<T>(ConcurrentDictionary<string, T> live, ConcurrentDictionary<string, T> staged,
        Func<T, T>? clone = null, Func<T, T, bool>? equal = null)
    {
        clone ??= static value => value;
        // JsonElement rows are immutable: unchanged snapshots retain the same document identity, so this
        // detects replacement without serializing every existing row on every transaction commit.
        equal ??= EqualityComparer<T>.Default.Equals;
        var baseline = live.ToDictionary(pair => pair.Key, pair => clone(pair.Value), StringComparer.Ordinal);
        foreach (var (key, value) in baseline) staged[key] = clone(value);
        return () =>
        {
            var keys = baseline.Keys.Concat(staged.Keys).Distinct(StringComparer.Ordinal);
            var writes = new List<(string Key, bool Present, T Value)>();
            foreach (var key in keys)
            {
                var existed = baseline.TryGetValue(key, out var before);
                var present = staged.TryGetValue(key, out var after);
                if (existed == present && (!present || equal(before!, after!))) continue;
                var currentExists = live.TryGetValue(key, out var current);
                if (existed != currentExists || (existed && !equal(before!, current!)))
                    throw new InvalidOperationException("A concurrent write conflicts with the transaction.");
                writes.Add((key, present, after!));
            }
            return () =>
            {
                foreach (var (key, present, value) in writes)
                    if (present) live[key] = clone(value); else live.TryRemove(key, out _);
            };
        };
    }

    private sealed class Transaction(InMemoryNetworkStorageStore owner, InMemoryNetworkStorageStore staged,
        List<Func<Action>> changes) : IStoreTransaction
    {
        private bool _finished;
        public INetworkStorageStore Store => staged;

        public Task CommitAsync(CancellationToken ct)
        {
            lock (owner._stateGate)
            lock (staged._stateGate)
            {
                ct.ThrowIfCancellationRequested();
                if (_finished) throw new InvalidOperationException("The transaction has already finished.");
                // Validate all tables before modifying any of them.
                var apply = changes.Select(change => change()).ToArray();
                foreach (var write in apply) write();
                owner._logSequence = Math.Max(owner._logSequence, staged._logSequence);
                _finished = true;
                return Task.CompletedTask;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (owner._stateGate) _finished = true;
            return ValueTask.CompletedTask;
        }
    }
}
