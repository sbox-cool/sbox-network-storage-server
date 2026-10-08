using C = SboxNetworkStorage.Storage.Relational.StoreColumns;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>
/// Every DML statement of the store, built once per store instance for a table
/// prefix (empty for SQLite, <c>"schema".</c> for PostgreSQL). The SQL is the
/// common subset of SQLite (3.35+) and PostgreSQL: <c>@name</c> parameters,
/// <c>INSERT … ON CONFLICT … DO UPDATE</c>, <c>LIMIT @limit</c>. Parameters are
/// named after the column they bind.
/// </summary>
internal sealed class StoreSql
{
    private readonly string _p;

    public StoreSql(string tablePrefix)
    {
        _p = tablePrefix;

        UpsertProject = Upsert("projects", ["project_id"], ["workspace_id", "storage_owner_user_id", "payload_json", "version", "updated_at_unix_ms"]);
        ReadProject = $"SELECT payload_json FROM {T("projects")} WHERE project_id = @project_id";
        DeleteProject = $"DELETE FROM {T("projects")} WHERE project_id = @project_id";
        DeleteProjectUsageMonthly = $"DELETE FROM {T("project_usage_monthly")} WHERE project_id = @project_id";
        DeleteProjectUsageDaily = $"DELETE FROM {T("project_usage_daily")} WHERE project_id = @project_id";
        DeleteProjectUsageEndpoints = $"DELETE FROM {T("project_usage_endpoints")} WHERE project_id = @project_id";
        DeleteProjectQueryLogs = $"DELETE FROM {T("query_run_logs")} WHERE project_id = @project_id";
        DeleteProjectQueryLastRuns = $"DELETE FROM {T("query_last_run")} WHERE project_id = @project_id";

        UpsertCollection = Upsert("collections", ["project_id", "collection_id"], ["name", "visibility", "definition_json", "version", "updated_at_unix_ms"]);
        ReadCollection = Select("collections", C.Collection, "project_id = @project_id AND collection_id = @collection_id");
        ListCollections = Select("collections", C.Collection, "project_id = @project_id", "collection_id");
        DeleteCollection = $"DELETE FROM {T("collections")} WHERE project_id = @project_id AND collection_id = @collection_id";

        UpsertEndpoint = Upsert("endpoints", ["project_id", "endpoint_id"], ["slug", "method", "enabled", "definition_json", "version_hash", "version", "updated_at_unix_ms"]);
        ReadEndpoint = Select("endpoints", C.Endpoint, "project_id = @project_id AND endpoint_id = @endpoint_id");
        ListEndpoints = Select("endpoints", C.Endpoint, "project_id = @project_id", "endpoint_id");
        DeleteEndpoint = $"DELETE FROM {T("endpoints")} WHERE project_id = @project_id AND endpoint_id = @endpoint_id";

        UpsertWorkflow = Upsert("workflows", ["project_id", "workflow_id"], ["name", "definition_json", "version_hash", "version", "updated_at_unix_ms"]);
        ReadWorkflow = Select("workflows", C.Workflow, "project_id = @project_id AND workflow_id = @workflow_id");
        ListWorkflows = Select("workflows", C.Workflow, "project_id = @project_id", "workflow_id");
        DeleteWorkflow = $"DELETE FROM {T("workflows")} WHERE project_id = @project_id AND workflow_id = @workflow_id";

        UpsertGameValues = Upsert("game_values", ["project_id"], ["payload_json", "version_hash", "version", "updated_at_unix_ms"]);
        ReadGameValues = Select("game_values", C.GameValues, "project_id = @project_id");
        DeleteGameValues = $"DELETE FROM {T("game_values")} WHERE project_id = @project_id";

        UpsertRateLimitRules = Upsert("rate_limit_rules", ["project_id"], ["rules_json", "version", "updated_at_unix_ms"]);
        ReadRateLimitRules = Select("rate_limit_rules", C.RateLimitRules, "project_id = @project_id");
        DeleteRateLimitRules = $"DELETE FROM {T("rate_limit_rules")} WHERE project_id = @project_id";

        UpsertQuery = Upsert("queries", ["project_id", "query_id"], ["name", "requires_secret_key", "definition_json", "version", "updated_at_unix_ms"]);
        ReadQuery = Select("queries", C.Query, "project_id = @project_id AND query_id = @query_id");
        ListQueries = Select("queries", C.Query, "project_id = @project_id", "query_id");
        DeleteQuery = $"DELETE FROM {T("queries")} WHERE project_id = @project_id AND query_id = @query_id";

        UpsertQueryLastRun = Upsert("query_last_run", ["project_id", "query_id"], ["run_at", "duration_ms", "keys_scanned", "records_returned", "from_cache", "updated_at_unix_ms"]);
        ReadQueryLastRun = Select("query_last_run", C.QueryLastRun, "project_id = @project_id AND query_id = @query_id");
        ListQueryLastRuns = Select("query_last_run", C.QueryLastRunWithId, "project_id = @project_id", "query_id");
        UpsertQueryLog = Upsert("query_run_logs", ["project_id", "query_id", "created_at_unix_ms"], ["log_type", "duration_ms", "keys_scanned", "records_returned", "from_cache", "changes_json"]);
        ExpireQueryLogs = $"DELETE FROM {T("query_run_logs")} WHERE project_id = @project_id AND query_id = @query_id AND created_at_unix_ms <= @expired_at";
        ListQueryLogs = Select("query_run_logs", C.QueryLog, "project_id = @project_id AND query_id = @query_id AND created_at_unix_ms > @expired_at", "created_at_unix_ms DESC", limit: true);

        UpsertRecord = Upsert("records", ["project_id", "collection_id", "record_key"], ["payload_json", "deleted", "version", "updated_at_unix_ms"]);
        ReadRecord = Select("records", C.Record, "project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key");
        ListRecords = Select("records", C.Record, "project_id = @project_id AND collection_id = @collection_id", "record_key");
        DeleteRecord = $"DELETE FROM {T("records")} WHERE project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key";

        UpsertRecordIdempotency = Upsert("record_idempotency", ["project_id", "collection_id", "record_key", "idempotency_key"], ["result_record_version", "result_hash", "payload_json", "created_at_unix_ms"]);
        ReadRecordIdempotency = Select("record_idempotency", C.RecordIdempotency, "project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key AND idempotency_key = @idempotency_key");
        DeleteRecordIdempotency = $"DELETE FROM {T("record_idempotency")} WHERE project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key AND idempotency_key = @idempotency_key";

        UpsertGlobalRecord = Upsert("global_records", ["project_id", "collection_id", "record_id"], ["payload_json", "version", "created_at_unix_ms"]);
        ReadGlobalRecord = Select("global_records", C.GlobalRecord, "project_id = @project_id AND collection_id = @collection_id AND record_id = @record_id");
        ListGlobalRecords = Select("global_records", C.GlobalRecord, "project_id = @project_id AND collection_id = @collection_id", "record_id");
        DeleteGlobalRecord = $"DELETE FROM {T("global_records")} WHERE project_id = @project_id AND collection_id = @collection_id AND record_id = @record_id";

        UpsertLedgerEntry = Upsert("ledger_entries", ["project_id", "collection_id", "record_key", "sequence"], ["entry_json", "created_at_unix_ms"]);
        ListLedgerEntries = Select("ledger_entries", C.LedgerEntry, "project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key", "sequence");
        DeleteLedgerEntries = $"DELETE FROM {T("ledger_entries")} WHERE project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key";

        UpsertCheckpointCursor = Upsert("checkpoint_cursor", ["project_id"], ["latest_sequence", "manifest_path", "updated_at_unix_ms", "version"]);
        ReadCheckpointCursor = Select("checkpoint_cursor", C.CheckpointCursor, "project_id = @project_id");
        DeleteCheckpointCursor = $"DELETE FROM {T("checkpoint_cursor")} WHERE project_id = @project_id";

        UpsertApiKey = Upsert("api_keys", ["project_id", "api_key"], ["user_id", "key_type", "key_hash", "key_identifier", "label", "enabled", "permissions_json", "version", "updated_at_unix_ms"]);
        ReadApiKey = Select("api_keys", C.ApiKey, "project_id = @project_id AND api_key = @api_key");
        ListApiKeys = Select("api_keys", C.ApiKey, "project_id = @project_id", "api_key");
        DeleteApiKey = $"DELETE FROM {T("api_keys")} WHERE project_id = @project_id AND api_key = @api_key";

        UpsertAuditLog = Upsert("project_audit_logs", ["project_id", "created_at_unix_ms", "log_id"], ["user_id", "action", "actor_json", "target_json", "summary_json", "diff_json"]);
        ListAuditLogs = Select("project_audit_logs", C.AuditLog, "project_id = @project_id", "created_at_unix_ms DESC, log_id", limit: true);
        DeleteAuditLogs = $"DELETE FROM {T("project_audit_logs")} WHERE project_id = @project_id";

        UpsertPlayerAnalytics = Upsert("player_analytics", ["project_id", "collection_id", "record_key", "created_at_unix_ms", "event_id"], ["event_type", "payload_json"]);
        ListPlayerAnalytics = Select("player_analytics", C.PlayerAnalytics, "project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key", "created_at_unix_ms DESC, event_id", limit: true);
        DeletePlayerAnalytics = $"DELETE FROM {T("player_analytics")} WHERE project_id = @project_id AND collection_id = @collection_id AND record_key = @record_key";

        UpsertPlayerEvent = Upsert("player_analytics_events", ["project_id", "steam_id", "created_at_unix_ms", "event_id"], ["event_type", "category", "label", "endpoint_slug", "collection_id", "payload_json"]);
        ListPlayerEvents = Select("player_analytics_events", C.PlayerEvent, "project_id = @project_id AND steam_id = @steam_id AND created_at_unix_ms <= @to_unix_ms AND created_at_unix_ms >= @from_unix_ms", "created_at_unix_ms DESC, event_id", limit: true);
        CountPlayerEvents = $"SELECT COUNT(*) FROM {T("player_analytics_events")} WHERE project_id = @project_id AND steam_id = @steam_id";

        UpsertPlayerProfile = Upsert("player_profiles", ["project_id", "steam_id"], ["player_name", "is_online", "online_since_unix_ms", "last_seen_unix_ms", "last_heartbeat_unix_ms", "current_session_id", "current_session_last_seconds", "total_seconds", "session_count", "last_event_type", "last_endpoint_slug", "managed_counters_json", "updated_at_unix_ms"]);
        ReadPlayerProfile = Select("player_profiles", C.PlayerProfile, "project_id = @project_id AND steam_id = @steam_id");
        ListPlayerProfiles = Select("player_profiles", C.PlayerProfile, "project_id = @project_id", "steam_id");

        UpsertPlayerSession = Upsert("player_sessions", ["project_id", "steam_id", "session_id"], ["started_at_unix_ms", "last_heartbeat_at_unix_ms", "ended_at_unix_ms", "last_metrics_json", "summary_json"]);
        ReadPlayerSession = Select("player_sessions", C.PlayerSession, "project_id = @project_id AND steam_id = @steam_id AND session_id = @session_id");

        UpsertProjectIssue = Upsert("project_analytics_issues", ["project_id", "bucket_date", "created_at_unix_ms", "event_id"], ["steam_id", "category", "event_type", "label", "payload_json"]);
        ListProjectIssues = Select("project_analytics_issues", C.ProjectIssue, "project_id = @project_id AND bucket_date = @bucket_date", "created_at_unix_ms DESC, event_id", limit: true);

        UpsertProjectMembership = Upsert("project_members", ["user_id", "project_id"], ["role", "created_at_unix_ms"]);
        ListProjectsForUser = Select("project_members", C.ProjectMembership, "user_id = @user_id", "project_id");
        DeleteProjectMembership = $"DELETE FROM {T("project_members")} WHERE user_id = @user_id AND project_id = @project_id";

        UpsertPage = Upsert("pages", ["project_id", "page_slug"], ["title", "content_json", "created_at_unix_ms", "updated_at_unix_ms"]);
        ReadPage = Select("pages", C.Page, "project_id = @project_id AND page_slug = @page_slug");
        ListPages = Select("pages", C.Page, "project_id = @project_id", "page_slug");
        DeletePage = $"DELETE FROM {T("pages")} WHERE project_id = @project_id AND page_slug = @page_slug";

        UpsertStorageError = Upsert("storage_errors", ["project_id", "created_at_unix_ms"], ["error_id", "message", "stack_trace", "source", "request_path", "severity"]);
        ListStorageErrors = Select("storage_errors", C.StorageError, "project_id = @project_id", "created_at_unix_ms DESC", limit: true);
        PurgeStorageErrors = $"DELETE FROM {T("storage_errors")} WHERE project_id = @project_id AND created_at_unix_ms < @before_unix_ms";

        UpsertStorageRequestLog = Upsert("storage_request_log", ["project_id", "created_at_unix_ms"], ["method", "path", "status_code", "duration_ms", "api_key_identifier"]);
        ListStorageRequestLog = Select("storage_request_log", C.StorageRequestLog, "project_id = @project_id", "created_at_unix_ms DESC", limit: true);
        PurgeStorageRequestLog = $"DELETE FROM {T("storage_request_log")} WHERE project_id = @project_id AND created_at_unix_ms < @before_unix_ms";

        IncrementUsageMonthly = Increment("project_usage_monthly", ["project_id", "month"], ["requests", "reads", "writes", "endpoint_calls", "bytes_in", "bytes_out", "errors", "duration_ms_sum", "duration_samples", "compute_units", "storage_delta_bytes"]);
        IncrementUsageDaily = Increment("project_usage_daily", ["project_id", "month", "day"], ["requests", "bytes_in", "bytes_out", "errors", "compute_units"]);
        IncrementUsageEndpoint = Increment("project_usage_endpoints", ["project_id", "month", "endpoint_slug"], ["calls", "errors", "duration_ms_sum", "compute_units"]);
        ReadUsageMonthly = Select("project_usage_monthly", C.UsageMonthly, "project_id = @project_id AND month = @month");
        ReadUsageStorage = $"SELECT storage_delta_bytes FROM {T("project_usage_monthly")} WHERE project_id = @project_id";
        ReadUsageDaily = Select("project_usage_daily", C.UsageDaily, "project_id = @project_id AND month = @month", "day");
        ReadUsageEndpoints = Select("project_usage_endpoints", C.UsageEndpoint, "project_id = @project_id AND month = @month", "endpoint_slug", limit: true);

        UpsertWorkspaceObject = Upsert("workspace_objects", ["path"], ["content", "size_bytes", "updated_at_unix_ms"]);
        ReadWorkspaceObject = $"SELECT content FROM {T("workspace_objects")} WHERE path = @path";
        DeleteWorkspaceObject = $"DELETE FROM {T("workspace_objects")} WHERE path = @path";
        ListAllWorkspaceObjects = $"SELECT path, size_bytes, updated_at_unix_ms FROM {T("workspace_objects")} ORDER BY path";
        ListWorkspaceObjectsInRange = $"SELECT path, size_bytes, updated_at_unix_ms FROM {T("workspace_objects")} WHERE path >= @range_start AND path < @range_end ORDER BY path";

        CreateSchemaVersionTable = $"CREATE TABLE IF NOT EXISTS {T("schema_version")} (version INTEGER NOT NULL PRIMARY KEY, applied_at_unix_ms BIGINT NOT NULL, description TEXT NOT NULL)";
        ReadSchemaVersion = $"SELECT COALESCE(MAX(version), 0) FROM {T("schema_version")}";
        InsertSchemaVersion = $"INSERT INTO {T("schema_version")} (version, applied_at_unix_ms, description) VALUES (@version, @applied_at_unix_ms, @description)";
    }

