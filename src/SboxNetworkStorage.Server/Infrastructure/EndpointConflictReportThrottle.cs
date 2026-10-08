using System.Collections.Concurrent;

namespace SboxNetworkStorage.Server.Infrastructure;

/// <summary>
/// Best-effort, time-windowed dedup gate for repeated native endpoint error
/// reports (<see cref="EndpointShadowReporter.CaptureErrorResultAsync"/>).
///
/// A persistent data-integrity conflict — most notably a <c>needs_review</c>
/// player's autosave looping on the anti-rollback guard
/// (<c>SAVE_REGRESSION_BLOCKED</c>: e.g. the "satu" player whose unbacked
/// <c>players.totalLevel</c> header rejects every recomputed save) — fires a
/// fresh 409 on EVERY autosave, every few seconds. Without throttling, the
/// reporter writes one <c>/admin/errors</c> row and one Discord alert per save,
/// flooding both sinks and burying genuinely new errors. That defeats the
/// visibility the 409 reporting was added for (commit 289ef7d6).
///
/// This gate reports the FIRST occurrence of a fingerprint, then suppresses
/// identical reports for <see cref="Window"/> so the issue stays visible
/// (re-alerts once per window) without flooding. The dedup is per-process; the
/// small worker fleet multiplies the rate by the worker count, which is still
/// bounded. Unexpected executor exceptions are NOT routed through this gate —
/// they are definite port bugs and must always surface.
/// </summary>
public sealed class EndpointConflictReportThrottle(TimeProvider timeProvider)
{
    /// <summary>How long an identical report is suppressed after it last fired.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    // Defensive cap: a stuck-player set is tiny, but the map must never grow
    // unbounded across the process lifetime. Pruned lazily once exceeded.
    private const int MaxTrackedFingerprints = 8192;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="fingerprint"/> should be reported
    /// now — first sight, or the window since its last report has elapsed — and
    /// records the report time. Returns <c>false</c> to suppress a duplicate
    /// inside the window. Thread-safe and best-effort: under contention a handful
    /// of extra reports may slip through, never fewer.
    /// </summary>
    public bool ShouldReport(string fingerprint)
    {
        var now = timeProvider.GetUtcNow();
        var report = false;
        _lastReported.AddOrUpdate(
            fingerprint,
            _ =>
            {
                report = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last >= Window)
                {
                    report = true;
                    return now;
                }
                report = false;
                return last;
            });

        if (_lastReported.Count > MaxTrackedFingerprints)
            Prune(now);

        return report;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, last) in _lastReported)
        {
            if (now - last >= Window)
                _lastReported.TryRemove(key, out _);
        }
    }
}
