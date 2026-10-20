namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Serializes live endpoint writes to the same record within one process, like the reference backend's keyed write
/// queue: one FIFO per <c>(project, collection, key)</c>. A call that finds a record busy waits behind the holder
/// instead of failing, so two independent calls on one record run one after the other rather than the second
/// answering 409 <c>CONFLICT</c> on its version check.
/// <para>
/// Bounds match the reference: at most <see cref="DefaultMaxQueued"/> waiters per record (a full queue answers 429
/// <c>NETWORK_STORAGE_WRITE_QUEUE_FULL</c> at once), and at most <see cref="DefaultTimeout"/> of waiting (429 <c>NETWORK_STORAGE_WRITE_QUEUE_TIMEOUT</c>). Both
/// carry a <c>Retry-After</c> hint.
/// </para>
/// <para>
/// Register one instance as a singleton; a per-request instance would serialize nothing. The queue is process-local.
/// Across several server instances, the record version checks remain the safety net.
/// </para>
/// </summary>
public sealed class EndpointWriteQueue
{
    /// <summary>Waiters allowed per record before a call is refused with <c>NETWORK_STORAGE_WRITE_QUEUE_FULL</c>.</summary>
    public const int DefaultMaxQueued = 64;

    /// <summary>How long one call waits for its records before <c>QUEUE_TIMEOUT</c>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly bool _enabled;
    private readonly int _maxQueued;
    private readonly TimeSpan _timeout;
    private readonly Lock _sync = new();
    // A record is present while a call holds it; its list holds the calls waiting behind the holder, in arrival order.
    private readonly Dictionary<RecordKey, LinkedList<Waiter>> _held = new();
    private long _entered;
    private long _enqueued;
    private long _completed;
    private long _rejected;
    private long _timedOut;
    private int _maxObservedQueueDepth;

