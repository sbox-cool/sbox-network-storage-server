using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Infrastructure;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The native endpoint-execution shadow path must surface its failures into the
/// shared error pipeline: the admin dashboard (<c>internal_errors</c>), the
/// per-project Network Storage dashboard (via <c>project_id</c>), and Discord
/// (via the alert sink). These tests assert the captured error carries the
/// project context and tags that make it findable in all three places.
/// </summary>
public sealed class EndpointShadowReporterTests
{
    private const string ProjectId = "6c22075ca036481e";
    private const string Slug = "buy-upgrade";
    private const string SteamId = "76561198021524886";

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (EndpointErrorReporter Reporter, FakeArchive Archive, FakeAlertSink Alerts) Build(TimeProvider? clock = null)
    {
        var archive = new FakeArchive();
        var alerts = new FakeAlertSink();
        var throttle = new EndpointConflictReportThrottle(clock ?? TimeProvider.System);
        var reporter = new EndpointErrorReporter(archive, alerts, throttle, NullLogger<EndpointErrorReporter>.Instance);
        return (reporter, archive, alerts);
    }

    [Fact]
    public async Task CaptureException_ArchivesAndAlertsWithProjectContext()
    {
        var (reporter, archive, alerts) = Build();

        await reporter.CaptureExceptionAsync(ProjectId, Slug, "POST", SteamId,
            new InvalidOperationException("native step blew up"), CancellationToken.None);

        // Reaches the admin dashboard + per-project dashboard (archive) AND Discord (alert sink).
        var captured = Assert.Single(archive.Captured);
        var alerted = Assert.Single(alerts.Notified);
        Assert.Equal(captured.Id, alerted.Id);

        Assert.Equal(ProjectId, captured.ProjectId);          // per-project dashboard filter
        Assert.Equal(SteamId, captured.SteamId);
        Assert.Equal(500, captured.StatusCode);
        Assert.Equal("InvalidOperationException", captured.Classification);
        Assert.Contains("native step blew up", captured.Message);
        Assert.Contains("endpoint-shadow", captured.Tags!);   // filterable
        Assert.Contains($"/v3/endpoints/{ProjectId}/{Slug}", captured.Path);
        Assert.NotNull(captured.StackTrace);
    }

    [Fact]
    public async Task CaptureServerErrorResult_RecordsCodeAndStatus()
    {
        var (reporter, archive, alerts) = Build();

        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
            500, "ENDPOINT_ERROR.WRITE", "write step failed: collection not found", CancellationToken.None);

