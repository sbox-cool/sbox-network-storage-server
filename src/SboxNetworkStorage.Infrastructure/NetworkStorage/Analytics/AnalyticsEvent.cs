using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;

/// <summary>
/// A normalized player-analytics event, captured on the request thread and written later by
/// <see cref="AnalyticsWriterService"/>. <see cref="TimestampMs"/> is when the event happened, not when it is stored.
/// </summary>
public sealed record AnalyticsEvent(
    string ProjectId,
    string SteamId,
    string Type,
    string Label,
    string Source,
    string? EndpointSlug,
    string? CollectionId,
    string? SessionId,
    string PlayerName,
    long TimestampMs,
    JsonElement Payload,
    IReadOnlyList<TrackedFieldDelta>? TrackedFieldDeltas);
