using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// Watches the first <see cref="PostUpdateVerifier.WatchWindowHours"/> hours of each build. When the per-library census
/// regresses against the previous build, it sends one critical alert per build. It never rolls back: rollback stays an
/// owner decision, or the unattended path in <c>AutoUpdateCommand</c>, which runs before this window closes.
/// </summary>
public sealed class UpdateWatchService(
    PostUpdateVerifier verifier,
    AlertDispatcher alerts,
    EffectiveConfig config,
    TimeProvider time,
    ILogger<UpdateWatchService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Persisted per build: when this build first started, and whether its regression alert was already sent.</summary>
    internal sealed record WatchMarker(string Version, long SinceUnixMs, long? AlertedAtUnixMs);

    internal static string MarkerPathFor(string stateDirectory) => Path.Combine(stateDirectory, "update-watch.json");

    private string MarkerPath => MarkerPathFor(config.StateDirectory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Update regression check failed; it will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var version = BuildInfo.Version;
        var marker = ReadMarker();
        if (marker is null || marker.Version != version)
        {
            marker = new WatchMarker(version, now.ToUnixTimeMilliseconds(), null);
            WriteMarker(marker);
        }

        if (marker.AlertedAtUnixMs is not null)
        {
            return;
        }

        var elapsed = now - DateTimeOffset.FromUnixTimeMilliseconds(marker.SinceUnixMs);
        if (elapsed > TimeSpan.FromHours(PostUpdateVerifier.WatchWindowHours))
        {
            return;
        }

        // Only hours since this build started: pre-update traffic is the baseline, not the current window.
        var hours = Math.Clamp((int)Math.Ceiling(elapsed.TotalHours), 1, PostUpdateVerifier.WatchWindowHours);
        var verdict = await verifier.VerifyAsync(version, hours, VerdictThresholds.Default, ct);
        if (verdict.Status != VerdictStatus.Regression)
        {
            return;
        }

        var regressed = verdict.Libraries.Where(library => library.Outcome == LibraryOutcome.Regressed).ToList();
        var lines = regressed.Select(library =>
            $"{library.Library}: error rate {library.BaselineErrorRate:P1} -> {library.PostErrorRate:P1} ({library.PostRequests} requests)");
        var names = string.Join(", ", regressed.Select(library => library.Library));
        var alert = new AlertMessage(
            Kind: AlertMessage.RuleKind,
            Severity: AlertSeverity.Critical,
            Title: $"Update {version} regressed client requests",
            Subject: $"[sbox-ns] update {version} regressed {names}",
            Message: $"Client requests fail more often in the {hours} h since this build started than on {verdict.BaselineVersion}. "
                + "Run `sbox-ns update verify` for the per-library table. " + RollbackAdvice.Text(verdict.BaselineVersion),
            Fields:
            [
                new AlertField("Version", version, Code: true),
                new AlertField("Baseline", verdict.BaselineVersion ?? "none", Code: true),
                new AlertField("Regressed libraries", string.Join("\n", lines), Code: true),
                new AlertField("Roll back", string.Join("\n", RollbackAdvice.Steps(verdict.BaselineVersion))),
            ],
            Timestamp: now,
            RuleId: "update-regression");
        await alerts.SendAsync(alert, channelIds: null, ct);
        logger.LogCritical("Update {Version} regressed {Libraries}; alert sent.", version, names);
        WriteMarker(marker with { AlertedAtUnixMs = now.ToUnixTimeMilliseconds() });
    }

    internal static WatchMarker? ReadMarkerFile(string stateDirectory)
    {
        var path = MarkerPathFor(stateDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WatchMarker>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private WatchMarker? ReadMarker() => ReadMarkerFile(config.StateDirectory);

    private void WriteMarker(WatchMarker marker)
    {
        Directory.CreateDirectory(config.StateDirectory);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
    }
}
