using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Infrastructure;

/// <summary>
/// Surfaces failures from the native .NET endpoint-execution shadow path into the
/// shared error pipeline so migration bugs are visible everywhere the team looks:
/// <list type="bullet">
/// <item>the admin error dashboard (<c>/admin/errors</c>) via <c>internal_errors</c>,</item>
/// <item>the per-project Network Storage dashboard (queries <c>internal_errors WHERE project_id</c>),</item>
/// <item>Discord/SMTP operator alerts (<c>alerts.toml</c>) via <see cref="IExceptionAlertSink"/>.</item>
/// </list>
///
/// Without this, native-executor exceptions, 5xx results, and 409 data-integrity
/// conflicts (e.g. <c>SAVE_REGRESSION_BLOCKED</c> / <c>STALE_SAVE</c>) were
/// swallowed at <c>LogDebug</c> — invisible while we shadow-verify the ScyllaDB
/// cutover. A 409 conflict is a logical/data-level failure, not a setup or
/// client-input error, and is reported with the same urgency as a 5xx: a
/// persistent 409 loop silently strands player progress. The captured error
/// carries the <c>project_id</c> so it appears on the owning project's
/// dashboard, and the <c>endpoint-shadow</c> tag so it is filterable.
/// </summary>
public sealed class EndpointShadowReporter(
    IErrorArchive errorArchive,
    IExceptionAlertSink alertSink,
    EndpointConflictReportThrottle conflictThrottle,
    ILogger<EndpointShadowReporter> logger)
{
    private static readonly string[] ShadowTags = ["dotnet", "endpoint-shadow"];

    /// <summary>
    /// Capture an unexpected exception thrown by the native endpoint executor.
    /// Definitely a port bug — always surfaced.
    /// </summary>
    public Task CaptureExceptionAsync(string projectId, string endpointSlug, string method, string? steamId, Exception exception, CancellationToken ct)
        => CaptureAsync(projectId, endpointSlug, method, steamId, 500, exception.GetType().Name, exception.Message, exception.ToString(), ct);

    /// <summary>
    /// Capture a native execution result that returned an error status the team
    /// must see: a server error (5xx — a port gap where the authoritative Bun path
    /// may have succeeded) or a data-integrity conflict (409 — e.g.
    /// <c>SAVE_REGRESSION_BLOCKED</c> / <c>STALE_SAVE</c>, where a player's save
    /// is being rejected by a guard). A 409 conflict is a logical, data-level
    /// failure — not a setup or client-input error — and is treated as critical:
    /// it reaches <c>/admin/errors</c>, the per-project Network Storage dashboard,
    /// and Discord with the same visibility as a 5xx.
    ///
    /// Repeated identical conflicts are throttled (see
    /// <see cref="EndpointConflictReportThrottle"/>): the first occurrence fires,
    /// then duplicates for the same (project, player, endpoint, status, code) are
    /// suppressed for a window so a persistent loop (e.g. a needs_review player's
    /// autosave) cannot flood <c>/admin/errors</c> + Discord with one entry per
    /// save and bury genuinely new errors.
    /// </summary>
    public Task CaptureErrorResultAsync(string projectId, string endpointSlug, string method, string? steamId, int statusCode, string errorCode, string message, CancellationToken ct)
    {
        var fingerprint = $"{projectId}|{steamId}|{endpointSlug}|{statusCode}|{errorCode}";
        if (!conflictThrottle.ShouldReport(fingerprint))
        {
            logger.LogDebug(
                "Suppressed duplicate native endpoint conflict report {Classification} ({Status}) for {ProjectId}/{Slug} player {SteamId} (throttled within {WindowMinutes}m)",
                errorCode, statusCode, projectId, endpointSlug, steamId, EndpointConflictReportThrottle.Window.TotalMinutes);
            return Task.CompletedTask;
        }
        return CaptureAsync(projectId, endpointSlug, method, steamId, statusCode, $"EndpointNative.{errorCode}", message, stackTrace: null, ct);
    }

    private async Task CaptureAsync(
        string projectId, string endpointSlug, string method, string? steamId,
        int statusCode, string classification, string message, string? stackTrace, CancellationToken ct)
    {
        var captured = new CapturedErrorDto(
            Id: Guid.NewGuid().ToString("D"),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "Network Storage endpoint (native shadow)",
            Method: string.IsNullOrEmpty(method) ? "POST" : method,
            Path: $"/v3/endpoints/{projectId}/{endpointSlug}",
            StatusCode: statusCode,
            Classification: classification,
            CorrelationId: Guid.NewGuid().ToString("D"),
            Message: string.IsNullOrEmpty(message) ? classification : message,
            StackTrace: stackTrace,
            ProjectId: string.IsNullOrEmpty(projectId) ? null : projectId,
            SteamId: string.IsNullOrEmpty(steamId) ? null : steamId,
            Tags: ShadowTags);

        // Archive first so the error reaches /admin/errors and the per-project
        // Network Storage dashboard even when external alerting is suppressed
        // (NoopExceptionAlertSink in dev/test). The Postgres INSERT is
        // ON CONFLICT (id) DO NOTHING, so the duplicate capture inside the
        // Discord sink is a no-op.
        try
        {
            await errorArchive.CaptureAsync(captured, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to archive native endpoint shadow error for {ProjectId}/{Slug}", projectId, endpointSlug);
        }

        try
        {
            await alertSink.NotifyAsync(captured, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send error alert for native endpoint shadow error {ProjectId}/{Slug}", projectId, endpointSlug);
        }
    }
}
