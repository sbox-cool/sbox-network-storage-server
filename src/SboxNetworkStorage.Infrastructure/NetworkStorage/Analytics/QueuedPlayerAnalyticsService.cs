using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;

/// <summary>
/// The <see cref="IPlayerAnalyticsService"/> request handlers use: it normalizes the event and hands it to the
/// <see cref="AnalyticsEventQueue"/>, doing no I/O and never waiting, so analytics cannot slow or fail a request.
/// </summary>
public sealed class QueuedPlayerAnalyticsService(
    AnalyticsEventQueue queue,
    TimeProvider time,
    ILogger<QueuedPlayerAnalyticsService> logger) : IPlayerAnalyticsService
{
    public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (PlayerAnalyticsIngester.Normalize(request, time.GetUtcNow().ToUnixTimeMilliseconds()) is { } analyticsEvent)
                queue.Enqueue(analyticsEvent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to queue player analytics event for project {ProjectId}", request.ProjectId);
        }
        return Task.CompletedTask;
    }

    public Task RecordEndpointEventAsync(
        string projectId,
        string steamId,
        string endpointSlug,
        string eventType,
        IReadOnlyDictionary<string, object>? payload,
        IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
        CancellationToken cancellationToken)
    {
        try
        {
            if (PlayerAnalyticsIngester.Normalize(projectId, steamId, endpointSlug, eventType, payload, trackedFieldDeltas,
                    time.GetUtcNow().ToUnixTimeMilliseconds()) is { } analyticsEvent)
                queue.Enqueue(analyticsEvent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to queue endpoint analytics event for project {ProjectId} endpoint {Slug}", projectId, endpointSlug);
        }
        return Task.CompletedTask;
    }
}
