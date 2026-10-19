using System.Collections.Concurrent;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;

/// <summary>Result of <see cref="GameLogRateLimiter.TryConsume"/>. When not allowed, <see cref="RetryAfterSeconds"/> is at least 1.</summary>
public readonly record struct GameLogRateDecision(bool Allowed, int RetryAfterSeconds)
{
    public static readonly GameLogRateDecision Permit = new(true, 0);
}

/// <summary>
/// In-memory token buckets for game-log ingest, one bucket per key. A bucket holds <c>perMinute</c> tokens and
/// refills at <c>perMinute / 60</c> tokens per second. A request costs one token per entry, so a batch is
/// rejected as a whole, never partially. Buckets are per process.
/// </summary>
public sealed class GameLogRateLimiter(TimeProvider? time = null)
{
    /// <summary>Above this many buckets, full (idle) buckets are pruned so per-actor keys cannot grow without bound.</summary>
    private const int PruneThreshold = 10_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public GameLogRateDecision TryConsume(string key, int perMinute, int cost)
    {
        var now = _time.GetTimestamp();
        var frequency = _time.TimestampFrequency;
        if (_buckets.Count > PruneThreshold) Prune(now, frequency, perMinute);
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(perMinute, now));
        lock (bucket)
        {
            bucket.Refill(now, frequency, perMinute);
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

    /// <summary>Number of live buckets; for tests and diagnostics.</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>Removes buckets that have refilled to capacity: forgetting them is equivalent to keeping them.</summary>
    private void Prune(long now, long frequency, int perMinute)
    {
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                bucket.Refill(now, frequency, perMinute);
                if (bucket.Tokens >= perMinute) _buckets.TryRemove(key, out _);
            }
        }
    }

    private sealed class Bucket(double tokens, long timestamp)
    {
        private long _last = timestamp;

        public double Tokens { get; set; } = tokens;

        public void Refill(long now, long frequency, int perMinute)
        {
            var elapsedSeconds = (now - _last) / (double)frequency;
            _last = now;
            Tokens = Math.Min(perMinute, Tokens + elapsedSeconds * perMinute / 60.0);
        }
    }
}
