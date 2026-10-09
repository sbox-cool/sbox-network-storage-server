using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

public interface IPlayerAnalyticsService
{
    Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Record an event on the endpoint-execution data plane. Emitted for every
    /// endpoint call (the path the game client uses for save-all / load-player /
    /// etc.) so the analytics dashboard sees live activity. Best-effort: a
    /// failure is logged and never propagates to the caller. The optional
    /// <paramref name="trackedFieldDeltas"/> are the per-field before/after/delta
    /// derived by the caller from the project's <c>analytics.trackedFields</c>
    /// config (the caller already pre-reads records for the save-all guard, so
    /// deriving deltas is free of extra the store round-trips).
    /// </summary>
    Task RecordEndpointEventAsync(
        string projectId,
        string steamId,
        string endpointSlug,
        string eventType,
        IReadOnlyDictionary<string, object>? payload,
        IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
        CancellationToken cancellationToken);
}

public sealed record PlayerEventRequest(
    string ProjectId,
    string CollectionId,
    string RecordKey,
    string EventType,
    object Payload);

/// <summary>
/// A tracked-field progression sample (emitted when a configured tracked field
/// changes between the pre-read and post-write state of an endpoint call).
/// Replaces the legacy per-collection <c>logs/audit/{field}/{date}.json</c>
/// workspace files that the dashboard ledger chart reads.
/// </summary>
public sealed record TrackedFieldDelta(
    string Field,
    string CollectionId,
    double Before,
    double After,
    double Delta,
    string? Source = null);
