using System.Text.Json;

namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>
/// Seeds rows through the store's public write methods, so every seeded row has
/// exactly the shape production reads back (snake_case columns, JSON columns parsed).
/// Replaces the managed test suite's direct pokes into the fake store's dictionaries.
/// </summary>
public static class StoreSeeding
{
    public static JsonElement ToJson(object value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);

    public static Task SeedCollectionAsync(this INetworkStorageStore store, string projectId, string collectionId, string name, string visibility, object definition, long version = 1)
        => store.UpsertCollectionAsync(projectId, collectionId, name, visibility, ToJson(definition), version, CancellationToken.None);

    public static Task SeedPlayerProfileAsync(
        this INetworkStorageStore store,
        string projectId,
        string steamId,
        string playerName,
        long lastSeenUnixMs,
        bool isOnline = false,
        long totalSeconds = 0,
        long sessionCount = 1,
        string? lastEventType = "",
        string managedCountersJson = "{}")
        => store.UpsertPlayerProfileAsync(projectId, steamId, playerName, isOnline, onlineSinceUnixMs: null, lastSeenUnixMs,
            lastHeartbeatUnixMs: null, currentSessionId: null, currentSessionLastSeconds: null, totalSeconds, sessionCount,
            lastEventType, lastEndpointSlug: null, managedCountersJson, updatedAtUnixMs: lastSeenUnixMs, CancellationToken.None);

    public static Task SeedPlayerEventAsync(
        this INetworkStorageStore store,
        string projectId,
        string steamId,
        long createdAtUnixMs,
        string eventId,
        string eventType,
        string category,
        string label,
        string endpointSlug,
        string collectionId,
        object payload)
        => store.InsertPlayerAnalyticsEventV2Async(projectId, steamId, createdAtUnixMs, eventId, eventType, category, label,
            endpointSlug, collectionId, ToJson(payload), CancellationToken.None);

    public static Task SeedAuditLogAsync(this INetworkStorageStore store, string projectId, long createdAtUnixMs, string logId, string userId, string action)
        => store.InsertAuditLogAsync(projectId, createdAtUnixMs, logId, userId, action, "{}", "{}", "{}", "{}", CancellationToken.None);
}