        var captured = Assert.Single(archive.Captured);
        Assert.Single(alerts.Notified);
        Assert.Equal(500, captured.StatusCode);
        Assert.Equal("EndpointNative.ENDPOINT_ERROR.WRITE", captured.Classification);
        Assert.Equal(ProjectId, captured.ProjectId);
        Assert.Contains("endpoint-shadow", captured.Tags!);
    }

    [Fact]
    public async Task CaptureErrorResult_Reports409ConflictAsCritical()
    {
        var (reporter, archive, alerts) = Build();

        // A 409 from a save guard (SAVE_REGRESSION_BLOCKED / STALE_SAVE) is a
        // data-integrity conflict, not a setup error — it must reach the archive
        // + Discord with the same urgency as a 5xx. This is the "satu" class of
        // failure: a persistent 409 loop that silently strands player progress.
        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
            409, "SAVE_REGRESSION_BLOCKED",
            "Refusing to overwrite totalLevel 81 with lower value 0.", CancellationToken.None);

        var captured = Assert.Single(archive.Captured);
        var alerted = Assert.Single(alerts.Notified);
        Assert.Equal(captured.Id, alerted.Id);
        Assert.Equal(409, captured.StatusCode);
        Assert.Equal("EndpointNative.SAVE_REGRESSION_BLOCKED", captured.Classification);
        Assert.Equal(ProjectId, captured.ProjectId);
        Assert.Equal(SteamId, captured.SteamId);
        Assert.Contains("endpoint-shadow", captured.Tags!);
        Assert.Contains("totalLevel 81", captured.Message);
    }

    [Fact]
    public async Task RepeatedConflict_SuppressedWithinWindow()
    {
        // A needs_review player's autosave loops on the same guard rejection every
        // few seconds. Only the FIRST report may reach /admin/errors + Discord;
        // identical repeats within the window are suppressed so the loop cannot
        // flood the sinks and bury genuinely new errors.
        var (reporter, archive, alerts) = Build();

        for (var i = 0; i < 5; i++)
        {
            await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
                409, "SAVE_REGRESSION_BLOCKED",
                $"Refusing to overwrite totalLevel 81 with lower value {9 + i}.", CancellationToken.None);
        }

        Assert.Single(archive.Captured);
        Assert.Single(alerts.Notified);
    }

    [Fact]
    public async Task RepeatedConflict_ReAlertsAfterWindow()
    {
        // The loop stays visible: once the suppression window elapses, the next
        // occurrence reports again (so a still-broken state is not silenced forever).
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var (reporter, archive, alerts) = Build(clock);

        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
            409, "SAVE_REGRESSION_BLOCKED", "loop", CancellationToken.None);
        clock.Advance(EndpointConflictReportThrottle.Window - TimeSpan.FromSeconds(1));
        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
            409, "SAVE_REGRESSION_BLOCKED", "loop", CancellationToken.None); // still suppressed
        clock.Advance(TimeSpan.FromSeconds(2)); // now past the window
        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", SteamId,
            409, "SAVE_REGRESSION_BLOCKED", "loop", CancellationToken.None);

        Assert.Equal(2, archive.Captured.Count);
        Assert.Equal(2, alerts.Notified.Count);
    }

    [Fact]
    public async Task DistinctConflicts_AreNotSuppressed()
    {
        // Dedup is per (project, player, endpoint, status, code). A different
        // player, endpoint, or error code is a different signal and must still fire.
        var (reporter, archive, alerts) = Build();

        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", "playerA",
            409, "SAVE_REGRESSION_BLOCKED", "a", CancellationToken.None);
        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", "playerB",
            409, "SAVE_REGRESSION_BLOCKED", "b", CancellationToken.None);   // different player
        await reporter.CaptureErrorResultAsync(ProjectId, "other-endpoint", "POST", "playerA",
            409, "SAVE_REGRESSION_BLOCKED", "c", CancellationToken.None);   // different endpoint
        await reporter.CaptureErrorResultAsync(ProjectId, Slug, "POST", "playerA",
            409, "STALE_SAVE", "d", CancellationToken.None);                // different code

        Assert.Equal(4, archive.Captured.Count);
        Assert.Equal(4, alerts.Notified.Count);
    }

    [Fact]
    public async Task RepeatedException_IsNeverThrottled()
    {
        // Unexpected executor exceptions are definite port bugs — they bypass the
        // conflict throttle and always surface, even when identical and repeated.
        var (reporter, archive, alerts) = Build();

        for (var i = 0; i < 3; i++)
        {
            await reporter.CaptureExceptionAsync(ProjectId, Slug, "POST", SteamId,
                new InvalidOperationException("native step blew up"), CancellationToken.None);
        }

        Assert.Equal(3, archive.Captured.Count);
        Assert.Equal(3, alerts.Notified.Count);
    }

    [Fact]
    public async Task AlertFailure_DoesNotPreventArchive()
    {
        var archive = new FakeArchive();
        var alerts = new FakeAlertSink(throwOnNotify: true);
        var reporter = new EndpointErrorReporter(archive, alerts, new EndpointConflictReportThrottle(TimeProvider.System), NullLogger<EndpointErrorReporter>.Instance);

        // A Discord failure must not prevent the error from reaching the dashboards.
        await reporter.CaptureExceptionAsync(ProjectId, Slug, "POST", SteamId,
            new Exception("boom"), CancellationToken.None);

        Assert.Single(archive.Captured);
    }

    [Fact]
    public async Task ArchiveFailure_StillAttemptsAlert()
    {
        var archive = new FakeArchive(throwOnCapture: true);
        var alerts = new FakeAlertSink();
        var reporter = new EndpointErrorReporter(archive, alerts, new EndpointConflictReportThrottle(TimeProvider.System), NullLogger<EndpointErrorReporter>.Instance);

        await reporter.CaptureExceptionAsync(ProjectId, Slug, "POST", SteamId,
            new Exception("boom"), CancellationToken.None);

        // Discord still fires even when the primary archive write fails.
        Assert.Single(alerts.Notified);
    }

    // ── Fakes ──

    private sealed class FakeArchive(bool throwOnCapture = false) : IErrorArchive
    {
        public List<CapturedErrorDto> Captured { get; } = [];

        public Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            if (throwOnCapture) throw new InvalidOperationException("archive down");
            Captured.Add(capturedError);
            return Task.FromResult(capturedError);
        }

        public Task<IReadOnlyList<CapturedErrorDto>> ListRecentAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CapturedErrorDto>>(Captured);

        public Task<InternalErrorArchiveResult> ListAsync(InternalErrorArchiveQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new InternalErrorArchiveResult(Captured, false, "fake"));

        public Task<CapturedErrorDto?> GetAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(Captured.FirstOrDefault(e => e.Id == id));

        public Task<bool> MarkResolvedAsync(string id, long resolvedByUserId, string? note, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> MarkAlertDeliveredAsync(string id, bool delivered, int statusCode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class FakeAlertSink(bool throwOnNotify = false) : IExceptionAlertSink
    {
        public List<CapturedErrorDto> Notified { get; } = [];

        public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            if (throwOnNotify) throw new InvalidOperationException("discord down");
            Notified.Add(capturedError);
            return Task.CompletedTask;
        }
    }
}
