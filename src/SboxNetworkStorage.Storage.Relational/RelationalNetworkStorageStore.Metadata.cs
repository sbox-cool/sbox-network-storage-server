using System.Text.Json;
using Microsoft.Extensions.Logging;
using C = SboxNetworkStorage.Storage.Relational.StoreColumns;
using V = SboxNetworkStorage.Storage.Relational.StoreValidation;

namespace SboxNetworkStorage.Storage.Relational;

// Project metadata: projects, collections, endpoints, workflows, game values,
// rate limit rules, queries (+ run tracking), api keys, memberships, pages.
public abstract partial class RelationalNetworkStorageStore
{
    // ── projects ────────────────────────────────────────────────────

    public Task UpsertProjectAsync(string projectId, JsonElement payload, long version, CancellationToken ct)
    {
        V.Id(projectId);
        var payloadText = Serialize(payload, "projects");
        return ExecuteAsync(_sql.UpsertProject, ct,
            Text("project_id", projectId),
            Text("workspace_id", V.ReadOptionalString(payload, "workspaceId", "workspace_id")),
            Text("storage_owner_user_id", V.ReadOptionalString(payload, "storageOwnerUserId", "storage_owner_user_id")),
            Text("payload_json", payloadText), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public async Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        var (found, payload) = await QueryUtf8Async(_sql.ReadProject, ct, Text("project_id", projectId));
        return found ? RowJson.ParseJsonColumn(payload) : null;
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        var connection = lease.Connection;
        var id = Text("project_id", projectId);
        await ExecuteAsync(connection, _sql.DeleteProject, ct, id);

        // Best-effort cleanup of usage counters and query run telemetry, as in
        // production: failures are logged and swallowed because the rows are
        // inert without the project row.
        foreach (var sql in new[]
                 {
                     _sql.DeleteProjectUsageDaily, _sql.DeleteProjectUsageEndpoints, _sql.DeleteProjectUsageMonthly,
                     _sql.DeleteProjectQueryLogs, _sql.DeleteProjectQueryLastRuns,
                 })
        {
            try
            {
                await ExecuteAsync(connection, sql, ct, id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Best-effort telemetry cleanup failed for deleted project {ProjectId}", projectId);
            }
        }
    }

    // ── collections ─────────────────────────────────────────────────

    public Task UpsertCollectionAsync(string projectId, string collectionId, string name, string visibility, JsonElement definitionJson, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId); V.NonReservedCollectionId(collectionId);
        var definition = Serialize(definitionJson, "collections");
        return ExecuteAsync(_sql.UpsertCollection, ct,
            Text("project_id", projectId), Text("collection_id", collectionId), Text("name", name), Text("visibility", visibility),
            Text("definition_json", definition), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadCollectionAsync(string projectId, string collectionId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId);
        return QuerySingleAsync(_sql.ReadCollection, C.Collection, ct, Text("project_id", projectId), Text("collection_id", collectionId));
    }

    public Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListCollections, C.Collection, ct, Text("project_id", projectId));
    }

    public Task DeleteCollectionAsync(string projectId, string collectionId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(collectionId);
        return ExecuteAsync(_sql.DeleteCollection, ct, Text("project_id", projectId), Text("collection_id", collectionId));
    }

    // ── endpoints ───────────────────────────────────────────────────

    public Task UpsertEndpointAsync(string projectId, string endpointId, string slug, string method, bool enabled, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(endpointId);
        var definition = Serialize(definitionJson, "endpoints");
        return ExecuteAsync(_sql.UpsertEndpoint, ct,
            Text("project_id", projectId), Text("endpoint_id", endpointId), Text("slug", slug), Text("method", method),
            Bool("enabled", enabled), Text("definition_json", definition), Text("version_hash", versionHash),
            Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadEndpointAsync(string projectId, string endpointId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(endpointId);
        return QuerySingleAsync(_sql.ReadEndpoint, C.Endpoint, ct, Text("project_id", projectId), Text("endpoint_id", endpointId));
    }

    public Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListEndpoints, C.Endpoint, ct, Text("project_id", projectId));
    }

    public Task DeleteEndpointAsync(string projectId, string endpointId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(endpointId);
        return ExecuteAsync(_sql.DeleteEndpoint, ct, Text("project_id", projectId), Text("endpoint_id", endpointId));
    }

    // ── workflows ───────────────────────────────────────────────────

