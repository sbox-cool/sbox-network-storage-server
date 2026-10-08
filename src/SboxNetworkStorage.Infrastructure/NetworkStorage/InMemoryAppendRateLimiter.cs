using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// In-memory implementation of <see cref="IAppendRateLimiter"/>.
/// Counters are per-process and reset at midnight UTC (the bucket string includes
/// today's date). This matches the retired Bun runtime's behavior closely
/// enough for the append carve-over; a distributed ScyllaDB-backed limiter can
/// replace it when strict cross-node {@literal >}consistency is required.
/// </summary>
public sealed class InMemoryAppendRateLimiter : IAppendRateLimiter
{
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);

    public Task<AppendRateLimitResult> CheckAsync(
        string projectId,
        string collectionId,
        string mode,
        int savesPerDay,
        string writerId,
        CancellationToken cancellationToken)
    {
        if (savesPerDay <= 0)
        {
            return Task.FromResult(AppendRateLimitResult.AllowedResult);
        }

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
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
