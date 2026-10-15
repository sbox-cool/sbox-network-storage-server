using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Bounded, best-effort per-hour request outcomes by library, for the post-update regression watch. Only requests
/// that authenticated to a project are counted (the caller of <see cref="Record"/> decides). Request threads never
/// touch the database; <see cref="ClientVersionWriter"/> drains this once a minute.
/// </summary>
public sealed class ClientOutcomeTracker(TimeProvider time, string serverVersion)
{
    /// <summary>Same fairness rule as <see cref="ClientVersionTracker"/>: one project cannot starve the others.</summary>
    public const int MaxGroupsPerProject = 500;
    /// <summary>Global memory ceiling on buffered groups.</summary>
    public const int MaxTotalGroups = 2000;
    /// <summary>Rows older than this are purged: the regression baseline reads seven days.</summary>
    public const int RetentionHours = 14 * 24;

    private readonly Lock _gate = new();
    private Dictionary<ClientOutcomeKey, Counter> _pending = new();
    private readonly Dictionary<string, int> _pendingByProject = new(StringComparer.Ordinal);
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Stamped into every row: a verdict compares this binary's traffic with the previous binary's.</summary>
    public string ServerVersion { get; } = serverVersion;

    public static long HourOf(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds() / 3_600_000;

    /// <summary>
    /// Counts one authenticated request. 5xx is a server error; 4xx other than 401, 403, 409 and 429 is a rejection
    /// (credentials, conflicts and rate limits are not library defects).
    /// </summary>
    public void Record(string projectId, ClientIdentity identity, int status)
    {
        if (projectId.Length is < 1 or > 128) return;
        var key = new ClientOutcomeKey(projectId, HourOf(time.GetUtcNow()), ServerVersion, identity.LibraryVersion,
            identity.LibraryRevision, identity.ClientType ?? "unknown");
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var counter))
            {
                _pendingByProject.TryGetValue(projectId, out var projectPending);
                if (_pending.Count >= MaxTotalGroups || projectPending >= MaxGroupsPerProject)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                _pending[key] = counter = new Counter();
                _pendingByProject[projectId] = projectPending + 1;
            }
            counter.Requests++;
            if (status >= StatusCodes.Status500InternalServerError) counter.ServerErrors++;
            else if (status >= StatusCodes.Status400BadRequest && status is not (StatusCodes.Status401Unauthorized
                or StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict or StatusCodes.Status429TooManyRequests))
                counter.Rejected++;
        }
    }

    /// <summary>Removes and returns every pending group. Called from one flush at a time.</summary>
    public IReadOnlyList<ClientOutcomeDelta> Drain()
    {
        lock (_gate)
        {
            var drained = _pending.Select(pair => new ClientOutcomeDelta(pair.Key, pair.Value.Requests, pair.Value.Rejected, pair.Value.ServerErrors)).ToArray();
            _pending = new();
            _pendingByProject.Clear();
            return drained;
        }
    }

    private sealed class Counter
    {
        public long Requests;
        public long Rejected;
        public long ServerErrors;
    }
}
