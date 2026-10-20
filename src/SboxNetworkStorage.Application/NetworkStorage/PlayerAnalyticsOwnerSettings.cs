using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// The analytics values the owner form edits. Each flag defaults to true, as the game library does
/// when the key is absent.
/// </summary>
public sealed record PlayerAnalyticsOwnerSettings(
    bool Enabled,
    bool CaptureSessions,
    bool ManagedLibraryEvents,
    IReadOnlyList<string> AllowedEventTypes)
{
    public static PlayerAnalyticsOwnerSettings From(JsonNode? analytics) => new(
        ReadFlag(analytics, "enabled"),
        ReadFlag(analytics, "captureSessions"),
        ReadFlag(analytics, "managedLibraryEvents"),
        PlayerAnalyticsAllowedEvents.ReadStored(analytics));

    private static bool ReadFlag(JsonNode? analytics, string key) =>
        analytics is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : true;
}
