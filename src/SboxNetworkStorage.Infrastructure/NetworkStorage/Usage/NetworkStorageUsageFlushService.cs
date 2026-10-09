using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;

/// <summary>
/// Periodic flush loop for <see cref="NetworkStorageUsageTracker"/> — the .NET
/// equivalent of the legacy server tracker's <c>setInterval(flushUsage, 30_000)</c>
/// plus its <c>beforeExit</c> forced flush. Flush errors are contained by the
/// tracker itself (re-queue + backoff); this loop never crashes the host.
/// </summary>
public sealed class NetworkStorageUsageFlushService(
    NetworkStorageUsageTracker tracker,
    ILogger<NetworkStorageUsageFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(NetworkStorageUsageTracker.FlushIntervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await tracker.FlushAsync(force: false, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — fall through to the final forced flush.
        }
        finally
        {
            // Best-effort final drain so a graceful shutdown loses nothing.
            try
            {
                await tracker.FlushAsync(force: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final usage flush on shutdown failed");
            }
        }
    }
}
