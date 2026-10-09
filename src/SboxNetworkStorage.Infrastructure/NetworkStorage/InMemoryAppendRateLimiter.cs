using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// In-memory implementation of <see cref="IAppendRateLimiter"/>.
/// Counters are per-process and reset at midnight UTC (the bucket string includes
/// today's date). This matches the retired legacy server runtime's behavior closely
/// enough for the append carve-over; a distributed the store-backed limiter can
/// replace it when strict cross-node {@literal >}consistency is required.
/// </summary>
public sealed class InMemoryAppendRateLimiter(TimeProvider? time = null) : IAppendRateLimiter
{
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _expiryGate = new();
    private string? _currentDay;

    /// <summary>Number of live counters; only the current UTC day's survive.</summary>
    public int BucketCount => _counters.Count;

    public Task<AppendRateLimitResult> CheckAsync(
        string projectId,
        string collectionId,
        string mode,
        int savesPerDay,
        string writerId,
        CancellationToken cancellationToken)
    {
        // Serialize the rollover with increments: an in-flight previous-day request cannot recreate an
        // expired bucket after the purge has completed.
        lock (_expiryGate)
        {
        var today = _time.GetUtcNow().ToString("yyyy-MM-dd");
        ExpireOtherDays(today);
        if (savesPerDay <= 0)
        {
            return Task.FromResult(AppendRateLimitResult.AllowedResult);
        }

        var scope = mode == "collection" || string.IsNullOrEmpty(writerId)
            ? $"c:{projectId}:{collectionId}:{today}"
            : $"p:{projectId}:{collectionId}:{writerId}:{today}";

        var count = _counters.AddOrUpdate(scope, static _ => 1, static (_, current) => current + 1);
        if (count > savesPerDay)
        {
            var perHour = Math.Round(savesPerDay / 24.0);
            var perMinute = (savesPerDay / 1440.0).ToString("F1");
            var target = mode == "collection" ? "this collection" : "this player";
            return Task.FromResult(new AppendRateLimitResult(
                false,
                "RATE_LIMIT_DAILY",
                $"Rate limit exceeded for {target}: max {savesPerDay} saves per day (~{perHour}/hr, ~{perMinute}/min). Resets at midnight UTC."));
        }

        return Task.FromResult(AppendRateLimitResult.AllowedResult);
        }
    }

    /// <summary>Counters of earlier days can never be hit again; drop them when the day rolls over.</summary>
    private void ExpireOtherDays(string today)
    {
        if (Volatile.Read(ref _currentDay) == today) return;
        lock (_expiryGate)
        {
            if (_currentDay == today) return;
            var suffix = ":" + today;
            foreach (var key in _counters.Keys)
            {
                if (!key.EndsWith(suffix, StringComparison.Ordinal)) _counters.TryRemove(key, out _);
            }
            Volatile.Write(ref _currentDay, today);
        }
    }
}
