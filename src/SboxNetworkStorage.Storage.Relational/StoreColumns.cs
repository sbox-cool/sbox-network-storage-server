using static SboxNetworkStorage.Storage.Relational.ColumnKind;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>
/// Row shapes returned by each read, mirroring the production ScyllaDB row
/// builders column for column (names, order, value kinds, null defaults).
/// </summary>
internal static class StoreColumns
{
    public static readonly Column[] Collection =
    [
        new("collection_id", Text), new("name", Text), new("visibility", Text),
        new("definition_json", Json), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] Endpoint =
    [
        new("endpoint_id", Text), new("slug", Text), new("method", Text), new("enabled", Bool),
        new("definition_json", Json), new("version_hash", Text), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] Workflow =
    [
        new("workflow_id", Text), new("name", Text), new("definition_json", Json),
        new("version_hash", Text), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] GameValues =
    [
        new("payload_json", Json), new("version_hash", Text), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] RateLimitRules =
    [
        new("rules_json", Json), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] Query =
    [
        new("query_id", Text), new("name", Text), new("requires_secret_key", Bool),
        new("definition_json", Json), new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] QueryLastRun =
    [
        new("run_at", Text), new("duration_ms", LongOrNull), new("keys_scanned", IntOrNull),
        new("records_returned", IntOrNull), new("from_cache", BoolOrNull), new("updated_at_unix_ms", LongOrNull),
    ];

    public static readonly Column[] QueryLastRunWithId = [new("query_id", Text), .. QueryLastRun];

    public static readonly Column[] QueryLog =
    [
        new("created_at_unix_ms", LongOrNull), new("log_type", Text, "COALESCE(log_type, 'run')"),
        new("duration_ms", LongOrNull), new("keys_scanned", IntOrNull), new("records_returned", IntOrNull),
        new("from_cache", BoolOrNull), new("changes_json", Json),
    ];

    public static readonly Column[] Record =
    [
        new("record_key", Text), new("payload_json", Json), new("deleted", Bool),
        new("version", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] RecordIdempotency =
    [
        new("idempotency_key", Text), new("result_record_version", Long), new("result_hash", Text),
        new("payload_json", Json), new("created_at_unix_ms", Long),
    ];

    public static readonly Column[] GlobalRecord =
    [
        new("record_id", Text), new("payload_json", Json), new("version", Long), new("created_at_unix_ms", Long),
    ];

    public static readonly Column[] LedgerEntry =
    [
        new("sequence", Long), new("entry_json", Json), new("created_at_unix_ms", Long),
    ];

    public static readonly Column[] CheckpointCursor =
    [
        new("latest_sequence", Long), new("manifest_path", Text), new("updated_at_unix_ms", Long), new("version", Long),
    ];

    public static readonly Column[] ApiKey =
    [
        new("api_key", Text), new("user_id", Text), new("key_type", Text), new("key_hash", Text),
        new("key_identifier", Text), new("label", Text), new("enabled", Bool), new("permissions_json", Json),
        new("version", Long), new("updated_at_unix_ms", Long),
    ];

    /// <summary>Audit JSON columns are returned as raw strings (production reads them with <c>GetValue&lt;string&gt;</c>).</summary>
    public static readonly Column[] AuditLog =
    [
        new("created_at_unix_ms", Long), new("log_id", Text), new("user_id", Text), new("action", Text),
        new("actor_json", Text), new("target_json", Text), new("summary_json", Text), new("diff_json", Text),
    ];

    public static readonly Column[] PlayerAnalytics =
    [
        new("record_key", Text), new("created_at_unix_ms", Long), new("event_id", Text),
        new("event_type", Text), new("payload_json", Json),
    ];

    public static readonly Column[] PlayerEvent =
    [
        new("steam_id", Text), new("created_at_unix_ms", Long), new("event_id", Text), new("event_type", Text),
        new("category", Text), new("label", Text), new("endpoint_slug", Text), new("collection_id", Text),
        new("payload_json", Json),
    ];

    public static readonly Column[] PlayerProfile =
    [
        new("steam_id", Text), new("player_name", Text), new("is_online", Bool),
        new("online_since_unix_ms", LongOrNull), new("last_seen_unix_ms", Long),
        new("last_heartbeat_unix_ms", LongOrNull), new("current_session_id", Text),
        new("current_session_last_seconds", LongOrNull), new("total_seconds", Long), new("session_count", Long),
        new("last_event_type", Text), new("last_endpoint_slug", Text), new("managed_counters_json", Json),
        new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] PlayerSession =
    [
        new("session_id", Text), new("started_at_unix_ms", LongOrNull), new("last_heartbeat_at_unix_ms", LongOrNull),
        new("ended_at_unix_ms", LongOrNull), new("last_metrics_json", Json), new("summary_json", Json),
    ];

    public static readonly Column[] ProjectIssue =
    [
        new("created_at_unix_ms", Long), new("event_id", Text), new("steam_id", Text), new("category", Text),
        new("event_type", Text), new("label", Text), new("payload_json", Json),
    ];

    public static readonly Column[] ProjectMembership =
    [
        new("project_id", Text), new("role", Text), new("created_at_unix_ms", Long),
    ];

    /// <summary>Page <c>content_json</c> is returned as a raw string, as in production.</summary>
    public static readonly Column[] Page =
    [
        new("page_slug", Text), new("title", Text), new("content_json", Text),
        new("created_at_unix_ms", Long), new("updated_at_unix_ms", Long),
    ];

    public static readonly Column[] StorageError =
    [
        new("created_at_unix_ms", Long), new("error_id", Text), new("message", Text), new("stack_trace", Text),
        new("source", Text), new("request_path", Text), new("severity", Text),
    ];

    public static readonly Column[] StorageRequestLog =
    [
        new("created_at_unix_ms", Long), new("method", Text), new("path", Text), new("status_code", Int),
        new("duration_ms", Int), new("api_key_identifier", Text),
    ];

    public static readonly Column[] UsageMonthly =
    [
        new("requests", Long), new("reads", Long), new("writes", Long), new("endpoint_calls", Long),
        new("bytes_in", Long), new("bytes_out", Long), new("errors", Long), new("duration_ms_sum", Long),
        new("duration_samples", Long), new("compute_units", Long), new("storage_delta_bytes", Long),
    ];

    public static readonly Column[] UsageDaily =
    [
        new("day", Text), new("requests", Long), new("bytes_in", Long), new("bytes_out", Long),
        new("errors", Long), new("compute_units", Long),
    ];

    public static readonly Column[] UsageEndpoint =
    [
        new("endpoint_slug", Text), new("calls", Long), new("errors", Long),
        new("duration_ms_sum", Long), new("compute_units", Long),
    ];
}
