using System.Text.Json;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// Base class for test-only INetworkStorageStore fakes. Every method throws
/// <see cref="NotImplementedException"/> by default — override only the methods
/// the test needs. This eliminates the 42-method boilerplate that makes
/// writing fakes error-prone (the interface gained V4 analytics methods,
/// pages, storage_errors, storage_request_log, etc.).
/// </summary>
public class EmptyNetworkStorageStore : INetworkStorageStore
{
    public virtual int MaxPayloadBytes => 64 * 1024;

    public virtual Task UpsertProjectAsync(string projectId, JsonElement payload, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteProjectAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertCollectionAsync(string projectId, string collectionId, string name, string visibility, JsonElement definitionJson, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteCollectionAsync(string projectId, string collectionId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertEndpointAsync(string projectId, string endpointId, string slug, string method, bool enabled, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadEndpointAsync(string projectId, string endpointId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteEndpointAsync(string projectId, string endpointId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertWorkflowAsync(string projectId, string workflowId, string name, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadWorkflowAsync(string projectId, string workflowId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteWorkflowAsync(string projectId, string workflowId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertGameValuesAsync(string projectId, JsonElement payloadJson, string? versionHash, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteGameValuesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertRateLimitRulesAsync(string projectId, JsonElement rulesJson, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadRateLimitRulesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteRateLimitRulesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertQueryAsync(string projectId, string queryId, string name, bool requiresSecretKey, JsonElement definitionJson, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteQueryAsync(string projectId, string queryId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task RecordQueryRunAsync(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadQueryLastRunAsync(string projectId, string queryId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListQueryLastRunsAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListQueryLogsAsync(string projectId, string queryId, int limit, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertRecordAsync(string projectId, string collectionId, string recordKey, JsonElement payloadJson, bool deleted, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, long resultRecordVersion, string resultHash, JsonElement payloadJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertGlobalRecordAsync(string projectId, string collectionId, string recordId, JsonElement payloadJson, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertLedgerEntryAsync(string projectId, string collectionId, string recordKey, long sequence, JsonElement entryJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertCheckpointCursorAsync(string projectId, long latestSequence, string? manifestPath, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadCheckpointCursorAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteCheckpointCursorAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertApiKeyAsync(string projectId, string apiKey, string userId, string keyType, string keyHash, string keyIdentifier, string label, bool enabled, JsonElement permissionsJson, long version, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteApiKeyAsync(string projectId, string apiKey, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertAuditLogAsync(string projectId, long createdAtUnixMs, string logId, string userId, string action, string actorJson, string targetJson, string summaryJson, string diffJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListAuditLogsAsync(string projectId, int limit, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteAuditLogsAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertPlayerAnalyticsEventAsync(string projectId, string collectionId, string recordKey, long createdAtUnixMs, string eventId, string eventType, JsonElement payloadJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListPlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, int limit, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeletePlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertPlayerAnalyticsEventV2Async(string projectId, string steamId, long createdAtUnixMs, string eventId, string eventType, string category, string label, string endpointSlug, string collectionId, JsonElement payloadJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListPlayerEventsAsync(string projectId, string steamId, long fromUnixMs, long toUnixMs, int limit, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<long> CountPlayerEventsAsync(string projectId, string steamId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertPlayerProfileAsync(string projectId, string steamId, string playerName, bool isOnline, long? onlineSinceUnixMs, long lastSeenUnixMs, long? lastHeartbeatUnixMs, string? currentSessionId, long? currentSessionLastSeconds, long totalSeconds, long sessionCount, string? lastEventType, string? lastEndpointSlug, string managedCountersJson, long updatedAtUnixMs, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadPlayerProfileAsync(string projectId, string steamId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ReadProjectProfilesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertPlayerSessionAsync(string projectId, string steamId, string sessionId, long? startedAtUnixMs, long? lastHeartbeatAtUnixMs, long? endedAtUnixMs, string? lastMetricsJson, string? summaryJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadPlayerSessionAsync(string projectId, string steamId, string sessionId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertProjectIssueAsync(string projectId, string bucketDate, long createdAtUnixMs, string eventId, string steamId, string category, string eventType, string label, JsonElement payloadJson, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListProjectIssuesAsync(string projectId, string bucketDate, int limit, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertProjectMembershipAsync(string userId, string projectId, string role, long createdAtUnixMs, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListProjectsForUserAsync(string userId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeleteProjectMembershipAsync(string userId, string projectId, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task UpsertPageAsync(string projectId, string pageSlug, string title, string contentJson, long createdAtUnixMs, long updatedAtUnixMs, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<JsonElement?> ReadPageAsync(string projectId, string pageSlug, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListPagesAsync(string projectId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeletePageAsync(string projectId, string pageSlug, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertStorageErrorAsync(string projectId, long createdAtUnixMs, string errorId, string message, string? stackTrace, string? source, string? requestPath, string severity, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListStorageErrorsAsync(string projectId, int limit, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task PurgeStorageErrorsAsync(string projectId, long beforeUnixMs, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task InsertStorageRequestLogAsync(string projectId, long createdAtUnixMs, string method, string path, int statusCode, int durationMs, string? apiKeyIdentifier, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<JsonElement>> ListStorageRequestLogAsync(string projectId, int limit, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task PurgeStorageRequestLogAsync(string projectId, long beforeUnixMs, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<JsonElement?> ReadProjectUsageMonthlyAsync(string projectId, string month, CancellationToken ct) => Task.FromResult<JsonElement?>(null);
    public virtual Task<long> ReadProjectStorageBytesAsync(string projectId, CancellationToken ct) => Task.FromResult(0L);
    public virtual Task<IReadOnlyList<JsonElement>> ReadProjectUsageDailyAsync(string projectId, string month, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonElement>>(Array.Empty<JsonElement>());
    public virtual Task<IReadOnlyList<JsonElement>> ReadProjectUsageEndpointsAsync(string projectId, string month, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonElement>>(Array.Empty<JsonElement>());
    public virtual Task PutWorkspaceObjectAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<string?> ReadWorkspaceObjectAsync(string path, CancellationToken ct) => Task.FromResult<string?>(null);
    public virtual Task DeleteWorkspaceObjectAsync(string path, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<IReadOnlyList<WorkspaceObjectEntry>> ListWorkspaceObjectsAsync(string directoryPath, CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkspaceObjectEntry>>(Array.Empty<WorkspaceObjectEntry>());
}
