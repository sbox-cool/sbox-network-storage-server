using System.Collections.Concurrent;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;

/// <summary>Result of <see cref="GameLogRateLimiter.TryConsume"/>. When not allowed, <see cref="RetryAfterSeconds"/> is at least 1.</summary>
public readonly record struct GameLogRateDecision(bool Allowed, int RetryAfterSeconds)
{
    public static readonly GameLogRateDecision Permit = new(true, 0);
}

/// <summary>
/// In-memory token buckets for game-log ingest, one bucket per key. A bucket holds its own capacity (the
/// <c>perMinute</c> of the caller that last used it) and refills at <c>capacity / 60</c> tokens per second. A request
/// costs one token per billable entry, so a batch is rejected as a whole, never partially. Buckets are per process.
/// </summary>
public sealed class GameLogRateLimiter(TimeProvider? time = null)
{
    /// <summary>Above this many buckets, full (idle) buckets are pruned so per-actor keys cannot grow without bound.</summary>
    private const int PruneThreshold = 10_000;

    /// <summary>Pruning runs at most once per interval, however many requests arrive while the table is large.</summary>
    private const int PruneIntervalSeconds = 60;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _nextPruneAt;

    public GameLogRateDecision TryConsume(string key, int perMinute, int cost)
    {
        var now = _time.GetTimestamp();
        var frequency = _time.TimestampFrequency;
        if (_buckets.Count > PruneThreshold) MaybePrune(now, frequency);

        while (true)
        {
            var bucket = _buckets.GetOrAdd(key, _ => new Bucket(perMinute, now));
            lock (bucket)
            {
                // A bucket pruned between lookup and lock is gone from the table; look it up again.
                if (bucket.Retired) continue;

                bucket.Capacity = perMinute;
                bucket.Refill(now, frequency);
                if (bucket.Tokens >= cost)
                {
                    bucket.Tokens -= cost;
                    return GameLogRateDecision.Permit;
                }

                var missing = cost - bucket.Tokens;
                var seconds = (int)Math.Ceiling(missing * 60.0 / perMinute);
                return new GameLogRateDecision(false, Math.Max(1, seconds));
            }
        }
    }

    /// <summary>Number of live buckets; for tests and diagnostics.</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>
    /// Runs <see cref="Prune"/> if the interval has elapsed. Only one caller wins the compare-and-swap per interval.
    /// </summary>
    private void MaybePrune(long now, long frequency)
    {
        var due = Interlocked.Read(ref _nextPruneAt);
        if (now < due) return;
        if (Interlocked.CompareExchange(ref _nextPruneAt, now + frequency * PruneIntervalSeconds, due) != due) return;
        Prune(now, frequency);
    }

    /// <summary>
    /// Removes buckets that have refilled to their own capacity: forgetting them is equivalent to keeping them.
    /// Each bucket is judged against its own capacity, so a large project bucket is never dropped because a
    /// smaller per-actor limit ran the prune.
    /// </summary>
    private void Prune(long now, long frequency)
    {
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                bucket.Refill(now, frequency);
                if (bucket.Tokens >= bucket.Capacity && _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket)))
                {
                    bucket.Retired = true;
                }
            }
        }
    }

    private sealed class Bucket(double tokens, int capacity, long timestamp)
    {
        private long _last = timestamp;

        public Bucket(int capacity, long timestamp) : this(capacity, capacity, timestamp) { }

        public double Tokens { get; set; } = tokens;

        public int Capacity { get; set; } = capacity;

        public bool Retired { get; set; }

        public void Refill(long now, long frequency)
        {
            var elapsedSeconds = (now - _last) / (double)frequency;
            _last = now;
            Tokens = Math.Min(Capacity, Tokens + elapsedSeconds * Capacity / 60.0);
        }
    }
}
