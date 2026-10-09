using Microsoft.Extensions.Caching.Memory;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Counts failed attempts per (client IP, projectId) and blocks the pair once the
/// threshold is reached inside the window. State lives in a size-limited
/// <see cref="IMemoryCache"/> (one slot per pair), so the number of tracked pairs
/// stays bounded no matter how many distinct callers or projects are probed.
/// Callers key on the connection address and project, never on a caller-supplied
/// identity such as a Steam id, so rotating that identity does not reset the count.
/// Instances are meant to be singletons.
/// </summary>
public abstract class FailureThrottle(IMemoryCache cache, TimeProvider time, int threshold, TimeSpan window, TimeSpan blockDuration) : IDisposable
{
    public bool IsBlocked(string clientIp, string projectId)
    {
        if (!cache.TryGetValue(Key(clientIp, projectId), out State? state) || state is null) return false;
        lock (state)
        {
            var elapsed = time.GetUtcNow().ToUnixTimeMilliseconds() - state.WindowStartMs;
            return state.Count >= threshold && elapsed < (long)(window + blockDuration).TotalMilliseconds;
        }
    }

    public void RecordFailure(string clientIp, string projectId)
    {
        var key = Key(clientIp, projectId);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var state = cache.GetOrCreate(key, entry =>
        {
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = window + blockDuration;
            return new State { WindowStartMs = now };
        })!;
        lock (state)
        {
            if (state.Count < threshold && now - state.WindowStartMs > (long)window.TotalMilliseconds)
            {
                state.Count = 0;
                state.WindowStartMs = now;
            }
            state.Count++;
        }
    }

    public void Dispose() => cache.Dispose();

    private static string Key(string clientIp, string projectId) => string.Concat(clientIp, "|", projectId);

    private sealed class State
    {
        public int Count;
        public long WindowStartMs;
    }
}

/// <summary>Failure throttle for s&amp;box auth token verification.</summary>
public sealed class SboxAuthFailureThrottle(IMemoryCache cache, TimeProvider time)
    : FailureThrottle(cache, time, threshold: 10, window: TimeSpan.FromMinutes(1), blockDuration: TimeSpan.FromMinutes(1));

/// <summary>Failure throttle for stats heartbeat requests that present a bad API key.</summary>
public sealed class HeartbeatFailureThrottle(IMemoryCache cache, TimeProvider time)
    : FailureThrottle(cache, time, threshold: 30, window: TimeSpan.FromMinutes(1), blockDuration: TimeSpan.FromMinutes(1));

/// <summary>
/// Limits session.heartbeat analytics events to one per player per interval. The
/// game client heartbeats every ~2 s; flushing an event for each would write ~30
/// events a minute per player. Entries live in a size-limited
/// <see cref="IMemoryCache"/> so the gate stays bounded. Singleton.
/// </summary>
public sealed class HeartbeatAnalyticsGate(IMemoryCache cache) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>True when the caller may flush an event now; the slot is then held for <see cref="Interval"/>.</summary>
    public bool TryAcquire(string projectId, string steamId)
    {
        var key = Key(projectId, steamId);
        if (cache.TryGetValue(key, out _)) return false;
        using var entry = cache.CreateEntry(key);
        entry.Size = 1;
        entry.AbsoluteExpirationRelativeToNow = Interval;
        entry.Value = true;
        return true;
    }

    /// <summary>Frees the slot after a failed flush so the next heartbeat retries.</summary>
    public void Release(string projectId, string steamId) => cache.Remove(Key(projectId, steamId));

    public void Dispose() => cache.Dispose();

    private static string Key(string projectId, string steamId) => string.Concat(projectId, ":", steamId);
}
