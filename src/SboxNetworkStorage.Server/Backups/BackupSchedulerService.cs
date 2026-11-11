using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Backups;

/// <summary>
/// Runs <see cref="BackupRunner"/> every backup.schedule.interval_hours while the server is up. The first run after
/// start waits about 10 minutes when no run is recorded; otherwise the next run is due one interval after the last
/// attempt, so restarts do not reset the schedule.
/// </summary>
public sealed class BackupSchedulerService(EffectiveConfig config, ILogger<BackupSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetBoolean("backup.schedule.enabled"))
            return;

        var interval = TimeSpan.FromHours(config.GetInteger("backup.schedule.interval_hours"));
        try
        {
            await Task.Delay(InitialDelay(interval), stoppingToken);
            while (true)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server shutdown.
        }
    }

    private TimeSpan InitialDelay(TimeSpan interval)
    {
        var attempted = BackupRunner.ReadState(config).AttemptedAt;
        if (attempted is null)
            return FirstRunDelay;
        var due = attempted.Value + interval - DateTimeOffset.UtcNow;
        return due < TimeSpan.Zero ? TimeSpan.Zero : due > interval ? interval : due;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await BackupRunner.RunAsync(config, null, ct);
            var file = Path.GetFileName(result.LocalFile);
            if (result.OffsiteError is null)
                logger.LogInformation("Scheduled backup {File} written; removed {Pruned} older automatic backup(s).", file, result.PrunedCount);
            else
                logger.LogWarning("Scheduled backup {File} was kept locally, but the sboxns upload failed: {Error}", file, result.OffsiteError);
        }
        catch (BackupBusyException)
        {
            logger.LogInformation("Skipped scheduled backup: another backup is already running.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Scheduled backup failed: {Error}", BackupRunner.DescribeError(ex));
        }
    }
}