    public EndpointWriteQueue(bool enabled = true, int maxQueued = DefaultMaxQueued, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueued);
        var wait = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero);
        _enabled = enabled;
        _maxQueued = maxQueued;
        _timeout = wait;
    }

    private int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(_timeout.TotalSeconds));

    /// <summary>
    /// Takes every record in <paramref name="records"/> for <paramref name="projectId"/>, in one canonical order so two
    /// multi-record calls cannot wait on each other in a cycle. Returns null when the queue is disabled or the set is
    /// empty. Throws <see cref="EndpointWriteQueueException"/> when a record's queue is full or the wait times out.
    /// </summary>
    public async Task<RecordLease?> AcquireAsync(
        string projectId,
        IEnumerable<(string Collection, string Key)> records,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(records);
        if (!_enabled) return null;

        var keys = records
            .Distinct()
            .OrderBy(record => record.Collection, StringComparer.Ordinal)
            .ThenBy(record => record.Key, StringComparer.Ordinal)
            .Select(record => new RecordKey(projectId, record.Collection, record.Key))
            .ToList();
        if (keys.Count == 0) return null;

        var held = new List<RecordKey>(keys.Count);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            foreach (var key in keys)
            {
                var waiter = Enter(key);
                if (waiter is not null)
                    await WaitForTurnAsync(key, waiter, ct, deadline.Token);
                held.Add(key);
            }
        }
        catch
        {
            foreach (var key in held)
                Release(key);
            throw;
        }
        return new RecordLease(this, held);
    }

    /// <summary>Counters and live depth for diagnostics. Counters are cumulative since process start.</summary>
    public EndpointWriteQueueStats GetStats()
    {
        lock (_sync)
        {
            var queued = 0;
            foreach (var waiters in _held.Values)
                queued += waiters.Count;
            return new EndpointWriteQueueStats(
                Enabled: _enabled,
                ActiveKeys: _held.Count,
                QueuedRequests: queued,
                MaxQueued: _maxQueued,
                TimeoutMs: (int)_timeout.TotalMilliseconds,
                Counters: new EndpointWriteQueueCounters(_entered, _enqueued, _completed, _rejected, _timedOut, _maxObservedQueueDepth));
        }
    }

    /// <summary>Takes <paramref name="key"/> at once when it is free (returns null); otherwise joins its queue.</summary>
    private Waiter? Enter(RecordKey key)
    {
        lock (_sync)
        {
            _entered++;
            if (!_held.TryGetValue(key, out var waiters))
            {
                _held[key] = new LinkedList<Waiter>();
                return null;
            }
            if (waiters.Count >= _maxQueued)
            {
                _rejected++;
                throw new EndpointWriteQueueException(
                    "NETWORK_STORAGE_WRITE_QUEUE_FULL", "Network Storage write queue is full for this record. Retry shortly.", RetryAfterSeconds);
            }
            var waiter = new Waiter();
            waiter.Node = waiters.AddLast(waiter);
            _enqueued++;
            _maxObservedQueueDepth = Math.Max(_maxObservedQueueDepth, waiters.Count);
            return waiter;
        }
    }

    private async Task WaitForTurnAsync(RecordKey key, Waiter waiter, CancellationToken ct, CancellationToken deadline)
    {
        try
        {
            await waiter.Turn.Task.WaitAsync(deadline);
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                // Handed the record as this wait ended: the call holds it now and releases it with the others.
                if (waiter.Granted) return;
                _held[key].Remove(waiter.Node!);
            }
            ct.ThrowIfCancellationRequested();
            lock (_sync) _timedOut++;
            throw new EndpointWriteQueueException(
                "NETWORK_STORAGE_WRITE_QUEUE_TIMEOUT", "Timed out waiting for Network Storage write queue for this record. Retry shortly.", RetryAfterSeconds);
        }
    }

    /// <summary>Frees <paramref name="key"/>, handing it to the first waiter in line when there is one.</summary>
    private void Release(RecordKey key)
    {
        lock (_sync)
        {
            _completed++;
            var waiters = _held[key];
            var next = waiters.First;
            if (next is null)
            {
                _held.Remove(key);
                return;
            }
            waiters.RemoveFirst();
            next.Value.Granted = true;
            next.Value.Turn.TrySetResult();
        }
    }

    internal readonly record struct RecordKey(string ProjectId, string Collection, string Key);

    private sealed class Waiter
    {
        public readonly TaskCompletionSource Turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public bool Granted;
    }

    /// <summary>The records one call holds. Disposing releases them; a second Dispose does nothing.</summary>
    public sealed class RecordLease : IDisposable
    {
        private EndpointWriteQueue? _queue;
        private readonly IReadOnlyList<RecordKey> _keys;

        internal RecordLease(EndpointWriteQueue queue, IReadOnlyList<RecordKey> keys)
        {
            _queue = queue;
            _keys = keys;
        }

        public void Dispose()
        {
            var queue = Interlocked.Exchange(ref _queue, null);
            if (queue is null) return;
            for (var i = _keys.Count - 1; i >= 0; i--)
                queue.Release(_keys[i]);
        }
    }
}

/// <summary>A write the queue refused or gave up on. Maps to HTTP 429 with <see cref="RetryAfterSeconds"/> as the Retry-After hint.</summary>
public sealed class EndpointWriteQueueException(string code, string message, int retryAfterSeconds) : Exception(message)
{
    /// <summary><c>NETWORK_STORAGE_WRITE_QUEUE_FULL</c> or <c>NETWORK_STORAGE_WRITE_QUEUE_TIMEOUT</c>.</summary>
    public string Code { get; } = code;

    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Point-in-time queue state plus cumulative counters, for diagnostics.</summary>
public sealed record EndpointWriteQueueStats(
    bool Enabled,
    int ActiveKeys,
    int QueuedRequests,
    int MaxQueued,
    int TimeoutMs,
    EndpointWriteQueueCounters Counters);

public sealed record EndpointWriteQueueCounters(
    long Entered,
    long Enqueued,
    long Completed,
    long Rejected,
    long TimedOut,
    int MaxObservedQueueDepth);
