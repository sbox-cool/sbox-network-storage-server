using System.Text.Json;
using C = SboxNetworkStorage.Storage.Relational.StoreColumns;
using V = SboxNetworkStorage.Storage.Relational.StoreValidation;

namespace SboxNetworkStorage.Storage.Relational;

// Time-series telemetry: audit logs, player analytics (legacy + V4), player
// profiles/sessions, project issues, storage errors, request log.
public abstract partial class RelationalNetworkStorageStore
{
    // ── project_audit_logs ──────────────────────────────────────────

    public Task InsertAuditLogAsync(string projectId, long createdAtUnixMs, string logId, string userId, string action, string actorJson, string targetJson, string summaryJson, string diffJson, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.UpsertAuditLog, ct,
            Text("project_id", projectId), Int64("created_at_unix_ms", createdAtUnixMs), Text("log_id", logId),
            Text("user_id", userId), Text("action", action), Text("actor_json", actorJson), Text("target_json", targetJson),
            Text("summary_json", summaryJson), Text("diff_json", diffJson));
    }

    public Task<IReadOnlyList<JsonElement>> ListAuditLogsAsync(string projectId, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Limit(limit);
        return QueryListAsync(_sql.ListAuditLogs, C.AuditLog, ct, Text("project_id", projectId), Int32("limit", limit));
    }

    public Task DeleteAuditLogsAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteAuditLogs, ct, Text("project_id", projectId));
    }

    // ── player_analytics (legacy) ───────────────────────────────────

    public Task InsertPlayerAnalyticsEventAsync(string projectId, string collectionId, string recordKey, long createdAtUnixMs, string eventId, string eventType, JsonElement payloadJson, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        var payload = Serialize(payloadJson, "player_analytics");
        return ExecuteAsync(_sql.UpsertPlayerAnalytics, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey),
            Int64("created_at_unix_ms", createdAtUnixMs), Text("event_id", eventId), Text("event_type", eventType),
            Text("payload_json", payload));
    }

    public Task<IReadOnlyList<JsonElement>> ListPlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey); V.Limit(limit);
        return QueryListAsync(_sql.ListPlayerAnalytics, C.PlayerAnalytics, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey), Int32("limit", limit));
    }

    public Task DeletePlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.RecordKey(recordKey);
        return ExecuteAsync(_sql.DeletePlayerAnalytics, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("record_key", recordKey));
    }

    // ── player_analytics_events (V4) ────────────────────────────────

    public Task InsertPlayerAnalyticsEventV2Async(string projectId, string steamId, long createdAtUnixMs, string eventId, string eventType, string category, string label, string endpointSlug, string collectionId, JsonElement payloadJson, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        var payload = Serialize(payloadJson, "player_analytics_events");
        return ExecuteAsync(_sql.UpsertPlayerEvent, ct,
            Text("project_id", projectId), Text("steam_id", steamId), Int64("created_at_unix_ms", createdAtUnixMs),
            Text("event_id", eventId), Text("event_type", eventType), Text("category", category), Text("label", label),
            Text("endpoint_slug", endpointSlug ?? string.Empty), Text("collection_id", collectionId ?? string.Empty),
            Text("payload_json", payload));
    }

    public Task<IReadOnlyList<JsonElement>> ListPlayerEventsAsync(string projectId, string steamId, long fromUnixMs, long toUnixMs, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId); V.Limit(limit);
        return QueryListAsync(_sql.ListPlayerEvents, C.PlayerEvent, ct,
            Text("project_id", projectId), Text("steam_id", steamId),
            Int64("to_unix_ms", toUnixMs), Int64("from_unix_ms", fromUnixMs), Int32("limit", limit));
    }

    public async Task<long> PurgeAnalyticsBeforeAsync(long beforeUnixMs, CancellationToken ct)
    {
        long deleted = 0;
        foreach (var sql in new[] { _sql.PurgeAnalyticsEvents, _sql.PurgeProjectIssues, _sql.PurgeLegacyAnalytics })
            deleted += await ExecuteCountAsync(sql, ct, Int64("before_unix_ms", beforeUnixMs));
        return deleted;
    }

    public Task<long> CountPlayerEventsAsync(string projectId, string steamId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        return QueryInt64ScalarAsync(_sql.CountPlayerEvents, ct, Text("project_id", projectId), Text("steam_id", steamId));
    }

    // ── player_profiles (V4) ────────────────────────────────────────

    public Task UpsertPlayerProfileAsync(string projectId, string steamId, string playerName, bool isOnline, long? onlineSinceUnixMs, long lastSeenUnixMs, long? lastHeartbeatUnixMs, string? currentSessionId, long? currentSessionLastSeconds, long totalSeconds, long sessionCount, string? lastEventType, string? lastEndpointSlug, string managedCountersJson, long updatedAtUnixMs, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        return ExecuteAsync(_sql.UpsertPlayerProfile, ct,
            Text("project_id", projectId), Text("steam_id", steamId), Text("player_name", playerName ?? string.Empty),
            Bool("is_online", isOnline), Int64("online_since_unix_ms", onlineSinceUnixMs), Int64("last_seen_unix_ms", lastSeenUnixMs),
            Int64("last_heartbeat_unix_ms", lastHeartbeatUnixMs), Text("current_session_id", currentSessionId ?? string.Empty),
            Int64("current_session_last_seconds", currentSessionLastSeconds), Int64("total_seconds", totalSeconds),
            Int64("session_count", sessionCount), Text("last_event_type", lastEventType ?? string.Empty),
            Text("last_endpoint_slug", lastEndpointSlug ?? string.Empty), Text("managed_counters_json", managedCountersJson ?? "{}"),
            Int64("updated_at_unix_ms", updatedAtUnixMs));
    }

    public Task<JsonElement?> ReadPlayerProfileAsync(string projectId, string steamId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        return QuerySingleAsync(_sql.ReadPlayerProfile, C.PlayerProfile, ct, Text("project_id", projectId), Text("steam_id", steamId));
    }

    public Task<IReadOnlyList<JsonElement>> ReadProjectProfilesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListPlayerProfiles, C.PlayerProfile, ct, Text("project_id", projectId));
    }

    // ── player_sessions (V4) ────────────────────────────────────────

    public Task InsertPlayerSessionAsync(string projectId, string steamId, string sessionId, long? startedAtUnixMs, long? lastHeartbeatAtUnixMs, long? endedAtUnixMs, string? lastMetricsJson, string? summaryJson, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        return ExecuteAsync(_sql.UpsertPlayerSession, ct,
            Text("project_id", projectId), Text("steam_id", steamId), Text("session_id", sessionId),
            Int64("started_at_unix_ms", startedAtUnixMs), Int64("last_heartbeat_at_unix_ms", lastHeartbeatAtUnixMs),
            Int64("ended_at_unix_ms", endedAtUnixMs), Text("last_metrics_json", lastMetricsJson ?? string.Empty),
            Text("summary_json", summaryJson ?? string.Empty));
    }

    public Task<JsonElement?> ReadPlayerSessionAsync(string projectId, string steamId, string sessionId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(steamId);
        return QuerySingleAsync(_sql.ReadPlayerSession, C.PlayerSession, ct,
            Text("project_id", projectId), Text("steam_id", steamId), Text("session_id", sessionId));
    }

    // ── project_analytics_issues (V4) ───────────────────────────────

    public Task InsertProjectIssueAsync(string projectId, string bucketDate, long createdAtUnixMs, string eventId, string steamId, string category, string eventType, string label, JsonElement payloadJson, CancellationToken ct)
    {
        V.Id(projectId);
        var payload = Serialize(payloadJson, "project_analytics_issues");
        return ExecuteAsync(_sql.UpsertProjectIssue, ct,
            Text("project_id", projectId), Text("bucket_date", bucketDate), Int64("created_at_unix_ms", createdAtUnixMs),
            Text("event_id", eventId), Text("steam_id", steamId), Text("category", category), Text("event_type", eventType),
            Text("label", label), Text("payload_json", payload));
    }

    public Task<IReadOnlyList<JsonElement>> ListProjectIssuesAsync(string projectId, string bucketDate, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Limit(limit);
        return QueryListAsync(_sql.ListProjectIssues, C.ProjectIssue, ct,
            Text("project_id", projectId), Text("bucket_date", bucketDate), Int32("limit", limit));
    }

    // ── storage_errors ──────────────────────────────────────────────

    public Task InsertStorageErrorAsync(string projectId, long createdAtUnixMs, string errorId, string message, string? stackTrace, string? source, string? requestPath, string severity, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.InsertStorageError, ct,
            Text("project_id", projectId), Int64("created_at_unix_ms", createdAtUnixMs), Text("error_id", errorId),
            Text("message", message), Text("stack_trace", stackTrace ?? string.Empty), Text("source", source ?? string.Empty),
            Text("request_path", requestPath ?? string.Empty), Text("severity", severity));
    }

    public Task<IReadOnlyList<JsonElement>> ListStorageErrorsAsync(string projectId, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Limit(limit);
        return QueryListAsync(_sql.ListStorageErrors, C.StorageError, ct, Text("project_id", projectId), Int32("limit", limit));
    }

    public Task PurgeStorageErrorsAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.PurgeStorageErrors, ct, Text("project_id", projectId), Int64("before_unix_ms", beforeUnixMs));
    }

    // ── storage_request_log ─────────────────────────────────────────

    public Task InsertStorageRequestLogAsync(string projectId, long createdAtUnixMs, string method, string path, int statusCode, int durationMs, string? apiKeyIdentifier, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.InsertStorageRequestLog, ct,
            Text("project_id", projectId), Int64("created_at_unix_ms", createdAtUnixMs), Text("method", method),
            Text("path", path), Int32("status_code", statusCode), Int32("duration_ms", durationMs),
            Text("api_key_identifier", apiKeyIdentifier ?? string.Empty));
    }

    public Task<IReadOnlyList<JsonElement>> ListStorageRequestLogAsync(string projectId, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Limit(limit);
        return QueryListAsync(_sql.ListStorageRequestLog, C.StorageRequestLog, ct, Text("project_id", projectId), Int32("limit", limit));
    }

    public Task PurgeStorageRequestLogAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.PurgeStorageRequestLog, ct, Text("project_id", projectId), Int64("before_unix_ms", beforeUnixMs));
    }
}
