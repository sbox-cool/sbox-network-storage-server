using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// In-memory implementation of <see cref="INetworkStorageStore"/> for tests and
/// throwaway servers. Rows have exactly the shape, ordering, overwrite and
/// validation semantics of the production ScyllaDB store (verified by the
/// storage conformance suite). Tables are dictionaries keyed by the primary key
/// parts joined with U+001F, which no ID can contain.
/// </summary>
public class InMemoryNetworkStorageStore : INetworkStorageStore
{
    private const char Sep = '\u001F';
    private const int PayloadLimitBytes = 64 * 1024;
    public int MaxPayloadBytes => PayloadLimitBytes;
    private static readonly long QueryRunLogTtlMs = 7_776_000L * 1000;
    private static readonly Regex IdPattern = new("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex RecordKeyPattern = new("^[a-zA-Z0-9_:-]{1,256}$", RegexOptions.Compiled);

    private readonly TimeProvider _time;

    public InMemoryNetworkStorageStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public ConcurrentDictionary<string, JsonElement> Projects { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Collections { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Endpoints { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Workflows { get; } = new();
    public ConcurrentDictionary<string, JsonElement> GameValues { get; } = new();
    public ConcurrentDictionary<string, JsonElement> RateLimitRules { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Queries { get; } = new();
    public ConcurrentDictionary<string, JsonElement> QueryLastRuns { get; } = new();
    public ConcurrentDictionary<string, JsonElement> QueryRunLogs { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Records { get; } = new();
    public ConcurrentDictionary<string, JsonElement> RecordIdempotency { get; } = new();
    public ConcurrentDictionary<string, JsonElement> GlobalRecords { get; } = new();
    public ConcurrentDictionary<string, JsonElement> LedgerEntries { get; } = new();
    public ConcurrentDictionary<string, JsonElement> CheckpointCursors { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ApiKeys { get; } = new();
    public ConcurrentDictionary<string, JsonElement> AuditLogs { get; } = new();
    public ConcurrentDictionary<string, JsonElement> PlayerAnalytics { get; } = new();
    public ConcurrentDictionary<string, JsonElement> PlayerAnalyticsEvents { get; } = new();
    public ConcurrentDictionary<string, JsonElement> PlayerProfiles { get; } = new();
    public ConcurrentDictionary<string, JsonElement> PlayerSessions { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ProjectAnalyticsIssues { get; } = new();
    public ConcurrentDictionary<string, JsonElement> ProjectMembers { get; } = new();
    public ConcurrentDictionary<string, JsonElement> Pages { get; } = new();
    public ConcurrentDictionary<string, JsonElement> StorageErrors { get; } = new();
    public ConcurrentDictionary<string, JsonElement> StorageRequestLog { get; } = new();

    // ── projects ────────────────────────────────────────────────────
    public Task UpsertProjectAsync(string projectId, JsonElement payload, long version, CancellationToken ct)
    {
        Id(projectId);
        Projects[projectId] = ParseJson(Serialize(payload, "projects")) ?? default;
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct)
    {
        Id(projectId);
        return Task.FromResult(Projects.TryGetValue(projectId, out var v) && v.ValueKind != JsonValueKind.Undefined ? v : (JsonElement?)null);
    }
    public Task DeleteProjectAsync(string projectId, CancellationToken ct)
    {
        Id(projectId);
        Projects.TryRemove(projectId, out _);
        // Production also drops the project's usage counters and query run telemetry.
        RemoveUnder(UsageMonthly, P(projectId));
        RemoveUnder(UsageDaily, P(projectId));
        RemoveUnder(UsageEndpoints, P(projectId));
        RemoveUnder(QueryLastRuns, P(projectId));
        RemoveUnder(QueryRunLogs, P(projectId));
        return Task.CompletedTask;
    }

    // ── collections ─────────────────────────────────────────────────
    public Task UpsertCollectionAsync(string projectId, string collectionId, string name, string visibility, JsonElement definitionJson, long version, CancellationToken ct)
    {
        Id(projectId); Id(collectionId);
        if (collectionId.StartsWith("__sbox_", StringComparison.Ordinal))
            throw new ArgumentException($"Collection ID '{collectionId}' uses a reserved system namespace (__sbox_). Choose a different ID.");
        var def = Serialize(definitionJson, "collections");
        Collections[K(projectId, collectionId)] = Row(new { collection_id = collectionId, name, visibility, definition_json = ParseJson(def), version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct) { Id(projectId); Id(collectionId); return Get(Collections, K(projectId, collectionId)); }
    public Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(Collections, P(projectId)).OrderBy(r => Str(r, "collection_id"), StringComparer.Ordinal)); }
    public Task DeleteCollectionAsync(string projectId, string collectionId, CancellationToken ct) { Id(projectId); Id(collectionId); return Remove(Collections, K(projectId, collectionId)); }

    // ── endpoints ───────────────────────────────────────────────────
    public Task UpsertEndpointAsync(string projectId, string endpointId, string slug, string method, bool enabled, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct)
    {
        Id(projectId); Id(endpointId);
        var def = Serialize(definitionJson, "endpoints");
        Endpoints[K(projectId, endpointId)] = Row(new { endpoint_id = endpointId, slug, method, enabled, definition_json = ParseJson(def), version_hash = versionHash, version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadEndpointAsync(string projectId, string endpointId, CancellationToken ct) { Id(projectId); Id(endpointId); return Get(Endpoints, K(projectId, endpointId)); }
    public Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(Endpoints, P(projectId)).OrderBy(r => Str(r, "endpoint_id"), StringComparer.Ordinal)); }
    public Task DeleteEndpointAsync(string projectId, string endpointId, CancellationToken ct) { Id(projectId); Id(endpointId); return Remove(Endpoints, K(projectId, endpointId)); }

    // ── workflows ───────────────────────────────────────────────────
    public Task UpsertWorkflowAsync(string projectId, string workflowId, string name, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct)
    {
        Id(projectId); Id(workflowId);
        var def = Serialize(definitionJson, "workflows");
        Workflows[K(projectId, workflowId)] = Row(new { workflow_id = workflowId, name, definition_json = ParseJson(def), version_hash = versionHash, version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadWorkflowAsync(string projectId, string workflowId, CancellationToken ct) { Id(projectId); Id(workflowId); return Get(Workflows, K(projectId, workflowId)); }
    public Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(Workflows, P(projectId)).OrderBy(r => Str(r, "workflow_id"), StringComparer.Ordinal)); }
    public Task DeleteWorkflowAsync(string projectId, string workflowId, CancellationToken ct) { Id(projectId); Id(workflowId); return Remove(Workflows, K(projectId, workflowId)); }

    // ── game_values ─────────────────────────────────────────────────
    public Task UpsertGameValuesAsync(string projectId, JsonElement payloadJson, string? versionHash, long version, CancellationToken ct)
    {
        Id(projectId);
        var payload = Serialize(payloadJson, "game_values");
        GameValues[projectId] = Row(new { payload_json = ParseJson(payload), version_hash = versionHash, version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct) { Id(projectId); return Get(GameValues, projectId); }
    public Task DeleteGameValuesAsync(string projectId, CancellationToken ct) { Id(projectId); return Remove(GameValues, projectId); }

    // ── rate_limit_rules ────────────────────────────────────────────
    public Task UpsertRateLimitRulesAsync(string projectId, JsonElement rulesJson, long version, CancellationToken ct)
    {
        Id(projectId);
        var rules = Serialize(rulesJson, "rate_limit_rules");
        RateLimitRules[projectId] = Row(new { rules_json = ParseJson(rules), version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadRateLimitRulesAsync(string projectId, CancellationToken ct) { Id(projectId); return Get(RateLimitRules, projectId); }
    public Task DeleteRateLimitRulesAsync(string projectId, CancellationToken ct) { Id(projectId); return Remove(RateLimitRules, projectId); }

    // ── queries ─────────────────────────────────────────────────────
    public Task UpsertQueryAsync(string projectId, string queryId, string name, bool requiresSecretKey, JsonElement definitionJson, long version, CancellationToken ct)
    {
        Id(projectId); Id(queryId);
        var def = Serialize(definitionJson, "queries");
        Queries[K(projectId, queryId)] = Row(new { query_id = queryId, name, requires_secret_key = requiresSecretKey, definition_json = ParseJson(def), version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct) { Id(projectId); Id(queryId); return Get(Queries, K(projectId, queryId)); }
    public Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(Queries, P(projectId)).OrderBy(r => Str(r, "query_id"), StringComparer.Ordinal)); }
    public Task DeleteQueryAsync(string projectId, string queryId, CancellationToken ct) { Id(projectId); Id(queryId); return Remove(Queries, K(projectId, queryId)); }

    // ── query run tracking (query_last_run + query_run_logs, 90-day TTL) ──
    public Task RecordQueryRunAsync(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, CancellationToken ct)
    {
        Id(projectId); Id(queryId);
        var now = Now();
        QueryLastRuns[K(projectId, queryId)] = Row(new { query_id = queryId, run_at = runAtIso, duration_ms = durationMs, keys_scanned = keysScanned, records_returned = recordsReturned, from_cache = fromCache, updated_at_unix_ms = now });
        // Keyed by created_at: a second run in the same millisecond overwrites the first.
        QueryRunLogs[K(projectId, queryId, now.ToString(System.Globalization.CultureInfo.InvariantCulture))] = Row(new { created_at_unix_ms = now, log_type = "run", duration_ms = durationMs, keys_scanned = keysScanned, records_returned = recordsReturned, from_cache = fromCache, changes_json = (JsonElement?)null });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadQueryLastRunAsync(string projectId, string queryId, CancellationToken ct)
    {
        Id(projectId); Id(queryId);
        return Task.FromResult(QueryLastRuns.TryGetValue(K(projectId, queryId), out var v) ? WithoutProperty(v, "query_id") : (JsonElement?)null);
    }
    public Task<IReadOnlyList<JsonElement>> ListQueryLastRunsAsync(string projectId, CancellationToken ct)
    { Id(projectId); return List(Under(QueryLastRuns, P(projectId)).OrderBy(r => Str(r, "query_id"), StringComparer.Ordinal)); }
    public Task<IReadOnlyList<JsonElement>> ListQueryLogsAsync(string projectId, string queryId, int limit, CancellationToken ct)
    {
        Id(projectId); Id(queryId);
        var expiredAt = Now() - QueryRunLogTtlMs;
        return List(Under(QueryRunLogs, P(projectId, queryId))
            .Where(r => Long(r, "created_at_unix_ms") > expiredAt)
            .OrderByDescending(r => Long(r, "created_at_unix_ms"))
            .Take(Math.Max(1, Math.Min(limit, 200))));
    }

    // ── records ─────────────────────────────────────────────────────
    public Task UpsertRecordAsync(string projectId, string collectionId, string recordKey, JsonElement payloadJson, bool deleted, long version, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); RecordKey(recordKey);
        var payload = Serialize(payloadJson, "records");
        Records[K(projectId, collectionId, recordKey)] = Row(new { record_key = recordKey, payload_json = ParseJson(payload), deleted, version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) { Id(projectId); Id(collectionId); RecordKey(recordKey); return Get(Records, K(projectId, collectionId, recordKey)); }
    public Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct) { Id(projectId); Id(collectionId); return List(Under(Records, P(projectId, collectionId)).OrderBy(r => Str(r, "record_key"), StringComparer.Ordinal)); }
    public Task DeleteRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) { Id(projectId); Id(collectionId); RecordKey(recordKey); return Remove(Records, K(projectId, collectionId, recordKey)); }

    // ── record_idempotency ──────────────────────────────────────────
    public Task UpsertRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, long resultRecordVersion, string resultHash, JsonElement payloadJson, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); RecordKey(recordKey);
        var payload = Serialize(payloadJson, "record_idempotency");
        RecordIdempotency[K(projectId, collectionId, recordKey, idempotencyKey)] = Row(new { idempotency_key = idempotencyKey, result_record_version = resultRecordVersion, result_hash = resultHash, payload_json = ParseJson(payload), created_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) { Id(projectId); Id(collectionId); RecordKey(recordKey); return Get(RecordIdempotency, K(projectId, collectionId, recordKey, idempotencyKey)); }
    public Task DeleteRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) { Id(projectId); Id(collectionId); RecordKey(recordKey); return Remove(RecordIdempotency, K(projectId, collectionId, recordKey, idempotencyKey)); }

    // ── global_records ──────────────────────────────────────────────
    public Task UpsertGlobalRecordAsync(string projectId, string collectionId, string recordId, JsonElement payloadJson, long version, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); Id(recordId);
        var payload = Serialize(payloadJson, "global_records");
        GlobalRecords[K(projectId, collectionId, recordId)] = Row(new { record_id = recordId, payload_json = ParseJson(payload), version, created_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) { Id(projectId); Id(collectionId); Id(recordId); return Get(GlobalRecords, K(projectId, collectionId, recordId)); }
    public Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct) { Id(projectId); Id(collectionId); return List(Under(GlobalRecords, P(projectId, collectionId)).OrderBy(r => Str(r, "record_id"), StringComparer.Ordinal)); }
    public Task DeleteGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) { Id(projectId); Id(collectionId); Id(recordId); return Remove(GlobalRecords, K(projectId, collectionId, recordId)); }

    // ── ledger_entries (clustered by sequence ASC) ──────────────────
    public Task InsertLedgerEntryAsync(string projectId, string collectionId, string recordKey, long sequence, JsonElement entryJson, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); RecordKey(recordKey);
        var entry = Serialize(entryJson, "ledger_entries");
        LedgerEntries[K(projectId, collectionId, recordKey, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))] = Row(new { sequence, entry_json = ParseJson(entry), created_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    { Id(projectId); Id(collectionId); RecordKey(recordKey); return List(Under(LedgerEntries, P(projectId, collectionId, recordKey)).OrderBy(r => Long(r, "sequence"))); }
    public Task DeleteLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    { Id(projectId); Id(collectionId); RecordKey(recordKey); RemoveUnder(LedgerEntries, P(projectId, collectionId, recordKey)); return Task.CompletedTask; }

    // ── checkpoint_cursor ───────────────────────────────────────────
    public Task UpsertCheckpointCursorAsync(string projectId, long latestSequence, string? manifestPath, long version, CancellationToken ct)
    {
        Id(projectId);
        CheckpointCursors[projectId] = Row(new { latest_sequence = latestSequence, manifest_path = manifestPath, updated_at_unix_ms = Now(), version });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadCheckpointCursorAsync(string projectId, CancellationToken ct) { Id(projectId); return Get(CheckpointCursors, projectId); }
    public Task DeleteCheckpointCursorAsync(string projectId, CancellationToken ct) { Id(projectId); return Remove(CheckpointCursors, projectId); }

    // ── api_keys ────────────────────────────────────────────────────
    public Task UpsertApiKeyAsync(string projectId, string apiKey, string userId, string keyType, string keyHash, string keyIdentifier, string label, bool enabled, JsonElement permissionsJson, long version, CancellationToken ct)
    {
        Id(projectId);
        var perms = Serialize(permissionsJson, "api_keys");
        ApiKeys[K(projectId, apiKey)] = Row(new { api_key = apiKey, user_id = userId, key_type = keyType, key_hash = keyHash, key_identifier = keyIdentifier, label, enabled, permissions_json = ParseJson(perms), version, updated_at_unix_ms = Now() });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct) { Id(projectId); return Get(ApiKeys, K(projectId, apiKey)); }
    public virtual Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(ApiKeys, P(projectId)).OrderBy(r => Str(r, "api_key"), StringComparer.Ordinal)); }
    public Task DeleteApiKeyAsync(string projectId, string apiKey, CancellationToken ct) { Id(projectId); return Remove(ApiKeys, K(projectId, apiKey)); }

    // ── project_audit_logs (created_at DESC, log_id ASC; JSON columns stay raw strings) ──
    public Task InsertAuditLogAsync(string projectId, long createdAtUnixMs, string logId, string userId, string action, string actorJson, string targetJson, string summaryJson, string diffJson, CancellationToken ct)
    {
        Id(projectId);
        AuditLogs[K(projectId, Num(createdAtUnixMs), logId)] = Row(new { created_at_unix_ms = createdAtUnixMs, log_id = logId, user_id = userId, action, actor_json = actorJson, target_json = targetJson, summary_json = summaryJson, diff_json = diffJson });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListAuditLogsAsync(string projectId, int limit, CancellationToken ct)
    { Id(projectId); Limit(limit); return List(Under(AuditLogs, P(projectId)).OrderByDescending(r => Long(r, "created_at_unix_ms")).ThenBy(r => Str(r, "log_id"), StringComparer.Ordinal).Take(limit)); }
    public Task DeleteAuditLogsAsync(string projectId, CancellationToken ct) { Id(projectId); RemoveUnder(AuditLogs, P(projectId)); return Task.CompletedTask; }

    // ── player_analytics (legacy) ───────────────────────────────────
    public Task InsertPlayerAnalyticsEventAsync(string projectId, string collectionId, string recordKey, long createdAtUnixMs, string eventId, string eventType, JsonElement payloadJson, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); RecordKey(recordKey);
        var payload = Serialize(payloadJson, "player_analytics");
        PlayerAnalytics[K(projectId, collectionId, recordKey, Num(createdAtUnixMs), eventId)] = Row(new { record_key = recordKey, created_at_unix_ms = createdAtUnixMs, event_id = eventId, event_type = eventType, payload_json = ParseJson(payload) });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListPlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, int limit, CancellationToken ct)
    {
        Id(projectId); Id(collectionId); RecordKey(recordKey); Limit(limit);
        return List(Under(PlayerAnalytics, P(projectId, collectionId, recordKey)).OrderByDescending(r => Long(r, "created_at_unix_ms")).ThenBy(r => Str(r, "event_id"), StringComparer.Ordinal).Take(limit));
    }
    public Task DeletePlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    { Id(projectId); Id(collectionId); RecordKey(recordKey); RemoveUnder(PlayerAnalytics, P(projectId, collectionId, recordKey)); return Task.CompletedTask; }

    // ── player_analytics_events (V4) ────────────────────────────────
    public Task InsertPlayerAnalyticsEventV2Async(string projectId, string steamId, long createdAtUnixMs, string eventId, string eventType, string category, string label, string endpointSlug, string collectionId, JsonElement payloadJson, CancellationToken ct)
    {
        Id(projectId); Id(steamId);
        var payload = Serialize(payloadJson, "player_analytics_events");
        PlayerAnalyticsEvents[K(projectId, steamId, Num(createdAtUnixMs), eventId)] = Row(new
        {
            steam_id = steamId, created_at_unix_ms = createdAtUnixMs, event_id = eventId, event_type = eventType, category, label,
            endpoint_slug = endpointSlug ?? string.Empty, collection_id = collectionId ?? string.Empty, payload_json = ParseJson(payload),
        });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListPlayerEventsAsync(string projectId, string steamId, long fromUnixMs, long toUnixMs, int limit, CancellationToken ct)
    {
        Id(projectId); Id(steamId); Limit(limit);
        return List(Under(PlayerAnalyticsEvents, P(projectId, steamId))
            .Where(r => Long(r, "created_at_unix_ms") >= fromUnixMs && Long(r, "created_at_unix_ms") <= toUnixMs)
            .OrderByDescending(r => Long(r, "created_at_unix_ms")).ThenBy(r => Str(r, "event_id"), StringComparer.Ordinal)
            .Take(limit));
    }
    public Task<long> CountPlayerEventsAsync(string projectId, string steamId, CancellationToken ct)
    { Id(projectId); Id(steamId); return Task.FromResult((long)Under(PlayerAnalyticsEvents, P(projectId, steamId)).Count()); }

    // ── player_profiles (V4) ────────────────────────────────────────
    public Task UpsertPlayerProfileAsync(string projectId, string steamId, string playerName, bool isOnline, long? onlineSinceUnixMs, long lastSeenUnixMs, long? lastHeartbeatUnixMs, string? currentSessionId, long? currentSessionLastSeconds, long totalSeconds, long sessionCount, string? lastEventType, string? lastEndpointSlug, string managedCountersJson, long updatedAtUnixMs, CancellationToken ct)
    {
        Id(projectId); Id(steamId);
        PlayerProfiles[K(projectId, steamId)] = Row(new
        {
            steam_id = steamId, player_name = playerName ?? string.Empty, is_online = isOnline, online_since_unix_ms = onlineSinceUnixMs,
            last_seen_unix_ms = lastSeenUnixMs, last_heartbeat_unix_ms = lastHeartbeatUnixMs, current_session_id = currentSessionId ?? string.Empty,
            current_session_last_seconds = currentSessionLastSeconds, total_seconds = totalSeconds, session_count = sessionCount,
            last_event_type = lastEventType ?? string.Empty, last_endpoint_slug = lastEndpointSlug ?? string.Empty,
            managed_counters_json = ParseJson(managedCountersJson ?? "{}"), updated_at_unix_ms = updatedAtUnixMs,
        });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadPlayerProfileAsync(string projectId, string steamId, CancellationToken ct) { Id(projectId); Id(steamId); return Get(PlayerProfiles, K(projectId, steamId)); }
    public Task<IReadOnlyList<JsonElement>> ReadProjectProfilesAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(PlayerProfiles, P(projectId)).OrderBy(r => Str(r, "steam_id"), StringComparer.Ordinal)); }

    // ── player_sessions (V4) ────────────────────────────────────────
    public Task InsertPlayerSessionAsync(string projectId, string steamId, string sessionId, long? startedAtUnixMs, long? lastHeartbeatAtUnixMs, long? endedAtUnixMs, string? lastMetricsJson, string? summaryJson, CancellationToken ct)
    {
        Id(projectId); Id(steamId);
        PlayerSessions[K(projectId, steamId, sessionId)] = Row(new
        {
            session_id = sessionId, started_at_unix_ms = startedAtUnixMs, last_heartbeat_at_unix_ms = lastHeartbeatAtUnixMs,
            ended_at_unix_ms = endedAtUnixMs, last_metrics_json = ParseJson(lastMetricsJson), summary_json = ParseJson(summaryJson),
        });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadPlayerSessionAsync(string projectId, string steamId, string sessionId, CancellationToken ct) { Id(projectId); Id(steamId); return Get(PlayerSessions, K(projectId, steamId, sessionId)); }

    // ── project_analytics_issues (V4) ───────────────────────────────
    public Task InsertProjectIssueAsync(string projectId, string bucketDate, long createdAtUnixMs, string eventId, string steamId, string category, string eventType, string label, JsonElement payloadJson, CancellationToken ct)
    {
        Id(projectId);
        var payload = Serialize(payloadJson, "project_analytics_issues");
        ProjectAnalyticsIssues[K(projectId, bucketDate, Num(createdAtUnixMs), eventId)] = Row(new { created_at_unix_ms = createdAtUnixMs, event_id = eventId, steam_id = steamId, category, event_type = eventType, label, payload_json = ParseJson(payload) });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListProjectIssuesAsync(string projectId, string bucketDate, int limit, CancellationToken ct)
    {
        Id(projectId); Limit(limit);
        return List(Under(ProjectAnalyticsIssues, P(projectId, bucketDate)).OrderByDescending(r => Long(r, "created_at_unix_ms")).ThenBy(r => Str(r, "event_id"), StringComparer.Ordinal).Take(limit));
    }

    // ── project_members ─────────────────────────────────────────────
    public Task UpsertProjectMembershipAsync(string userId, string projectId, string role, long createdAtUnixMs, CancellationToken ct)
    {
        Id(projectId);
        ProjectMembers[K(userId, projectId)] = Row(new { project_id = projectId, role, created_at_unix_ms = createdAtUnixMs });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListProjectsForUserAsync(string userId, CancellationToken ct)
        => List(Under(ProjectMembers, P(userId)).OrderBy(r => Str(r, "project_id"), StringComparer.Ordinal));
    public Task DeleteProjectMembershipAsync(string userId, string projectId, CancellationToken ct) { Id(projectId); return Remove(ProjectMembers, K(userId, projectId)); }

    // ── pages (content_json stays a raw string) ─────────────────────
    public Task UpsertPageAsync(string projectId, string pageSlug, string title, string contentJson, long createdAtUnixMs, long updatedAtUnixMs, CancellationToken ct)
    {
        Id(projectId);
        Pages[K(projectId, pageSlug)] = Row(new { page_slug = pageSlug, title, content_json = contentJson, created_at_unix_ms = createdAtUnixMs, updated_at_unix_ms = updatedAtUnixMs });
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadPageAsync(string projectId, string pageSlug, CancellationToken ct) { Id(projectId); return Get(Pages, K(projectId, pageSlug)); }
    public Task<IReadOnlyList<JsonElement>> ListPagesAsync(string projectId, CancellationToken ct) { Id(projectId); return List(Under(Pages, P(projectId)).OrderBy(r => Str(r, "page_slug"), StringComparer.Ordinal)); }
    public Task DeletePageAsync(string projectId, string pageSlug, CancellationToken ct) { Id(projectId); return Remove(Pages, K(projectId, pageSlug)); }

    // ── storage_errors (keyed by created_at: same-millisecond rows overwrite) ──
    public Task InsertStorageErrorAsync(string projectId, long createdAtUnixMs, string errorId, string message, string? stackTrace, string? source, string? requestPath, string severity, CancellationToken ct)
    {
        Id(projectId);
        StorageErrors[K(projectId, Num(createdAtUnixMs))] = Row(new { created_at_unix_ms = createdAtUnixMs, error_id = errorId, message, stack_trace = stackTrace ?? string.Empty, source = source ?? string.Empty, request_path = requestPath ?? string.Empty, severity });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListStorageErrorsAsync(string projectId, int limit, CancellationToken ct)
    { Id(projectId); Limit(limit); return List(Under(StorageErrors, P(projectId)).OrderByDescending(r => Long(r, "created_at_unix_ms")).Take(limit)); }
    public Task PurgeStorageErrorsAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    { Id(projectId); RemoveWhere(StorageErrors, P(projectId), r => Long(r, "created_at_unix_ms") < beforeUnixMs); return Task.CompletedTask; }

    // ── storage_request_log (keyed by created_at) ───────────────────
    public Task InsertStorageRequestLogAsync(string projectId, long createdAtUnixMs, string method, string path, int statusCode, int durationMs, string? apiKeyIdentifier, CancellationToken ct)
    {
        Id(projectId);
        StorageRequestLog[K(projectId, Num(createdAtUnixMs))] = Row(new { created_at_unix_ms = createdAtUnixMs, method, path, status_code = statusCode, duration_ms = durationMs, api_key_identifier = apiKeyIdentifier ?? string.Empty });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<JsonElement>> ListStorageRequestLogAsync(string projectId, int limit, CancellationToken ct)
    { Id(projectId); Limit(limit); return List(Under(StorageRequestLog, P(projectId)).OrderByDescending(r => Long(r, "created_at_unix_ms")).Take(limit)); }
    public Task PurgeStorageRequestLogAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    { Id(projectId); RemoveWhere(StorageRequestLog, P(projectId), r => Long(r, "created_at_unix_ms") < beforeUnixMs); return Task.CompletedTask; }

    // ── usage counters ──────────────────────────────────────────────
    // Keys: monthly (project, month), daily (project, month, day), endpoints (project, month, slug).
    public ConcurrentDictionary<string, ConcurrentDictionary<string, long>> UsageMonthly { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, long>> UsageDaily { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, long>> UsageEndpoints { get; } = new();

    private static readonly string[] MonthlyCounters = ["requests", "reads", "writes", "endpoint_calls", "bytes_in", "bytes_out", "errors", "duration_ms_sum", "duration_samples", "compute_units", "storage_delta_bytes"];
    private static readonly string[] DailyCounters = ["requests", "bytes_in", "bytes_out", "errors", "compute_units"];
    private static readonly string[] EndpointCounters = ["calls", "errors", "duration_ms_sum", "compute_units"];

    public virtual Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct)
    {
        Id(projectId);
        if (string.IsNullOrEmpty(month)) throw new ArgumentException("Month key is required.", nameof(month));
        if (string.IsNullOrEmpty(day)) throw new ArgumentException("Day key is required.", nameof(day));
        Bump(UsageMonthly, K(projectId, month), MonthlyCounters,
            [delta.Requests, delta.Reads, delta.Writes, delta.EndpointCalls, delta.BytesIn, delta.BytesOut, delta.Errors, delta.DurationMsSum, delta.DurationSamples, delta.ComputeUnits, delta.StorageDeltaBytes]);
        Bump(UsageDaily, K(projectId, month, day), DailyCounters, [delta.Requests, delta.BytesIn, delta.BytesOut, delta.Errors, delta.ComputeUnits]);
        if (!string.IsNullOrEmpty(endpointSlug))
            Bump(UsageEndpoints, K(projectId, month, endpointSlug), EndpointCounters, [delta.EndpointCalls, delta.Errors, delta.DurationMsSum, delta.ComputeUnits]);
        return Task.CompletedTask;
    }
    public Task<JsonElement?> ReadProjectUsageMonthlyAsync(string projectId, string month, CancellationToken ct)
    {
        Id(projectId);
        return Task.FromResult(UsageMonthly.TryGetValue(K(projectId, month), out var row) ? CounterRow(null, null, row, MonthlyCounters) : (JsonElement?)null);
    }
    public Task<long> ReadProjectStorageBytesAsync(string projectId, CancellationToken ct)
    {
        Id(projectId);
        var total = UsageMonthly.Where(kv => kv.Key.StartsWith(P(projectId), StringComparison.Ordinal)).Sum(kv => kv.Value.GetValueOrDefault("storage_delta_bytes"));
        return Task.FromResult(Math.Max(0, total));
    }
    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageDailyAsync(string projectId, string month, CancellationToken ct)
    {
        Id(projectId);
        var prefix = P(projectId, month);
        return List(UsageDaily.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => CounterRow("day", kv.Key[prefix.Length..], kv.Value, DailyCounters)));
    }
    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageEndpointsAsync(string projectId, string month, int limit, CancellationToken ct)
    {
        Id(projectId); Limit(limit);
        var prefix = P(projectId, month);
        return List(UsageEndpoints.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv => CounterRow("endpoint_slug", kv.Key[prefix.Length..], kv.Value, EndpointCounters)));
    }

    // ── workspace_objects ───────────────────────────────────────────
    public ConcurrentDictionary<string, (string Content, DateTimeOffset UpdatedAt)> WorkspaceObjects { get; } = new(StringComparer.Ordinal);

    public Task PutWorkspaceObjectAsync(string path, string content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        WorkspaceObjects[WorkspaceObjectPaths.NormalizeObjectPath(path)] = (content, DateTimeOffset.FromUnixTimeMilliseconds(Now()));
        return Task.CompletedTask;
    }
    public Task<string?> ReadWorkspaceObjectAsync(string path, CancellationToken ct)
        => Task.FromResult(WorkspaceObjects.TryGetValue(WorkspaceObjectPaths.NormalizeObjectPath(path), out var entry) ? entry.Content : null);
    public Task DeleteWorkspaceObjectAsync(string path, CancellationToken ct)
    {
        WorkspaceObjects.TryRemove(WorkspaceObjectPaths.NormalizeObjectPath(path), out _);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<WorkspaceObjectEntry>> ListWorkspaceObjectsAsync(string directoryPath, CancellationToken ct)
    {
        var prefix = WorkspaceObjectPaths.NormalizeDirectoryPrefix(directoryPath);
        return Task.FromResult(WorkspaceObjectPaths.ImmediateChildren(
            prefix,
            WorkspaceObjects.Select(kv => (kv.Key, (long)Encoding.UTF8.GetByteCount(kv.Value.Content), kv.Value.UpdatedAt))));
    }

    // ── helpers ─────────────────────────────────────────────────────
    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static string K(params string[] parts) => string.Join(Sep, parts);
    private static string P(params string[] parts) => string.Join(Sep, parts) + Sep;
    private static string Num(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static JsonElement Row<T>(T row) => JsonSerializer.SerializeToElement(row);
    private static string Str(JsonElement row, string name) => row.GetProperty(name).GetString() ?? string.Empty;
    private static long Long(JsonElement row, string name) => row.GetProperty(name).GetInt64();
    private static Task<JsonElement?> Get(ConcurrentDictionary<string, JsonElement> table, string key)
        => Task.FromResult(table.TryGetValue(key, out var v) ? v : (JsonElement?)null);
    private static Task Remove(ConcurrentDictionary<string, JsonElement> table, string key) { table.TryRemove(key, out _); return Task.CompletedTask; }
    private static IEnumerable<JsonElement> Under(ConcurrentDictionary<string, JsonElement> table, string prefix)
        => table.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => kv.Value);
    private static Task<IReadOnlyList<JsonElement>> List(IEnumerable<JsonElement> rows) => Task.FromResult<IReadOnlyList<JsonElement>>(rows.ToList());
    private static void RemoveUnder<T>(ConcurrentDictionary<string, T> table, string prefix)
    {
        foreach (var key in table.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) table.TryRemove(key, out _);
    }
    private static void RemoveWhere(ConcurrentDictionary<string, JsonElement> table, string prefix, Func<JsonElement, bool> predicate)
    {
        foreach (var kv in table.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && predicate(kv.Value)).ToList()) table.TryRemove(kv.Key, out _);
    }

    private static void Bump(ConcurrentDictionary<string, ConcurrentDictionary<string, long>> table, string key, string[] fields, long[] deltas)
    {
        var row = table.GetOrAdd(key, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
        for (var i = 0; i < fields.Length; i++)
            row.AddOrUpdate(fields[i], deltas[i], (_, current) => current + deltas[i]);
    }

    private static JsonElement CounterRow(string? keyName, string? keyValue, ConcurrentDictionary<string, long> row, string[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (keyName is not null) values[keyName] = keyValue;
        foreach (var field in fields) values[field] = row.GetValueOrDefault(field);
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement WithoutProperty(JsonElement row, string name)
        => JsonSerializer.SerializeToElement(row.EnumerateObject().Where(p => p.Name != name).ToDictionary(p => p.Name, p => p.Value));

    /// <summary>Production <c>ParseJsonColumn</c>: null/blank/corrupt text yields null.</summary>
    private static JsonElement? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string Serialize(JsonElement element, string resourceType)
    {
        var str = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(str) > PayloadLimitBytes)
            throw new ArgumentException($"Payload exceeds {PayloadLimitBytes} bytes for {resourceType}.");
        return str;
    }

    private static void Id(string value)
    {
        if (string.IsNullOrEmpty(value) || !IdPattern.IsMatch(value))
            throw new ArgumentException($"Invalid ID '{value}': must match ^[a-zA-Z0-9_-]{{1,128}}$.");
    }

    private static void RecordKey(string value)
    {
        if (string.IsNullOrEmpty(value) || !RecordKeyPattern.IsMatch(value))
            throw new ArgumentException($"Invalid record key '{value}': must match ^[a-zA-Z0-9_:-]{{1,256}}$.");
    }

    private static void Limit(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), limit, "LIMIT must be strictly positive.");
    }
}
