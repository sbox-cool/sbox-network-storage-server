using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

/// <summary>
/// Serves collection, endpoint and game-value reads from the project's <see cref="ProjectMetadataSnapshot"/>
/// and invalidates it on every write that can change them, including project deletion and imports. The same
/// writes, plus record and player-profile writes, are reported to <see cref="QueryResultCache"/> so cached
/// query results follow the data. Every other operation passes straight through. Identifiers the store would
/// reject are passed through too, so callers see the store's own validation errors.
/// </summary>
public sealed class MetadataCachingNetworkStore : INetworkStorageStore, IProjectImportStore, IAuthoritativeProjectStore
{
    private readonly INetworkStorageStore _inner;
    private readonly ProjectMetadataCache _cache;
    private readonly QueryResultCache _queries;
    // Set on a transaction's store: writes are recorded instead of invalidating, because nothing is visible
    // until commit, and reads bypass the snapshot so they see the transaction's own writes.
    private readonly PendingChanges? _pending;

    public MetadataCachingNetworkStore(INetworkStorageStore inner, ProjectMetadataCache cache, QueryResultCache queries)
        : this(inner, cache, queries, null)
    {
    }

    private MetadataCachingNetworkStore(INetworkStorageStore inner, ProjectMetadataCache cache, QueryResultCache queries, PendingChanges? pending)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _pending = pending;
    }

    private bool Cacheable(string projectId, string? id = null)
        => _pending is null
           && StorageIdValidation.IsValidCollectionId(projectId)
           && (id is null || StorageIdValidation.IsValidCollectionId(id));

    private async Task Changed(Task write, string projectId)
    {
        await write;
        if (_pending is null) DefinitionsChanged(projectId);
        else lock (_pending) _pending.Definitions.Add(projectId);
    }

    private void DefinitionsChanged(string projectId)
    {
        _cache.Invalidate(projectId);
        _queries.DefinitionsChanged(projectId);
    }

    private async Task RecordsChanged(Task write, string projectId)
    {
        await write;
        RecordsChanged(projectId);
    }

    private async Task<bool> RecordsChanged(Task<bool> write, string projectId)
    {
        var applied = await write;
        if (applied) RecordsChanged(projectId);
        return applied;
    }

    private void RecordsChanged(string projectId)
    {
        if (_pending is null) _queries.DataChanged(projectId);
        else lock (_pending) _pending.Records.Add(projectId);
    }

    public async Task<IStoreTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        var pending = new PendingChanges();
        var transaction = await _inner.BeginTransactionAsync(ct);
        return new CachingTransaction(transaction, new MetadataCachingNetworkStore(transaction.Store, _cache, _queries, pending), this, pending);
    }

    private sealed class PendingChanges
    {
        public readonly HashSet<string> Definitions = new(StringComparer.Ordinal);
        public readonly HashSet<string> Records = new(StringComparer.Ordinal);
    }

    private sealed class CachingTransaction(IStoreTransaction inner, INetworkStorageStore store, MetadataCachingNetworkStore owner, PendingChanges pending) : IStoreTransaction
    {
        public INetworkStorageStore Store => store;

        public async Task CommitAsync(CancellationToken ct)
        {
            await inner.CommitAsync(ct);
            lock (pending)
            {
                foreach (var projectId in pending.Definitions) owner.DefinitionsChanged(projectId);
                foreach (var projectId in pending.Records) owner.RecordsChanged(projectId);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // ── imports ─────────────────────────────────────────────────────

    public async Task<bool> TryImportProjectAsync(string projectId, Func<INetworkStorageStore, CancellationToken, Task> restore, CancellationToken ct)
    {
        if (_inner is not IProjectImportStore importer)
            throw new NotSupportedException("The underlying store does not support atomic project imports.");
        try
        {
            return await importer.TryImportProjectAsync(projectId, restore, ct);
        }
        finally
        {
            if (StorageIdValidation.IsValidCollectionId(projectId)) DefinitionsChanged(projectId);
        }
    }

    public IAsyncEnumerable<ProjectSnapshotRow> ExportProjectRowsAsync(string projectId, CancellationToken ct)
        => Authoritative().ExportProjectRowsAsync(projectId, ct);

    public async Task ReplaceProjectRowsAsync(string projectId, IAsyncEnumerable<ProjectSnapshotRow> rows, CancellationToken ct)
    {
        try
        {
            await Authoritative().ReplaceProjectRowsAsync(projectId, rows, ct);
        }
        finally
        {
            if (StorageIdValidation.IsValidCollectionId(projectId)) DefinitionsChanged(projectId);
        }
    }

    private IAuthoritativeProjectStore Authoritative()
        => _inner as IAuthoritativeProjectStore ?? throw new NotSupportedException("The underlying store does not support project snapshots.");

    // ── cached metadata reads, invalidating writes ──────────────────
    /// <summary>Returns the endpoint's cached value-model definition, parsed once per metadata generation.</summary>
    public async Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string slug, CancellationToken ct)
        => (await _cache.GetAsync(_inner, projectId, ct)).EndpointDefinition(slug);


    public Task UpsertCollectionAsync(string projectId, string collectionId, string name, string visibility, JsonElement definitionJson, long version, CancellationToken ct)
        => Changed(_inner.UpsertCollectionAsync(projectId, collectionId, name, visibility, definitionJson, version, ct), projectId);

    public Task DeleteCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        => Changed(_inner.DeleteCollectionAsync(projectId, collectionId, ct), projectId);

    public async Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        => Cacheable(projectId, collectionId)
            ? (await _cache.GetAsync(_inner, projectId, ct)).Collection(collectionId)
            : await _inner.ReadCollectionAsync(projectId, collectionId, ct);

    public async Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct)
        => Cacheable(projectId)
            ? (await _cache.GetAsync(_inner, projectId, ct)).Collections
            : await _inner.ListCollectionsAsync(projectId, ct);

    public Task UpsertEndpointAsync(string projectId, string endpointId, string slug, string method, bool enabled, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct)
        => Changed(_inner.UpsertEndpointAsync(projectId, endpointId, slug, method, enabled, definitionJson, versionHash, version, ct), projectId);

    public Task DeleteEndpointAsync(string projectId, string endpointId, CancellationToken ct)
        => Changed(_inner.DeleteEndpointAsync(projectId, endpointId, ct), projectId);

    public async Task<JsonElement?> ReadEndpointAsync(string projectId, string endpointId, CancellationToken ct)
        => Cacheable(projectId, endpointId)
            ? (await _cache.GetAsync(_inner, projectId, ct)).Endpoint(endpointId)
            : await _inner.ReadEndpointAsync(projectId, endpointId, ct);

    public async Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct)
        => Cacheable(projectId)
            ? (await _cache.GetAsync(_inner, projectId, ct)).Endpoints
            : await _inner.ListEndpointsAsync(projectId, ct);

    public Task UpsertGameValuesAsync(string projectId, JsonElement payloadJson, string? versionHash, long version, CancellationToken ct)
        => Changed(_inner.UpsertGameValuesAsync(projectId, payloadJson, versionHash, version, ct), projectId);

    public Task DeleteGameValuesAsync(string projectId, CancellationToken ct)
        => Changed(_inner.DeleteGameValuesAsync(projectId, ct), projectId);

    public async Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct)
        => Cacheable(projectId)
            ? (await _cache.GetAsync(_inner, projectId, ct)).GameValues
            : await _inner.ReadGameValuesAsync(projectId, ct);

    public Task DeleteProjectAsync(string projectId, CancellationToken ct)
        => Changed(_inner.DeleteProjectAsync(projectId, ct), projectId);

    // ── pass-through ────────────────────────────────────────────────

    public int MaxPayloadBytes => _inner.MaxPayloadBytes;
    public Task UpsertProjectAsync(string projectId, JsonElement payload, long version, CancellationToken ct) => Changed(_inner.UpsertProjectAsync(projectId, payload, version, ct), projectId);
    public Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct) => _inner.ReadProjectAsync(projectId, ct);
    public Task UpsertWorkflowAsync(string projectId, string workflowId, string name, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct) => Changed(_inner.UpsertWorkflowAsync(projectId, workflowId, name, definitionJson, versionHash, version, ct), projectId);
    public Task<JsonElement?> ReadWorkflowAsync(string projectId, string workflowId, CancellationToken ct) => _inner.ReadWorkflowAsync(projectId, workflowId, ct);
    public Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct) => _inner.ListWorkflowsAsync(projectId, ct);
    public Task DeleteWorkflowAsync(string projectId, string workflowId, CancellationToken ct) => Changed(_inner.DeleteWorkflowAsync(projectId, workflowId, ct), projectId);
    public Task UpsertRateLimitRulesAsync(string projectId, JsonElement rulesJson, long version, CancellationToken ct) => Changed(_inner.UpsertRateLimitRulesAsync(projectId, rulesJson, version, ct), projectId);
    public Task<JsonElement?> ReadRateLimitRulesAsync(string projectId, CancellationToken ct) => _inner.ReadRateLimitRulesAsync(projectId, ct);
    public Task DeleteRateLimitRulesAsync(string projectId, CancellationToken ct) => Changed(_inner.DeleteRateLimitRulesAsync(projectId, ct), projectId);
    public Task UpsertQueryAsync(string projectId, string queryId, string name, bool requiresSecretKey, JsonElement definitionJson, long version, CancellationToken ct) => Changed(_inner.UpsertQueryAsync(projectId, queryId, name, requiresSecretKey, definitionJson, version, ct), projectId);
    public Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct) => _inner.ReadQueryAsync(projectId, queryId, ct);
    public Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct) => _inner.ListQueriesAsync(projectId, ct);
    public Task DeleteQueryAsync(string projectId, string queryId, CancellationToken ct) => Changed(_inner.DeleteQueryAsync(projectId, queryId, ct), projectId);
    public Task RecordQueryRunAsync(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, CancellationToken ct) => _inner.RecordQueryRunAsync(projectId, queryId, runAtIso, durationMs, keysScanned, recordsReturned, fromCache, ct);
    public Task<JsonElement?> ReadQueryLastRunAsync(string projectId, string queryId, CancellationToken ct) => _inner.ReadQueryLastRunAsync(projectId, queryId, ct);
    public Task<IReadOnlyList<JsonElement>> ListQueryLastRunsAsync(string projectId, CancellationToken ct) => _inner.ListQueryLastRunsAsync(projectId, ct);
    public Task<IReadOnlyList<JsonElement>> ListQueryLogsAsync(string projectId, string queryId, int limit, CancellationToken ct) => _inner.ListQueryLogsAsync(projectId, queryId, limit, ct);
    public Task UpsertRecordAsync(string projectId, string collectionId, string recordKey, JsonElement payloadJson, bool deleted, long version, CancellationToken ct) => RecordsChanged(_inner.UpsertRecordAsync(projectId, collectionId, recordKey, payloadJson, deleted, version, ct), projectId);
    public Task<JsonElement?> ReadRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => _inner.ReadRecordAsync(projectId, collectionId, recordKey, ct);
    public Task<IReadOnlyList<JsonElement>> ListRecordsAsync(string projectId, string collectionId, CancellationToken ct) => _inner.ListRecordsAsync(projectId, collectionId, ct);
    public Task DeleteRecordAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => RecordsChanged(_inner.DeleteRecordAsync(projectId, collectionId, recordKey, ct), projectId);
    public Task<bool> TryMutateRecordAsync(string projectId, string collectionId, string recordKey, bool global, JsonElement payloadJson, bool delete, long? expectedVersion, CancellationToken ct, RecordMutationSnapshot? snapshot = null) => RecordsChanged(_inner.TryMutateRecordAsync(projectId, collectionId, recordKey, global, payloadJson, delete, expectedVersion, ct, snapshot), projectId);
    public Task UpsertRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, long resultRecordVersion, string resultHash, JsonElement payloadJson, CancellationToken ct) => _inner.UpsertRecordIdempotencyAsync(projectId, collectionId, recordKey, idempotencyKey, resultRecordVersion, resultHash, payloadJson, ct);
    public Task<JsonElement?> ReadRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) => _inner.ReadRecordIdempotencyAsync(projectId, collectionId, recordKey, idempotencyKey, ct);
    public Task DeleteRecordIdempotencyAsync(string projectId, string collectionId, string recordKey, string idempotencyKey, CancellationToken ct) => _inner.DeleteRecordIdempotencyAsync(projectId, collectionId, recordKey, idempotencyKey, ct);
    public Task UpsertGlobalRecordAsync(string projectId, string collectionId, string recordId, JsonElement payloadJson, long version, CancellationToken ct) => RecordsChanged(_inner.UpsertGlobalRecordAsync(projectId, collectionId, recordId, payloadJson, version, ct), projectId);
    public Task<JsonElement?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) => _inner.ReadGlobalRecordAsync(projectId, collectionId, recordId, ct);
    public Task<IReadOnlyList<JsonElement>> ListGlobalRecordsAsync(string projectId, string collectionId, CancellationToken ct) => _inner.ListGlobalRecordsAsync(projectId, collectionId, ct);
    public Task DeleteGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct) => RecordsChanged(_inner.DeleteGlobalRecordAsync(projectId, collectionId, recordId, ct), projectId);
    public Task InsertLedgerEntryAsync(string projectId, string collectionId, string recordKey, long sequence, JsonElement entryJson, CancellationToken ct) => _inner.InsertLedgerEntryAsync(projectId, collectionId, recordKey, sequence, entryJson, ct);
    public Task<IReadOnlyList<JsonElement>> ListLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => _inner.ListLedgerEntriesAsync(projectId, collectionId, recordKey, ct);
    public Task DeleteLedgerEntriesAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => _inner.DeleteLedgerEntriesAsync(projectId, collectionId, recordKey, ct);
    public Task UpsertCheckpointCursorAsync(string projectId, long latestSequence, string? manifestPath, long version, CancellationToken ct) => _inner.UpsertCheckpointCursorAsync(projectId, latestSequence, manifestPath, version, ct);
    public Task<JsonElement?> ReadCheckpointCursorAsync(string projectId, CancellationToken ct) => _inner.ReadCheckpointCursorAsync(projectId, ct);
    public Task DeleteCheckpointCursorAsync(string projectId, CancellationToken ct) => _inner.DeleteCheckpointCursorAsync(projectId, ct);
    public Task UpsertApiKeyAsync(string projectId, string apiKey, string userId, string keyType, string keyHash, string keyIdentifier, string label, bool enabled, JsonElement permissionsJson, long version, CancellationToken ct) => Changed(_inner.UpsertApiKeyAsync(projectId, apiKey, userId, keyType, keyHash, keyIdentifier, label, enabled, permissionsJson, version, ct), projectId);
    public Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct) => _inner.ReadApiKeyAsync(projectId, apiKey, ct);
    public Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct) => _inner.ListApiKeysAsync(projectId, ct);
    public Task DeleteApiKeyAsync(string projectId, string apiKey, CancellationToken ct) => Changed(_inner.DeleteApiKeyAsync(projectId, apiKey, ct), projectId);
    public Task InsertAuditLogAsync(string projectId, long createdAtUnixMs, string logId, string userId, string action, string actorJson, string targetJson, string summaryJson, string diffJson, CancellationToken ct) => _inner.InsertAuditLogAsync(projectId, createdAtUnixMs, logId, userId, action, actorJson, targetJson, summaryJson, diffJson, ct);
    public Task<IReadOnlyList<JsonElement>> ListAuditLogsAsync(string projectId, int limit, CancellationToken ct) => _inner.ListAuditLogsAsync(projectId, limit, ct);
    public Task DeleteAuditLogsAsync(string projectId, CancellationToken ct) => _inner.DeleteAuditLogsAsync(projectId, ct);
    public Task InsertPlayerAnalyticsEventAsync(string projectId, string collectionId, string recordKey, long createdAtUnixMs, string eventId, string eventType, JsonElement payloadJson, CancellationToken ct) => _inner.InsertPlayerAnalyticsEventAsync(projectId, collectionId, recordKey, createdAtUnixMs, eventId, eventType, payloadJson, ct);
    public Task<IReadOnlyList<JsonElement>> ListPlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, int limit, CancellationToken ct) => _inner.ListPlayerAnalyticsEventsAsync(projectId, collectionId, recordKey, limit, ct);
    public Task DeletePlayerAnalyticsEventsAsync(string projectId, string collectionId, string recordKey, CancellationToken ct) => _inner.DeletePlayerAnalyticsEventsAsync(projectId, collectionId, recordKey, ct);
    public Task<long> PurgeAnalyticsBeforeAsync(long beforeUnixMs, CancellationToken ct) => _inner.PurgeAnalyticsBeforeAsync(beforeUnixMs, ct);
    public Task InsertPlayerAnalyticsEventV2Async(string projectId, string steamId, long createdAtUnixMs, string eventId, string eventType, string category, string label, string endpointSlug, string collectionId, JsonElement payloadJson, CancellationToken ct) => _inner.InsertPlayerAnalyticsEventV2Async(projectId, steamId, createdAtUnixMs, eventId, eventType, category, label, endpointSlug, collectionId, payloadJson, ct);
    public Task<IReadOnlyList<JsonElement>> ListPlayerEventsAsync(string projectId, string steamId, long fromUnixMs, long toUnixMs, int limit, CancellationToken ct) => _inner.ListPlayerEventsAsync(projectId, steamId, fromUnixMs, toUnixMs, limit, ct);
    public Task<long> CountPlayerEventsAsync(string projectId, string steamId, CancellationToken ct) => _inner.CountPlayerEventsAsync(projectId, steamId, ct);
    public Task UpsertPlayerProfileAsync(string projectId, string steamId, string playerName, bool isOnline, long? onlineSinceUnixMs, long lastSeenUnixMs, long? lastHeartbeatUnixMs, string? currentSessionId, long? currentSessionLastSeconds, long totalSeconds, long sessionCount, string? lastEventType, string? lastEndpointSlug, string managedCountersJson, long updatedAtUnixMs, CancellationToken ct) => RecordsChanged(_inner.UpsertPlayerProfileAsync(projectId, steamId, playerName, isOnline, onlineSinceUnixMs, lastSeenUnixMs, lastHeartbeatUnixMs, currentSessionId, currentSessionLastSeconds, totalSeconds, sessionCount, lastEventType, lastEndpointSlug, managedCountersJson, updatedAtUnixMs, ct), projectId);
    public Task<JsonElement?> ReadPlayerProfileAsync(string projectId, string steamId, CancellationToken ct) => _inner.ReadPlayerProfileAsync(projectId, steamId, ct);
    public Task<IReadOnlyList<JsonElement>> ReadProjectProfilesAsync(string projectId, CancellationToken ct) => _inner.ReadProjectProfilesAsync(projectId, ct);
    public Task InsertPlayerSessionAsync(string projectId, string steamId, string sessionId, long? startedAtUnixMs, long? lastHeartbeatAtUnixMs, long? endedAtUnixMs, string? lastMetricsJson, string? summaryJson, CancellationToken ct) => _inner.InsertPlayerSessionAsync(projectId, steamId, sessionId, startedAtUnixMs, lastHeartbeatAtUnixMs, endedAtUnixMs, lastMetricsJson, summaryJson, ct);
    public Task<JsonElement?> ReadPlayerSessionAsync(string projectId, string steamId, string sessionId, CancellationToken ct) => _inner.ReadPlayerSessionAsync(projectId, steamId, sessionId, ct);
    public Task InsertProjectIssueAsync(string projectId, string bucketDate, long createdAtUnixMs, string eventId, string steamId, string category, string eventType, string label, JsonElement payloadJson, CancellationToken ct) => _inner.InsertProjectIssueAsync(projectId, bucketDate, createdAtUnixMs, eventId, steamId, category, eventType, label, payloadJson, ct);
    public Task<IReadOnlyList<JsonElement>> ListProjectIssuesAsync(string projectId, string bucketDate, int limit, CancellationToken ct) => _inner.ListProjectIssuesAsync(projectId, bucketDate, limit, ct);
    public Task UpsertProjectMembershipAsync(string userId, string projectId, string role, long createdAtUnixMs, CancellationToken ct) => Changed(_inner.UpsertProjectMembershipAsync(userId, projectId, role, createdAtUnixMs, ct), projectId);
    public Task<IReadOnlyList<JsonElement>> ListProjectsForUserAsync(string userId, CancellationToken ct) => _inner.ListProjectsForUserAsync(userId, ct);
    public Task DeleteProjectMembershipAsync(string userId, string projectId, CancellationToken ct) => Changed(_inner.DeleteProjectMembershipAsync(userId, projectId, ct), projectId);
    public Task UpsertPageAsync(string projectId, string pageSlug, string title, string contentJson, long createdAtUnixMs, long updatedAtUnixMs, CancellationToken ct) => Changed(_inner.UpsertPageAsync(projectId, pageSlug, title, contentJson, createdAtUnixMs, updatedAtUnixMs, ct), projectId);
    public Task<JsonElement?> ReadPageAsync(string projectId, string pageSlug, CancellationToken ct) => _inner.ReadPageAsync(projectId, pageSlug, ct);
    public Task<IReadOnlyList<JsonElement>> ListPagesAsync(string projectId, CancellationToken ct) => _inner.ListPagesAsync(projectId, ct);
    public Task DeletePageAsync(string projectId, string pageSlug, CancellationToken ct) => Changed(_inner.DeletePageAsync(projectId, pageSlug, ct), projectId);
    public Task InsertStorageErrorAsync(string projectId, long createdAtUnixMs, string errorId, string message, string? stackTrace, string? source, string? requestPath, string severity, CancellationToken ct) => _inner.InsertStorageErrorAsync(projectId, createdAtUnixMs, errorId, message, stackTrace, source, requestPath, severity, ct);
    public Task<IReadOnlyList<JsonElement>> ListStorageErrorsAsync(string projectId, int limit, CancellationToken ct) => _inner.ListStorageErrorsAsync(projectId, limit, ct);
    public Task PurgeStorageErrorsAsync(string projectId, long beforeUnixMs, CancellationToken ct) => _inner.PurgeStorageErrorsAsync(projectId, beforeUnixMs, ct);
    public Task InsertStorageRequestLogAsync(string projectId, long createdAtUnixMs, string method, string path, int statusCode, int durationMs, string? apiKeyIdentifier, CancellationToken ct) => _inner.InsertStorageRequestLogAsync(projectId, createdAtUnixMs, method, path, statusCode, durationMs, apiKeyIdentifier, ct);
    public Task<IReadOnlyList<JsonElement>> ListStorageRequestLogAsync(string projectId, int limit, CancellationToken ct) => _inner.ListStorageRequestLogAsync(projectId, limit, ct);
    public Task PurgeStorageRequestLogAsync(string projectId, long beforeUnixMs, CancellationToken ct) => _inner.PurgeStorageRequestLogAsync(projectId, beforeUnixMs, ct);
    public Task IncrementProjectUsageAsync(string projectId, string month, string day, string? endpointSlug, UsageDelta delta, CancellationToken ct) => _inner.IncrementProjectUsageAsync(projectId, month, day, endpointSlug, delta, ct);
    public Task<JsonElement?> ReadProjectUsageMonthlyAsync(string projectId, string month, CancellationToken ct) => _inner.ReadProjectUsageMonthlyAsync(projectId, month, ct);
    public Task<long> ReadProjectStorageBytesAsync(string projectId, CancellationToken ct) => _inner.ReadProjectStorageBytesAsync(projectId, ct);
    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageDailyAsync(string projectId, string month, CancellationToken ct) => _inner.ReadProjectUsageDailyAsync(projectId, month, ct);
    public Task<IReadOnlyList<JsonElement>> ReadProjectUsageEndpointsAsync(string projectId, string month, int limit, CancellationToken ct) => _inner.ReadProjectUsageEndpointsAsync(projectId, month, limit, ct);
    public Task PutWorkspaceObjectAsync(string path, string content, CancellationToken ct) => _inner.PutWorkspaceObjectAsync(path, content, ct);
    public Task<string?> ReadWorkspaceObjectAsync(string path, CancellationToken ct) => _inner.ReadWorkspaceObjectAsync(path, ct);
    public Task DeleteWorkspaceObjectAsync(string path, CancellationToken ct) => _inner.DeleteWorkspaceObjectAsync(path, ct);
    public Task<IReadOnlyList<WorkspaceObjectEntry>> ListWorkspaceObjectsAsync(string directoryPath, CancellationToken ct) => _inner.ListWorkspaceObjectsAsync(directoryPath, ct);
}
