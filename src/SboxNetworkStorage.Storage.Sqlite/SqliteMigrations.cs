using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Sqlite;

/// <summary>
/// Forward-only SQLite migrations. Never edit a released migration; append a
/// new version instead. Tables mirror the production CQL schema (same table and
/// column names, same primary keys and clustering directions).
/// </summary>
internal static class SqliteMigrations
{
    public static readonly IReadOnlyList<SchemaMigration> All =
    [
        new(1, "Network Storage tables, usage counters, workspace objects", V1),
    ];

    private static string V1(string p) => $"""
        CREATE TABLE {p}projects (project_id TEXT NOT NULL PRIMARY KEY, workspace_id TEXT, storage_owner_user_id TEXT, payload_json TEXT, version INTEGER, updated_at_unix_ms INTEGER) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}collections (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, name TEXT, visibility TEXT, definition_json TEXT, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, collection_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}endpoints (project_id TEXT NOT NULL, endpoint_id TEXT NOT NULL, slug TEXT, method TEXT, enabled INTEGER, definition_json TEXT, version_hash TEXT, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, endpoint_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}workflows (project_id TEXT NOT NULL, workflow_id TEXT NOT NULL, name TEXT, definition_json TEXT, version_hash TEXT, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, workflow_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}game_values (project_id TEXT NOT NULL PRIMARY KEY, payload_json TEXT, version_hash TEXT, version INTEGER, updated_at_unix_ms INTEGER) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}rate_limit_rules (project_id TEXT NOT NULL PRIMARY KEY, rules_json TEXT, version INTEGER, updated_at_unix_ms INTEGER) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}queries (project_id TEXT NOT NULL, query_id TEXT NOT NULL, name TEXT, requires_secret_key INTEGER, definition_json TEXT, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, query_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}query_last_run (project_id TEXT NOT NULL, query_id TEXT NOT NULL, run_at TEXT, duration_ms INTEGER, keys_scanned INTEGER, records_returned INTEGER, from_cache INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, query_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}query_run_logs (project_id TEXT NOT NULL, query_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, log_type TEXT, duration_ms INTEGER, keys_scanned INTEGER, records_returned INTEGER, from_cache INTEGER, changes_json TEXT, PRIMARY KEY (project_id, query_id, created_at_unix_ms DESC)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}records (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, record_key TEXT NOT NULL, payload_json TEXT, deleted INTEGER, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, collection_id, record_key)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}record_idempotency (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, record_key TEXT NOT NULL, idempotency_key TEXT NOT NULL, result_record_version INTEGER, result_hash TEXT, payload_json TEXT, created_at_unix_ms INTEGER, PRIMARY KEY (project_id, collection_id, record_key, idempotency_key)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}global_records (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, record_id TEXT NOT NULL, payload_json TEXT, version INTEGER, created_at_unix_ms INTEGER, PRIMARY KEY (project_id, collection_id, record_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}ledger_entries (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, record_key TEXT NOT NULL, sequence INTEGER NOT NULL, entry_json TEXT, created_at_unix_ms INTEGER, PRIMARY KEY (project_id, collection_id, record_key, sequence)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}checkpoint_cursor (project_id TEXT NOT NULL PRIMARY KEY, latest_sequence INTEGER, manifest_path TEXT, updated_at_unix_ms INTEGER, version INTEGER) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}api_keys (project_id TEXT NOT NULL, api_key TEXT NOT NULL, user_id TEXT, key_type TEXT, key_hash TEXT, key_identifier TEXT, label TEXT, enabled INTEGER, permissions_json TEXT, version INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, api_key)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_audit_logs (project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, log_id TEXT NOT NULL, user_id TEXT, action TEXT, actor_json TEXT, target_json TEXT, summary_json TEXT, diff_json TEXT, PRIMARY KEY (project_id, created_at_unix_ms DESC, log_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}player_analytics (project_id TEXT NOT NULL, collection_id TEXT NOT NULL, record_key TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, event_id TEXT NOT NULL, event_type TEXT, payload_json TEXT, PRIMARY KEY (project_id, collection_id, record_key, created_at_unix_ms DESC, event_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}player_analytics_events (project_id TEXT NOT NULL, steam_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, event_id TEXT NOT NULL, event_type TEXT, category TEXT, label TEXT, endpoint_slug TEXT, collection_id TEXT, payload_json TEXT, PRIMARY KEY (project_id, steam_id, created_at_unix_ms DESC, event_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}player_profiles (project_id TEXT NOT NULL, steam_id TEXT NOT NULL, player_name TEXT, is_online INTEGER, online_since_unix_ms INTEGER, last_seen_unix_ms INTEGER, last_heartbeat_unix_ms INTEGER, current_session_id TEXT, current_session_last_seconds INTEGER, total_seconds INTEGER, session_count INTEGER, last_event_type TEXT, last_endpoint_slug TEXT, managed_counters_json TEXT, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, steam_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}player_sessions (project_id TEXT NOT NULL, steam_id TEXT NOT NULL, session_id TEXT NOT NULL, started_at_unix_ms INTEGER, last_heartbeat_at_unix_ms INTEGER, ended_at_unix_ms INTEGER, last_metrics_json TEXT, summary_json TEXT, PRIMARY KEY (project_id, steam_id, session_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_analytics_issues (project_id TEXT NOT NULL, bucket_date TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, event_id TEXT NOT NULL, steam_id TEXT, category TEXT, event_type TEXT, label TEXT, payload_json TEXT, PRIMARY KEY (project_id, bucket_date, created_at_unix_ms DESC, event_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_members (user_id TEXT NOT NULL, project_id TEXT NOT NULL, role TEXT, created_at_unix_ms INTEGER, PRIMARY KEY (user_id, project_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}pages (project_id TEXT NOT NULL, page_slug TEXT NOT NULL, title TEXT, content_json TEXT, created_at_unix_ms INTEGER, updated_at_unix_ms INTEGER, PRIMARY KEY (project_id, page_slug)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}storage_errors (project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, error_id TEXT, message TEXT, stack_trace TEXT, source TEXT, request_path TEXT, severity TEXT, PRIMARY KEY (project_id, created_at_unix_ms DESC)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}storage_request_log (project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, method TEXT, path TEXT, status_code INTEGER, duration_ms INTEGER, api_key_identifier TEXT, PRIMARY KEY (project_id, created_at_unix_ms DESC)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_usage_monthly (project_id TEXT NOT NULL, month TEXT NOT NULL, requests INTEGER NOT NULL DEFAULT 0, reads INTEGER NOT NULL DEFAULT 0, writes INTEGER NOT NULL DEFAULT 0, endpoint_calls INTEGER NOT NULL DEFAULT 0, bytes_in INTEGER NOT NULL DEFAULT 0, bytes_out INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0, duration_ms_sum INTEGER NOT NULL DEFAULT 0, duration_samples INTEGER NOT NULL DEFAULT 0, compute_units INTEGER NOT NULL DEFAULT 0, storage_delta_bytes INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_usage_daily (project_id TEXT NOT NULL, month TEXT NOT NULL, day TEXT NOT NULL, requests INTEGER NOT NULL DEFAULT 0, bytes_in INTEGER NOT NULL DEFAULT 0, bytes_out INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0, compute_units INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month, day)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}project_usage_endpoints (project_id TEXT NOT NULL, month TEXT NOT NULL, endpoint_slug TEXT NOT NULL, calls INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0, duration_ms_sum INTEGER NOT NULL DEFAULT 0, compute_units INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month, endpoint_slug)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}workspace_objects (path TEXT NOT NULL PRIMARY KEY, content TEXT NOT NULL, size_bytes INTEGER NOT NULL, updated_at_unix_ms INTEGER NOT NULL) STRICT, WITHOUT ROWID;
        """;
}