    public string UpsertProject { get; }
    public string ReadProject { get; }
    public string DeleteProject { get; }
    public string DeleteProjectUsageMonthly { get; }
    public string DeleteProjectUsageDaily { get; }
    public string DeleteProjectUsageEndpoints { get; }
    public string DeleteProjectQueryLogs { get; }
    public string DeleteProjectQueryLastRuns { get; }
    public string UpsertCollection { get; }
    public string ReadCollection { get; }
    public string ListCollections { get; }
    public string DeleteCollection { get; }
    public string UpsertEndpoint { get; }
    public string ReadEndpoint { get; }
    public string ListEndpoints { get; }
    public string DeleteEndpoint { get; }
    public string UpsertWorkflow { get; }
    public string ReadWorkflow { get; }
    public string ListWorkflows { get; }
    public string DeleteWorkflow { get; }
    public string UpsertGameValues { get; }
    public string ReadGameValues { get; }
    public string DeleteGameValues { get; }
    public string UpsertRateLimitRules { get; }
    public string ReadRateLimitRules { get; }
    public string DeleteRateLimitRules { get; }
    public string UpsertQuery { get; }
    public string ReadQuery { get; }
    public string ListQueries { get; }
    public string DeleteQuery { get; }
    public string UpsertQueryLastRun { get; }
    public string ReadQueryLastRun { get; }
    public string ListQueryLastRuns { get; }
    public string UpsertQueryLog { get; }
    public string ExpireQueryLogs { get; }
    public string ListQueryLogs { get; }
    public string UpsertRecord { get; }
    public string ReadRecord { get; }
    public string ListRecords { get; }
    public string DeleteRecord { get; }
    public string UpsertRecordIdempotency { get; }
    public string ReadRecordIdempotency { get; }
    public string DeleteRecordIdempotency { get; }
    public string UpsertGlobalRecord { get; }
    public string ReadGlobalRecord { get; }
    public string ListGlobalRecords { get; }
    public string DeleteGlobalRecord { get; }
    public string UpsertLedgerEntry { get; }
    public string ListLedgerEntries { get; }
    public string DeleteLedgerEntries { get; }
    public string UpsertCheckpointCursor { get; }
    public string ReadCheckpointCursor { get; }
    public string DeleteCheckpointCursor { get; }
    public string UpsertApiKey { get; }
    public string ReadApiKey { get; }
    public string ListApiKeys { get; }
    public string DeleteApiKey { get; }
    public string UpsertAuditLog { get; }
    public string ListAuditLogs { get; }
    public string DeleteAuditLogs { get; }
    public string UpsertPlayerAnalytics { get; }
    public string ListPlayerAnalytics { get; }
    public string DeletePlayerAnalytics { get; }
    public string UpsertPlayerEvent { get; }
    public string ListPlayerEvents { get; }
    public string CountPlayerEvents { get; }
    public string UpsertPlayerProfile { get; }
    public string ReadPlayerProfile { get; }
    public string ListPlayerProfiles { get; }
    public string UpsertPlayerSession { get; }
    public string ReadPlayerSession { get; }
    public string UpsertProjectIssue { get; }
    public string ListProjectIssues { get; }
    public string UpsertProjectMembership { get; }
    public string ListProjectsForUser { get; }
    public string DeleteProjectMembership { get; }
    public string UpsertPage { get; }
    public string ReadPage { get; }
    public string ListPages { get; }
    public string DeletePage { get; }
    public string UpsertStorageError { get; }
    public string ListStorageErrors { get; }
    public string PurgeStorageErrors { get; }
    public string UpsertStorageRequestLog { get; }
    public string ListStorageRequestLog { get; }
    public string PurgeStorageRequestLog { get; }
    public string IncrementUsageMonthly { get; }
    public string IncrementUsageDaily { get; }
    public string IncrementUsageEndpoint { get; }
    public string ReadUsageMonthly { get; }
    public string ReadUsageStorage { get; }
    public string ReadUsageDaily { get; }
    public string ReadUsageEndpoints { get; }
    public string UpsertWorkspaceObject { get; }
    public string ReadWorkspaceObject { get; }
    public string DeleteWorkspaceObject { get; }
    public string ListAllWorkspaceObjects { get; }
    public string ListWorkspaceObjectsInRange { get; }
    public string CreateSchemaVersionTable { get; }
    public string ReadSchemaVersion { get; }
    public string InsertSchemaVersion { get; }

