using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Singleton transition-based failure tracker for <see cref="PlayerAnalyticsIngester"/>.
///
/// The ingester is scoped (one instance per request), so per-instance
/// consecutive-failure counters would reset on every request and never detect a
/// sustained failure. This singleton persists across the process lifetime.
///
/// The alert fires exactly once on the transition into a sustained-failure state
/// (≥ <see cref="FailureThreshold"/> consecutive failures), never per-event.
/// Recovery is logged when ingestion succeeds again.
/// </summary>
public sealed class AnalyticsIngestionFailureTracker(ILogger<AnalyticsIngestionFailureTracker> logger)
{
    /// <summary>Number of consecutive failures before a transition alert fires.</summary>
    public const int FailureThreshold = 5;

    private int _consecutiveFailures;
    private bool _alertActive;

    /// <summary>
    /// Record a failure. Returns <c>true</c> when this failure crosses the
    /// threshold and a transition alert should be fired (at most once per
    /// failure state).
    /// </summary>
    public bool RecordFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count == FailureThreshold && !_alertActive)
        {
            _alertActive = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Record a success. Returns <c>true</c> when the ingester was in a
    /// sustained-failure state and has now recovered.
    /// </summary>
    public bool RecordSuccess()
    {
        var wasFailing = _alertActive;
        _consecutiveFailures = 0;
        _alertActive = false;
        return wasFailing;
    }

    /// <summary>Log the transition into a sustained-failure state.</summary>
    public Task FireTransitionAlertAsync(string projectId, int consecutiveFailures, CancellationToken ct)
    {
        logger.LogError(
            "Analytics ingestion has failed {ConsecutiveFailures} consecutive times for project {ProjectId}; analytics data may be stale",
            consecutiveFailures,
            projectId);
        return Task.CompletedTask;
    }

    /// <summary>Current consecutive failure count (for diagnostics/testing).</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>Whether a sustained-failure alert is currently active.</summary>
    public bool AlertActive => _alertActive;
}
