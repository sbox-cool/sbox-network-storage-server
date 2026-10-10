using System.Collections.Concurrent;
using System.Globalization;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

/// <summary>
/// Query results per <c>(project, query)</c>, shared by every request of one server, with single-flight
/// execution: concurrent misses for the same query wait for one run instead of each scanning the source
/// collections.
///
/// <para>A result is reused while it is younger than the query's <c>cache.ttlSeconds</c>, but never for less
/// than <see cref="MinimumLifetime"/>. Record writes in the project (<see cref="DataChanged"/>) end that
/// reuse early, once the result is older than <see cref="MinimumLifetime"/>, so players see their own writes
/// within a couple of seconds while a flood of reads still runs the query at most once per that interval.
/// Definition changes (<see cref="DefinitionsChanged"/>: queries, collections, game values) stop every result
/// of the project from being reused.</para>
/// </summary>
public sealed class QueryResultCache(TimeProvider time)
{
    /// <summary>Shortest reuse of a result, even for a TTL of 0 or right after a write.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<(Entry? Entry, bool Reused)>>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectVersions> _versions = new(StringComparer.Ordinal);

    public QueryResultCache() : this(TimeProvider.System)
    {
    }

    /// <summary>A record of the project changed: results older than <see cref="MinimumLifetime"/> run again.</summary>
    public void DataChanged(string projectId)
        => _versions.AddOrUpdate(projectId, static _ => new ProjectVersions(1, 0), static (_, v) => v with { Data = v.Data + 1 });

    /// <summary>A query, collection or game-value definition of the project changed: no result is reused.</summary>
    public void DefinitionsChanged(string projectId)
        => _versions.AddOrUpdate(projectId, static _ => new ProjectVersions(0, 1), static (_, v) => v with { Definitions = v.Definitions + 1 });

    /// <summary>
    /// Returns a reusable result (<see cref="QueryResult.FromCache"/> set), or runs <paramref name="execute"/>
    /// once for all concurrent callers and keeps its result. The run is not tied to any one caller's
    /// cancellation, so a caller that gives up does not fail the others; each caller stops waiting on its own
    /// <paramref name="ct"/>. Every caller gets its own copy of the result fields it may set.
    /// </summary>
    public async Task<QueryResult?> GetOrRunAsync(string projectId, string queryId, int ttlSeconds,
        Func<Task<QueryResult?>> execute, CancellationToken ct)
    {
        var key = projectId + "\n" + queryId;
        if (Fresh(key, projectId) is { } hit) return hit.Copy(fromCache: true);

        var run = _running.GetOrAdd(key, _ => new Lazy<Task<(Entry?, bool)>>(() => RunAsync(key, projectId, ttlSeconds, execute)));
        var (entry, reused) = await run.Value.WaitAsync(ct).ConfigureAwait(false);
        return entry?.Copy(fromCache: reused);
    }

    private async Task<(Entry?, bool)> RunAsync(string key, string projectId, int ttlSeconds, Func<Task<QueryResult?>> execute)
    {
        try
        {
            // Another run may have finished between this caller's miss and its GetOrAdd.
            if (Fresh(key, projectId) is { } hit) return (hit, true);

            // Versions are read before the scan, so a write that lands mid-run makes this result stale.
            var versions = _versions.GetValueOrDefault(projectId);
            var result = await execute().ConfigureAwait(false);
            if (result is null) return (null, false);
            if (result.Type == "error") return (new Entry(result, null, versions), false);

            var now = time.GetUtcNow();
            var ttl = TimeSpan.FromSeconds(Math.Max(ttlSeconds, 0));
            var entry = new Entry(result, new Lifetime(now, now + (ttl > MinimumLifetime ? ttl : MinimumLifetime)), versions);
            _entries[key] = entry;
            return (entry, false);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private Entry? Fresh(string key, string projectId)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.Kept is not { } kept) return null;
        var now = time.GetUtcNow();
        var versions = _versions.GetValueOrDefault(projectId);
        var fresh = entry.Versions.Definitions == versions.Definitions
            && now < kept.ExpiresAt
            && (entry.Versions.Data == versions.Data || now - kept.CachedAt < MinimumLifetime);
        return fresh ? entry : null;
    }

    private readonly record struct ProjectVersions(long Data, long Definitions);

    private sealed record Lifetime(DateTimeOffset CachedAt, DateTimeOffset ExpiresAt);

    /// <summary>A run's result; <see cref="Kept"/> is null for error results, which are never reused.</summary>
    private sealed record Entry(QueryResult Result, Lifetime? Kept, ProjectVersions Versions)
    {
        public QueryResult Copy(bool fromCache)
        {
            var copy = Result.Clone();
            copy.FromCache = fromCache;
            copy.CachedAt = fromCache ? Iso(Kept!.CachedAt) : null;
            copy.ExpiresAt = fromCache ? Iso(Kept!.ExpiresAt) : null;
            return copy;
        }

        private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
