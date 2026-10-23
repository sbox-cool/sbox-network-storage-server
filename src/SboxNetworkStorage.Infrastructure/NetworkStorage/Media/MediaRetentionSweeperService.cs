using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.Media;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Media;

/// <summary>
/// Purges expired media once a day: the object rows and the blobs nothing else references. Expired objects already
/// return 404 and stop counting toward quota, so this only reclaims stored bytes. The work lives in
/// <see cref="NetworkStorageMediaService.SweepExpiredAsync"/>; this type owns only the schedule.
/// </summary>
public sealed class MediaRetentionSweeperService(
    NetworkStorageMediaService media,
    TimeProvider time,
    ILoggerFactory loggerFactory) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan FirstSweepDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>Expired rows read per page. Bounds memory and the time each blob stripe is held.</summary>
    internal const int BatchSize = 200;

    private readonly ILogger _logger = loggerFactory.CreateLogger<MediaRetentionSweeperService>();

    // Set once at construction, so the first sweep runs FirstSweepDelay after server start, not after the first timer tick.
    private DateTimeOffset _nextSweep = time.GetUtcNow() + FirstSweepDelay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepIfDueAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal async Task SweepIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now < _nextSweep) return;

        _nextSweep = now + SweepInterval;
        try
        {
            var result = await media.SweepExpiredAsync(now.ToUnixTimeMilliseconds(), BatchSize, ct);
            if (result.ObjectsDeleted > 0 || result.BlobsDeleted > 0)
                _logger.LogInformation("Media retention sweep deleted {Objects} expired objects and {Blobs} unreferenced blobs",
                    result.ObjectsDeleted, result.BlobsDeleted);
            if (result.BlobFailures.Count > 0)
                _logger.LogWarning(result.BlobFailures[0], "Media retention sweep could not delete {Count} blobs; their expired rows remain and the next sweep retries them",
                    result.BlobFailures.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Media retention sweep failed; it will run again in {Interval}", SweepInterval);
        }
    }
}
