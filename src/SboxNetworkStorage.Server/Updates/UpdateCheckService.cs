using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>Latest update check result, surfaced by <c>/v3/server-info</c> and the server log.</summary>
public sealed class UpdateNoticeState
{
    private volatile UpdateNotice? _notice;

    public UpdateNotice? Current => _notice;

    public void Set(UpdateNotice notice) => _notice = notice;
}

public sealed record UpdateNotice(string CurrentVersion, ReleaseInfo Latest, bool UpdateAvailable, DateTimeOffset CheckedAt);

/// <summary>
/// Checks for a newer release every <c>updates.interval_hours</c> and logs a notice.
/// It never downloads or installs anything: operators run <c>sbox-ns update</c>.
/// </summary>
public sealed class UpdateCheckService(
    EffectiveConfig config,
    UpdateNoticeState state,
    LibraryCompatProvider libraryCompat,
    ILogger<UpdateCheckService> logger) : BackgroundService
{
    /// <summary>
    /// Consecutive checks without an applicable overlay (no <c>libraryCompat</c> in the signed
    /// feed, an unsigned fallback, or a failed check) before the current overlay is cleared.
    /// A stale overlay must not tighten statuses forever after the feed has gone quiet.
    /// </summary>
    public const int OverlayExpiryMisses = 3;

    private int _overlayMisses;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetBoolean("updates.check"))
        {
            logger.LogInformation("Update checks are disabled (updates.check = false)");
            return;
        }

        using var http = ReleaseFeed.CreateHttpClient();
        var feed = new ReleaseFeed(http, config);
        var interval = TimeSpan.FromHours(config.GetInteger("updates.interval_hours"));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckOnceAsync(feed, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task CheckOnceAsync(ReleaseFeed feed, CancellationToken ct)
    {
        try
        {
            var (latest, fromSignedFeed) = await feed.GetReleaseWithSourceAsync(specificVersion: null, ct);
            var notice = Evaluate(BuildInfo.Version, latest, DateTimeOffset.UtcNow);
            state.Set(notice);
            // Overlays ride the signed feed only: the unsigned GitHub release.json fallback says
            // nothing about library policy, even when it carries a libraryCompat object.
            if (fromSignedFeed && latest.LibraryCompat is { IsEmpty: false })
            {
                libraryCompat.SetOverlay(latest.LibraryCompat);
                _overlayMisses = 0;
                logger.LogWarning("Applied the library compatibility overlay from release {Version}.", latest.Version);
            }
            else if (fromSignedFeed && latest.LibraryCompat is not null)
            {
                // An explicit empty object withdraws the overlay; it is not a missed check.
                _overlayMisses = 0;
                libraryCompat.SetOverlay(null);
                logger.LogWarning("Cleared the library compatibility overlay: release {Version} withdrew it.", latest.Version);
            }
            else
            {
                NoteOverlayMiss();
            }
            if (!notice.UpdateAvailable)
            {
                return;
            }

            if (latest.Security)
            {
                logger.LogWarning(
                    "SECURITY UPDATE: sbox-ns {Latest} is available (running {Current}). Run `sbox-ns update` as soon as possible. Changelog: {Changelog}",
                    latest.Version, notice.CurrentVersion, latest.ChangelogUrl);
            }
            else
            {
                logger.LogInformation(
                    "sbox-ns {Latest} is available (running {Current}). Run `sbox-ns update` when convenient. Changelog: {Changelog}",
                    latest.Version, notice.CurrentVersion, latest.ChangelogUrl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Update check failed");
            NoteOverlayMiss();
        }
    }

    private void NoteOverlayMiss()
    {
        if (++_overlayMisses < OverlayExpiryMisses) return;
        _overlayMisses = 0;
        libraryCompat.SetOverlay(null);
        logger.LogWarning(
            "Cleared the library compatibility overlay after {Misses} consecutive update checks without one.",
            OverlayExpiryMisses);
    }

    public static UpdateNotice Evaluate(string currentVersion, ReleaseInfo latest, DateTimeOffset checkedAt)
    {
        // A development build is not on the release line, so an older release is never an update for it.
        var available = !BuildInfo.IsDevelopmentVersion(currentVersion)
            && SemanticVersion.TryParse(currentVersion, out var current)
            && SemanticVersion.TryParse(latest.Version, out var candidate)
            && candidate.CompareTo(current) > 0;
        return new UpdateNotice(currentVersion, latest, available, checkedAt);
    }
}