    public Task UpsertWorkflowAsync(string projectId, string workflowId, string name, JsonElement definitionJson, string? versionHash, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(workflowId);
        var definition = Serialize(definitionJson, "workflows");
        return ExecuteAsync(_sql.UpsertWorkflow, ct,
            Text("project_id", projectId), Text("workflow_id", workflowId), Text("name", name),
            Text("definition_json", definition), Text("version_hash", versionHash),
            Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadWorkflowAsync(string projectId, string workflowId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(workflowId);
        return QuerySingleAsync(_sql.ReadWorkflow, C.Workflow, ct, Text("project_id", projectId), Text("workflow_id", workflowId));
    }

    public Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListWorkflows, C.Workflow, ct, Text("project_id", projectId));
    }

    public Task DeleteWorkflowAsync(string projectId, string workflowId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(workflowId);
        return ExecuteAsync(_sql.DeleteWorkflow, ct, Text("project_id", projectId), Text("workflow_id", workflowId));
    }

    // ── game_values ─────────────────────────────────────────────────

    public Task UpsertGameValuesAsync(string projectId, JsonElement payloadJson, string? versionHash, long version, CancellationToken ct)
    {
        V.Id(projectId);
        var payload = Serialize(payloadJson, "game_values");
        return ExecuteAsync(_sql.UpsertGameValues, ct,
            Text("project_id", projectId), Text("payload_json", payload), Text("version_hash", versionHash),
            Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadGameValues, C.GameValues, ct, Text("project_id", projectId));
    }