    private string T(string table) => _p + table;

    /// <summary>Full-row overwrite, the relational equivalent of a CQL <c>INSERT</c>.</summary>
    private string Upsert(string table, string[] keys, string[] values)
    {
        var columns = string.Join(", ", keys.Concat(values));
        var parameters = string.Join(", ", keys.Concat(values).Select(c => "@" + c));
        var updates = string.Join(", ", values.Select(c => $"{c} = excluded.{c}"));
        return $"INSERT INTO {T(table)} ({columns}) VALUES ({parameters}) ON CONFLICT ({string.Join(", ", keys)}) DO UPDATE SET {updates}";
    }

    /// <summary>Atomic additive counter update, the equivalent of CQL <c>UPDATE … SET c = c + ?</c>.</summary>
    private string Increment(string table, string[] keys, string[] counters)
    {
        var columns = string.Join(", ", keys.Concat(counters));
        var parameters = string.Join(", ", keys.Concat(counters).Select(c => "@" + c));
        var updates = string.Join(", ", counters.Select(c => $"{c} = t.{c} + excluded.{c}"));
        return $"INSERT INTO {T(table)} AS t ({columns}) VALUES ({parameters}) ON CONFLICT ({string.Join(", ", keys)}) DO UPDATE SET {updates}";
    }

    private string Select(string table, Column[] columns, string where, string? orderBy = null, bool limit = false)
    {
        var selectList = string.Join(", ", columns.Select(c => c.Expression ?? c.Name));
        var sql = $"SELECT {selectList} FROM {T(table)} WHERE {where}";
        if (orderBy is not null) sql += " ORDER BY " + orderBy;
        if (limit) sql += " LIMIT @limit";
        return sql;
    }
}
