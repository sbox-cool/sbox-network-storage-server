using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Webhooks;

/// <summary>
/// Drains <c>webhook_outbox</c> about once a second. Each tick reaps expired leases (interrupted sends become
/// failed, never resent), delivers due rows in batches, and about every 10 minutes purges finished rows older
/// than 7 days. Endpoint execution never waits on this worker.
/// </summary>
public sealed class WebhookOutboxWorker(
    IWebhookOutboxStore store,
    WebhookOutboxDispatcher dispatcher,
    TimeProvider time,
    ILogger<WebhookOutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        var nextPurge = time.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var now = time.GetUtcNow().ToUnixTimeMilliseconds();
                    var reaped = await store.ReapExpiredLeasesAsync(now, stoppingToken);
                    if (reaped > 0)
                        logger.LogWarning("Reaped {Count} webhook deliveries interrupted before they finished", reaped);

                    int claimed;
                    do
                    {
                        claimed = await dispatcher.DrainOnceAsync(BatchSize, stoppingToken);
                    } while (claimed == BatchSize && !stoppingToken.IsCancellationRequested);

                    if (time.GetUtcNow() >= nextPurge)
                    {
                        await store.PurgeFinishedAsync(time.GetUtcNow().Subtract(Retention).ToUnixTimeMilliseconds(), stoppingToken);
                        nextPurge = time.GetUtcNow().Add(PurgeInterval);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook outbox tick failed; retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