    public Task DeleteGameValuesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteGameValues, ct, Text("project_id", projectId));
    }

    // ── rate_limit_rules ────────────────────────────────────────────

    public Task UpsertRateLimitRulesAsync(string projectId, JsonElement rulesJson, long version, CancellationToken ct)
    {
        V.Id(projectId);
        var rules = Serialize(rulesJson, "rate_limit_rules");
        return ExecuteAsync(_sql.UpsertRateLimitRules, ct,
            Text("project_id", projectId), Text("rules_json", rules), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadRateLimitRulesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadRateLimitRules, C.RateLimitRules, ct, Text("project_id", projectId));
    }

    public Task DeleteRateLimitRulesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteRateLimitRules, ct, Text("project_id", projectId));
    }

    // ── queries ─────────────────────────────────────────────────────

    public Task UpsertQueryAsync(string projectId, string queryId, string name, bool requiresSecretKey, JsonElement definitionJson, long version, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        var definition = Serialize(definitionJson, "queries");
        return ExecuteAsync(_sql.UpsertQuery, ct,
            Text("project_id", projectId), Text("query_id", queryId), Text("name", name), Bool("requires_secret_key", requiresSecretKey),
            Text("definition_json", definition), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadQueryAsync(string projectId, string queryId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        return QuerySingleAsync(_sql.ReadQuery, C.Query, ct, Text("project_id", projectId), Text("query_id", queryId));
    }

    public Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListQueries, C.Query, ct, Text("project_id", projectId));
    }

    public Task DeleteQueryAsync(string projectId, string queryId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        return ExecuteAsync(_sql.DeleteQuery, ct, Text("project_id", projectId), Text("query_id", queryId));
    }

    // ── query run tracking ──────────────────────────────────────────

    public async Task RecordQueryRunAsync(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        var now = UnixMs();
        await using var lease = await LeaseAsync(ct);
        var connection = lease.Connection;
        await ExecuteAsync(connection, _sql.UpsertQueryLastRun, ct,
            Text("project_id", projectId), Text("query_id", queryId), Text("run_at", runAtIso), Int64("duration_ms", durationMs),
            Int32("keys_scanned", keysScanned), Int32("records_returned", recordsReturned), Bool("from_cache", fromCache),
            Int64("updated_at_unix_ms", now));
        // Log rows are keyed by (project, query, created_at): a second run in the
        // same millisecond overwrites the first, exactly like the CQL primary key.
        await ExecuteAsync(connection, _sql.UpsertQueryLog, ct,
            Text("project_id", projectId), Text("query_id", queryId), Int64("created_at_unix_ms", now), Text("log_type", "run"),
            Int64("duration_ms", durationMs), Int32("keys_scanned", keysScanned), Int32("records_returned", recordsReturned),
            Bool("from_cache", fromCache), Text("changes_json", null));
        // Emulate the 90-day table TTL by dropping expired rows of this partition.
        await ExecuteAsync(connection, _sql.ExpireQueryLogs, ct,
            Text("project_id", projectId), Text("query_id", queryId), Int64("expired_at", QueryLogExpiryCutoff(now)));
    }

    public Task<JsonElement?> ReadQueryLastRunAsync(string projectId, string queryId, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        return QuerySingleAsync(_sql.ReadQueryLastRun, C.QueryLastRun, ct, Text("project_id", projectId), Text("query_id", queryId));
    }

    public Task<IReadOnlyList<JsonElement>> ListQueryLastRunsAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListQueryLastRuns, C.QueryLastRunWithId, ct, Text("project_id", projectId));
    }

    public Task<IReadOnlyList<JsonElement>> ListQueryLogsAsync(string projectId, string queryId, int limit, CancellationToken ct)
    {
        V.Id(projectId); V.Id(queryId);
        return QueryListAsync(_sql.ListQueryLogs, C.QueryLog, ct,
            Text("project_id", projectId), Text("query_id", queryId),
            Int64("expired_at", QueryLogExpiryCutoff(UnixMs())), Int32("limit", Math.Max(1, Math.Min(limit, 200))));
    }

    private static long QueryLogExpiryCutoff(long nowUnixMs) => nowUnixMs - (long)QueryRunLogRetention.TotalMilliseconds;

    // ── api_keys ────────────────────────────────────────────────────

    public Task UpsertApiKeyAsync(string projectId, string apiKey, string userId, string keyType, string keyHash, string keyIdentifier, string label, bool enabled, JsonElement permissionsJson, long version, CancellationToken ct)
    {
        V.Id(projectId);
        var permissions = Serialize(permissionsJson, "api_keys");
        return ExecuteAsync(_sql.UpsertApiKey, ct,
            Text("project_id", projectId), Text("api_key", apiKey), Text("user_id", userId), Text("key_type", keyType),
            Text("key_hash", keyHash), Text("key_identifier", keyIdentifier), Text("label", label), Bool("enabled", enabled),
            Text("permissions_json", permissions), Int64("version", version), Int64("updated_at_unix_ms", UnixMs()));
    }

    public Task<JsonElement?> ReadApiKeyAsync(string projectId, string apiKey, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadApiKey, C.ApiKey, ct, Text("project_id", projectId), Text("api_key", apiKey));
    }

    public Task<IReadOnlyList<JsonElement>> ListApiKeysAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListApiKeys, C.ApiKey, ct, Text("project_id", projectId));
    }

    public Task DeleteApiKeyAsync(string projectId, string apiKey, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteApiKey, ct, Text("project_id", projectId), Text("api_key", apiKey));
    }

    // ── project_members ─────────────────────────────────────────────

    public Task UpsertProjectMembershipAsync(string userId, string projectId, string role, long createdAtUnixMs, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.UpsertProjectMembership, ct,
            Text("user_id", userId), Text("project_id", projectId), Text("role", role), Int64("created_at_unix_ms", createdAtUnixMs));
    }

    public Task<IReadOnlyList<JsonElement>> ListProjectsForUserAsync(string userId, CancellationToken ct)
        => QueryListAsync(_sql.ListProjectsForUser, C.ProjectMembership, ct, Text("user_id", userId));

    public Task DeleteProjectMembershipAsync(string userId, string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeleteProjectMembership, ct, Text("user_id", userId), Text("project_id", projectId));
    }

    // ── pages ───────────────────────────────────────────────────────

    public Task UpsertPageAsync(string projectId, string pageSlug, string title, string contentJson, long createdAtUnixMs, long updatedAtUnixMs, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.UpsertPage, ct,
            Text("project_id", projectId), Text("page_slug", pageSlug), Text("title", title), Text("content_json", contentJson),
            Int64("created_at_unix_ms", createdAtUnixMs), Int64("updated_at_unix_ms", updatedAtUnixMs));
    }

    public Task<JsonElement?> ReadPageAsync(string projectId, string pageSlug, CancellationToken ct)
    {
        V.Id(projectId);
        return QuerySingleAsync(_sql.ReadPage, C.Page, ct, Text("project_id", projectId), Text("page_slug", pageSlug));
    }

    public Task<IReadOnlyList<JsonElement>> ListPagesAsync(string projectId, CancellationToken ct)
    {
        V.Id(projectId);
        return QueryListAsync(_sql.ListPages, C.Page, ct, Text("project_id", projectId));
    }

    public Task DeletePageAsync(string projectId, string pageSlug, CancellationToken ct)
    {
        V.Id(projectId);
        return ExecuteAsync(_sql.DeletePage, ct, Text("project_id", projectId), Text("page_slug", pageSlug));
    }
}
