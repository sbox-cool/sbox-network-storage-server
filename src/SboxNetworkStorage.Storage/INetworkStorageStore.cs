using System.Text.Json;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// Abstraction over all 17 ScyllaDB Network Storage tables. The real
/// implementation is <see cref="ScyllaDbResourceStore"/>; tests use
/// <c>InMemoryNetworkStorageStore</c>. Every method validates inputs before any
/// CQL execution and stamps <c>version</c>/<c>updated_at_unix_ms</c>
/// on writes.
/// </summary>
public interface INetworkStorageStore
{
    /// <summary>Maximum accepted JSON payload size in UTF-8 bytes, matching the configured store limit.</summary>
    int MaxPayloadBytes { get; }

    /// <summary>
    /// Opens a transaction. Every call on <see cref="IStoreTransaction.Store"/> joins it; nothing is visible to
    /// other callers until <see cref="IStoreTransaction.CommitAsync"/> succeeds, and disposing the transaction
    /// without committing discards every write. Calls on this store instance never join the transaction.
    /// </summary>
    Task<IStoreTransaction> BeginTransactionAsync(CancellationToken ct);

    // ── projects ────────────────────────────────────────────────────
    Task UpsertProjectAsync(string projectId, JsonElement payload, long version, CancellationToken ct);
    Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct);
    Task DeleteProjectAsync(string projectId, CancellationToken ct);

    // ── collections ─────────────────────────────────────────────────
    Task UpsertCollectionAsync(string projectId, string collectionId, string name, string visibility, JsonElement definitionJson, long version, CancellationToken ct);
    Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct);
    Task DeleteCollectionAsync(string projectId, string collectionId, CancellationToken ct);

    // ── endpoints ───────────────────────────────────────────────────
    Task UpsertEndpointAsync(string projectId, string endpointId, string slug, string method, bool enabled, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct);
    Task<JsonElement?> ReadEndpointAsync(string projectId, string endpointId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct);
    Task DeleteEndpointAsync(string projectId, string endpointId, CancellationToken ct);

    // ── workflows ───────────────────────────────────────────────────
    Task UpsertWorkflowAsync(string projectId, string workflowId, string name, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct);
    Task<JsonElement?> ReadWorkflowAsync(string projectId, string workflowId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct);
    Task DeleteWorkflowAsync(string projectId, string workflowId, CancellationToken ct);

    // ── game_values ─────────────────────────────────────────────────
    Task UpsertGameValuesAsync(string projectId, JsonElement payloadJson, string? versionHash, long version, CancellationToken ct);
    Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct);
    Task DeleteGameValuesAsync(string projectId, CancellationToken ct);

    // ── rate_limit_rules ────────────────────────────────────────────
    Task UpsertRateLimitRulesAsync(string projectId, JsonElement rulesJson, long version, CancellationToken ct);
    Task<JsonElement?> ReadRateLimitRulesAsync(string projectId, CancellationToken ct);
    Task DeleteRateLimitRulesAsync(string projectId, CancellationToken ct);

    // ── queries ─────────────────────────────────────────────────────
    Task UpsertQueryAsync(string projectId, string queryId, string name, bool requiresSecretKey, JsonElement definitionJson, long version, CancellationToken ct);
    Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct);
    Task DeleteQueryAsync(string projectId, string queryId, CancellationToken ct);
    // ── query run tracking (V6 — query_last_run + query_run_logs) ───
    /// <summary>Record a query execution: upserts the latest-run row and appends a log entry. Best-effort — never throws on failure.</summary>
    Task RecordQueryRunAsync(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, CancellationToken ct);
    /// <summary>Read the latest run info for a query, or null if it has never run.</summary>
    Task<JsonElement?> ReadQueryLastRunAsync(string projectId, string queryId, CancellationToken ct);
    /// <summary>Read the latest run info for every query in a project (single partition). Empty when none have run.</summary>
    Task<IReadOnlyList<JsonElement>> ListQueryLastRunsAsync(string projectId, CancellationToken ct);
    /// <summary>Read the run/edit history for a query (newest first), limited to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<JsonElement>> ListQueryLogsAsync(string projectId, string queryId, int limit, CancellationToken ct);

    // ── records ─────────────────────────────────────────────────────
    Task UpsertRecordAsync(string projectId, string collectionId, string recordKey, JsonElement payloadJson, bool deleted, long version, CancellationToken ct);
    Task<JsonElement?> ReadRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct);
    Task DeleteRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct);

    /// <summary>
    /// Atomically saves or deletes a record only when its current version matches.
    /// An optional trusted snapshot additionally checks exact stored payload and timestamp.
    /// A null expected version creates an absent record (or revives a player tombstone);
    /// a non-null version requires a live record. Successful saves/tombstones increment
    /// that row's version; global deletion removes the row. Returns false on conflict.
    /// </summary>
    Task<bool> TryMutateRecordAsync(string projectId, string collectionId, string recordKey,
        bool global, JsonElement payloadJson, bool delete, long? expectedVersion, CancellationToken ct,
        RecordMutationSnapshot? snapshot = null);

    // ── record_idempotency ──────────────────────────────────────────
    Task UpsertRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, long resultRecordVersion, string resultHash, JsonElement payloadJson, CancellationToken ct);
    Task<JsonElement?> ReadRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct);
    Task DeleteRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct);

    // ── global_records ──────────────────────────────────────────────
    Task UpsertGlobalRecordAsync(string projectId, string collectionId, string recordId, JsonElement payloadJson, long version, CancellationToken ct);
    Task<JsonElement?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct);
    Task DeleteGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct);

    // ── ledger_entries ──────────────────────────────────────────────
    Task InsertLedgerEntryAsync(string projectId, string collectionId, string recordKey, long sequence, JsonElement entryJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct);
    Task DeleteLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct);

    // ── checkpoint_cursor ───────────────────────────────────────────
    Task UpsertCheckpointCursorAsync(string projectId, long latestSequence, string? manifestPath, long version, CancellationToken ct);
    Task<JsonElement?> ReadCheckpointCursorAsync(string projectId, CancellationToken ct);
    Task DeleteCheckpointCursorAsync(string projectId, CancellationToken ct);

    // ── api_keys ────────────────────────────────────────────────────
    Task UpsertApiKeyAsync(string projectId, string apiKey, string userId, string keyType, string keyHash, string keyIdentifier, string label, bool enabled, JsonElement permissionsJson, long version, CancellationToken ct);
    Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct);
    Task DeleteApiKeyAsync(string projectId, string apiKey, CancellationToken ct);

    // ── project_audit_logs ──────────────────────────────────────────
    Task InsertAuditLogAsync(string projectId, long createdAtUnixMs, string logId, string userId, string action, string actorJson, string targetJson, string summaryJson, string diffJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListAuditLogsAsync(string projectId, int limit, CancellationToken ct);
    Task DeleteAuditLogsAsync(string projectId, CancellationToken ct);

    // ── player_analytics (legacy — superseded by V4 tables) ─────────
    Task InsertPlayerAnalyticsEventAsync(string projectId, string collectionId, string recordKey, long createdAtUnixMs, string eventId, string eventType, JsonElement payloadJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListPlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, int limit, CancellationToken ct);
    Task DeletePlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, CancellationToken ct);

    /// <summary>
    /// Deletes timeline events, project issues and legacy analytics rows whose <c>created_at_unix_ms</c> is
    /// strictly before <paramref name="beforeUnixMs"/> across all projects. Returns the number of rows deleted.
    /// Profiles and sessions are state, not history, and are kept.
    /// </summary>
    Task<long> PurgeAnalyticsBeforeAsync(long beforeUnixMs, CancellationToken ct);

    // ── player_analytics_events (V4 — per-player event timeline) ─────
    Task InsertPlayerAnalyticsEventV2Async(string projectId, string steamId, long createdAtUnixMs, string eventId, string eventType, string category, string label, string endpointSlug, string collectionId, JsonElement payloadJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListPlayerEventsAsync(string projectId, string steamId, long fromUnixMs, long toUnixMs, int limit, CancellationToken ct);
    /// <summary>Exact count of all stored events for a player (reliable; not a denormalized counter).</summary>
    Task<long> CountPlayerEventsAsync(string projectId, string steamId, CancellationToken ct);

    // ── player_profiles (V4 — per-player presence/profile) ───────────
    Task UpsertPlayerProfileAsync(string projectId, string steamId, string playerName, bool isOnline, long? onlineSinceUnixMs, long lastSeenUnixMs, long? lastHeartbeatUnixMs, string? currentSessionId, long? currentSessionLastSeconds, long totalSeconds, long sessionCount, string? lastEventType, string? lastEndpointSlug, string managedCountersJson, long updatedAtUnixMs, CancellationToken ct);
    Task<JsonElement?> ReadPlayerProfileAsync(string projectId, string steamId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ReadProjectProfilesAsync(string projectId, CancellationToken ct);

    // ── player_sessions (V4 — per-player session state) ──────────────
    Task InsertPlayerSessionAsync(string projectId, string steamId, string sessionId, long? startedAtUnixMs, long? lastHeartbeatAtUnixMs, long? endedAtUnixMs, string? lastMetricsJson, string? summaryJson, CancellationToken ct);
    Task<JsonElement?> ReadPlayerSessionAsync(string projectId, string steamId, string sessionId, CancellationToken ct);

    // ── project_analytics_issues (V4 — project issues/incidents) ─────
    Task InsertProjectIssueAsync(string projectId, string bucketDate, long createdAtUnixMs, string eventId, string steamId, string category, string eventType, string label, JsonElement payloadJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListProjectIssuesAsync(string projectId, string bucketDate, int limit, CancellationToken ct);

    // ── project_members ──────────────────────────────────────────────
    Task UpsertProjectMembershipAsync(string userId, string projectId, string role, long createdAtUnixMs, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListProjectsForUserAsync(string userId, CancellationToken ct);
    Task DeleteProjectMembershipAsync(string userId, string projectId, CancellationToken ct);

    // ── pages ────────────────────────────────────────────────────────
    Task UpsertPageAsync(string projectId, string pageSlug, string title, string contentJson, long createdAtUnixMs, long updatedAtUnixMs, CancellationToken ct);
    Task<JsonElement?> ReadPageAsync(string projectId, string pageSlug, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListPagesAsync(string projectId, CancellationToken ct);
    Task DeletePageAsync(string projectId, string pageSlug, CancellationToken ct);

    // ── storage_errors ────────────────────────────────────────────────
    Task InsertStorageErrorAsync(string projectId, long createdAtUnixMs, string errorId, string message, string? stackTrace, string? source, string? requestPath, string severity, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListStorageErrorsAsync(string projectId, int limit, CancellationToken ct);
    Task PurgeStorageErrorsAsync(string projectId, long beforeUnixMs, CancellationToken ct);

    // ── storage_request_log ───────────────────────────────────────────
    Task InsertStorageRequestLogAsync(string projectId, long createdAtUnixMs, string method, string path, int statusCode, int durationMs, string? apiKeyIdentifier, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListStorageRequestLogAsync(string projectId, int limit, CancellationToken ct);
    Task PurgeStorageRequestLogAsync(string projectId, long beforeUnixMs, CancellationToken ct);

    // ── project_usage_monthly / daily / endpoints (V5 — usage counters) ──

    /// <summary>
    /// Atomically increment a project's usage counters for the given UTC month
    /// (and day, when provided). The endpointSlug, when non-null and non-empty,
    /// additionally increments a row in <c>project_usage_endpoints</c>. All
    /// counter increments are additive (<c>UPDATE … SET c = c + ?</c>), so
    /// concurrent flushes from different workers merge safely.
    /// </summary>
    /// <param name="month">UTC month key (<c>yyyy-MM</c>).</param>
    /// <param name="day">UTC day key (<c>yyyy-MM-dd</c>) for the daily series row.</param>
    /// <param name="endpointSlug">Optional endpoint slug for per-endpoint counters.</param>
    Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct);

    /// <summary>Read the monthly usage counters row, or null when no row exists.</summary>
    Task<JsonElement?> ReadProjectUsageMonthlyAsync(string projectId, string month, CancellationToken ct);
    /// <summary>Read the cumulative logical storage delta across all metered months.</summary>
    Task<long> ReadProjectStorageBytesAsync(string projectId, CancellationToken ct);


    /// <summary>Read the per-day usage series for a month (ordered by day ascending).</summary>
    Task<IReadOnlyList<JsonElement>> ReadProjectUsageDailyAsync(string projectId, string month, CancellationToken ct);

    /// <summary>Read the per-endpoint usage rows for a month, limited to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<JsonElement>> ReadProjectUsageEndpointsAsync(string projectId, string month, int limit, CancellationToken ct);

    // ── workspace_objects (self-host replacement for the managed workspace object bucket) ──

    /// <summary>Create or overwrite the object stored at <paramref name="path"/> (slash-separated, no leading slash).</summary>
    Task PutWorkspaceObjectAsync(string path, string content, CancellationToken ct);
    /// <summary>Read the object at <paramref name="path"/>, or null when it does not exist.</summary>
    Task<string?> ReadWorkspaceObjectAsync(string path, CancellationToken ct);
    /// <summary>Delete the object at <paramref name="path"/>; deleting a missing object is a no-op.</summary>
    Task DeleteWorkspaceObjectAsync(string path, CancellationToken ct);
    /// <summary>
    /// List the immediate children of <paramref name="directoryPath"/> (trailing slash optional):
    /// objects directly under it and one directory entry per deeper sub-path, ordered by name (ordinal).
    /// Returns an empty list when nothing exists under the directory.
    /// </summary>
    Task<IReadOnlyList<WorkspaceObjectEntry>> ListWorkspaceObjectsAsync(string directoryPath, CancellationToken ct);
}

/// <summary>A store transaction. Dispose without committing to roll back.</summary>
public interface IStoreTransaction : IAsyncDisposable
{
    /// <summary>A store whose every operation runs inside this transaction.</summary>
    INetworkStorageStore Store { get; }

    /// <summary>Makes every write made through <see cref="Store"/> durable and visible. Call at most once.</summary>
    Task CommitAsync(CancellationToken ct);
}

/// <summary>Trusted observed record state for comparisons against writers that reuse versions.</summary>
public sealed record RecordMutationSnapshot(string PayloadJson, long? ChangedAtUnixMs);

/// <summary>A direct child of a workspace object directory listing.</summary>
public sealed record WorkspaceObjectEntry(
    string Name,
    bool IsDirectory,
    long? LengthBytes,
    DateTimeOffset? LastChanged);

/// <summary>
/// Additive counter deltas for a single flush batch. Each field is added to the
/// corresponding counter column. Zero-valued fields are safe (counter tables
/// treat <c>c + 0</c> as a no-op write but still touch the row, which is fine).
/// </summary>
public sealed record UsageDelta(
    long Requests,
    long Reads,
    long Writes,
    long EndpointCalls,
    long BytesIn,
    long BytesOut,
    long Errors,
    long DurationMsSum,
    long DurationSamples,
    long StorageDeltaBytes,
    long ComputeUnits = 0);
