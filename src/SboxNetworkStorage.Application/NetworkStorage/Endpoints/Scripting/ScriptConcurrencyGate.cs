using System.Diagnostics;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Bounds how many scripted requests run at once, server-wide and per project. A call that finds its project or the
/// server full waits for a slot for up to the wait time, then is refused: the caller answers 429 <c>SCRIPT_BUSY</c>
/// with <c>Retry-After</c> and <c>retrySafe</c>, and the request never runs. A waiting call takes no server slot until
/// its project admits it, so a project at its own limit does not hold back the others. Register one instance as a
/// singleton; a per-request instance would limit nothing.
/// </summary>
public sealed class ScriptConcurrencyGate
{
    /// <summary>Concurrent scripted requests allowed for one project.</summary>
    public const int DefaultPerProjectLimit = 2;

    /// <summary>How long a scripted request waits for a slot before it is refused.</summary>
    public static readonly TimeSpan DefaultWaitTime = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _global;
    private readonly int _globalLimit;
    private readonly int _perProjectLimit;
    private readonly TimeSpan _waitTime;
    private readonly Lock _sync = new();

    // A lane exists while a call holds or waits for a slot in its project. Refs counts those calls, under _sync.
    private readonly Dictionary<string, ProjectLane> _lanes = new(StringComparer.Ordinal);
    private long _entered;
    private long _waited;
    private long _timedOut;
    private int _waiting;
    private int _maxObservedWaiting;

    /// <summary>One slot per processor server-wide, with <see cref="DefaultPerProjectLimit"/> per project.</summary>
    public ScriptConcurrencyGate()
        : this(Environment.ProcessorCount, DefaultPerProjectLimit)
    {
    }

    public ScriptConcurrencyGate(int globalLimit, int perProjectLimit, TimeSpan? waitTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(globalLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perProjectLimit, 1);
        var wait = waitTime ?? DefaultWaitTime;
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        _globalLimit = globalLimit;
        _global = new SemaphoreSlim(globalLimit, globalLimit);
        _perProjectLimit = perProjectLimit;
        _waitTime = wait;
    }

    /// <summary>Seconds a refused caller should wait before retrying: the wait time rounded up, at least 1.</summary>
    public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(_waitTime.TotalSeconds));

    /// <summary>
    /// Takes a slot for <paramref name="projectId"/>. Returns at once when one is free; otherwise waits up to the wait
    /// time and returns null if none frees. Cancelling the wait gives the caller's place back and throws.
    /// </summary>
    public async Task<ScriptSlot?> EnterAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        var lane = RetainLane(projectId);
        var started = Stopwatch.GetTimestamp();
        var laneHeld = false;
        var serverHeld = false;
        var handedOff = false;
        try
        {
            // Project first, then server: a call queued behind its own project never holds a server slot.
            var laneFast = lane.Slots.Wait(0);
            laneHeld = laneFast || await WaitForSlotAsync(lane.Slots, started, ct);
            var serverFast = laneHeld && _global.Wait(0);
            serverHeld = serverFast || (laneHeld && await WaitForSlotAsync(_global, started, ct));
            if (!serverHeld)
            {
                lock (_sync) _timedOut++;
                return null;
            }

            if (!laneFast || !serverFast)
            {
                lock (_sync) _waited++;
            }

            handedOff = true;
            return new ScriptSlot(this, lane);
        }
        finally
        {
            if (!handedOff)
            {
                if (serverHeld) _global.Release();
                if (laneHeld) lane.Slots.Release();
                ReleaseLane(lane);
            }
        }
    }

    /// <summary>Point-in-time gate state plus cumulative counters, for diagnostics.</summary>
    public ScriptGateStats GetStats()
    {
        lock (_sync)
        {
            return new ScriptGateStats(
                GlobalLimit: _globalLimit,
                PerProjectLimit: _perProjectLimit,
                WaitMs: (int)_waitTime.TotalMilliseconds,
                ActiveSlots: _globalLimit - _global.CurrentCount,
                QueuedRequests: _waiting,
                Counters: new ScriptGateCounters(_entered, _waited, _timedOut, _maxObservedWaiting));
        }
    }

    private async Task<bool> WaitForSlotAsync(SemaphoreSlim slots, long started, CancellationToken ct)
    {
        lock (_sync)
        {
            _waiting++;
            if (_waiting > _maxObservedWaiting) _maxObservedWaiting = _waiting;
        }

        try
        {
            return await slots.WaitAsync(RemainingWait(started), ct);
        }
        finally
        {
            lock (_sync) _waiting--;
        }
    }

    private TimeSpan RemainingWait(long started)
    {
        var left = _waitTime - Stopwatch.GetElapsedTime(started);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private ProjectLane RetainLane(string projectId)
    {
        lock (_sync)
        {
            _entered++;
            if (!_lanes.TryGetValue(projectId, out var lane))
            {
                lane = new ProjectLane(projectId, _perProjectLimit);
                _lanes[projectId] = lane;
            }

            lane.Refs++;
            return lane;
        }
    }

    private void ReleaseLane(ProjectLane lane)
    {
        lock (_sync)
        {
            if (--lane.Refs == 0)
                _lanes.Remove(lane.ProjectId);
        }
    }

    private void ReleaseSlot(ProjectLane lane)
    {
        _global.Release();
        lane.Slots.Release();
        ReleaseLane(lane);
    }

    internal sealed class ProjectLane(string projectId, int limit)
    {
        public string ProjectId { get; } = projectId;
        public SemaphoreSlim Slots { get; } = new(limit, limit);
        public int Refs { get; set; }
    }

    /// <summary>A held slot. Disposing it releases the slot; a second Dispose does nothing.</summary>
    public sealed class ScriptSlot : IDisposable
    {
        private ScriptConcurrencyGate? _gate;
        private readonly ProjectLane _lane;

        internal ScriptSlot(ScriptConcurrencyGate gate, ProjectLane lane)
        {
            _gate = gate;
            _lane = lane;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.ReleaseSlot(_lane);
        }
    }
}

/// <summary>Point-in-time gate state plus cumulative counters, for diagnostics.</summary>
public sealed record ScriptGateStats(
    int GlobalLimit,
    int PerProjectLimit,
    int WaitMs,
    int ActiveSlots,
    int QueuedRequests,
    ScriptGateCounters Counters);

public sealed record ScriptGateCounters(long Entered, long Waited, long TimedOut, int MaxObservedQueueDepth);
