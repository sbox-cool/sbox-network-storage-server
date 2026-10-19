using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Sqlite;

/// <summary>
/// Forward-only SQLite migrations. Never edit a released migration; append a
/// new version instead. Tables mirror the production CQL schema (same table and
/// column names, same primary keys and clustering directions).
/// </summary>
public static class SqliteMigrations
{
    // Every migration carries a SchemaChange tag (docs/database.md "Rollback-safe migrations"). Additive means the previous
    // binary's statements keep working on the result; Breaking means they may not. v1 is the baseline and always breaking.
    // v3 rebuilds the two log tables with a surrogate key but keeps every column and name, and the previous binary only
    // uses named-column INSERTs and (project_id, created_at) filters on them, so it is additive.
    public static readonly IReadOnlyList<SchemaMigration> All =
    [
        new(1, "Network Storage tables, usage counters, workspace objects", SchemaChange.Breaking, V1),
        new(2, "Existing projects keep the built-in player projections", SchemaChange.Additive, V2),
        new(3, "Endpoint slug and API key identifier indexes; request log and error surrogate ids", SchemaChange.Additive, V3),
        new(4, "Request log records the caller's library version, client type and engine build", SchemaChange.Additive, V4),
        new(5, "Daily client-version aggregates; errors record the caller's library identity", SchemaChange.Additive, V5),
        new(6, "Integrity flags, player blocks and player action log for the integrity and insights modules", SchemaChange.Additive, V6),
        new(7, "Hourly per-library request outcomes by server version for post-update regression checks", SchemaChange.Additive, V7),
        new(8, "Durable Discord webhook outbox and per-target delivery state for endpoint webhook steps", SchemaChange.Additive, V8),
        new(9, "Media objects and single-use upload grant records for the media store", SchemaChange.Additive, V9),
    ];

    // Additive: two new tables with indexes. Nothing existing is touched; older binaries never read them.
    private static string V9(string p) => $"""
        CREATE TABLE {p}media_objects (
            project_id TEXT NOT NULL, media_id TEXT NOT NULL, purpose TEXT NOT NULL, sha256 TEXT NOT NULL,
            content_type TEXT NOT NULL, size_bytes INTEGER NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
            owner_steam_id TEXT NOT NULL, visibility TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL,
            expires_at_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (project_id, media_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}media_grants (
            project_id TEXT NOT NULL, grant_id TEXT NOT NULL, purpose TEXT NOT NULL, owner_steam_id TEXT NOT NULL,
            issued_at_unix_ms INTEGER NOT NULL, expires_at_unix_ms INTEGER NOT NULL, used_at_unix_ms INTEGER,
            PRIMARY KEY (project_id, grant_id)) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_media_grants_rate ON {p}media_grants (project_id, owner_steam_id, purpose, issued_at_unix_ms);
        CREATE INDEX ix_media_grants_issued ON {p}media_grants (issued_at_unix_ms);
        """;

    // Additive: three new tables plus two indexes. No existing table is touched, so the
    // previous binary's statements keep working and older binaries ignore the new tables.
    private static string V6(string p) => $"""
        CREATE TABLE {p}integrity_flags (project_id TEXT NOT NULL, flag_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, steam_id TEXT NOT NULL, detector TEXT NOT NULL, severity TEXT NOT NULL, state TEXT NOT NULL, note TEXT, evidence_json TEXT, PRIMARY KEY (project_id, flag_id)) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_integrity_flags_project_state ON {p}integrity_flags (project_id, state, created_at_unix_ms DESC);
        CREATE TABLE {p}player_blocks (project_id TEXT NOT NULL, steam_id TEXT NOT NULL, scope TEXT NOT NULL, reason TEXT, created_at_unix_ms INTEGER NOT NULL, expires_at_unix_ms INTEGER, created_by TEXT, PRIMARY KEY (project_id, steam_id)) STRICT, WITHOUT ROWID;
        CREATE TABLE {p}player_action_log (project_id TEXT NOT NULL, steam_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, entry_id TEXT NOT NULL, endpoint_slug TEXT, revision_id INTEGER, outcome TEXT, changed_fields_json TEXT, body_json TEXT, PRIMARY KEY (project_id, steam_id, created_at_unix_ms DESC, entry_id)) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_player_action_log_project_time ON {p}player_action_log (project_id, created_at_unix_ms DESC);
        """;

    // Additive: two new tables. Only binaries with the outbox write them; the previous binary never reads them.
    private static string V8(string p) => $"""
        CREATE TABLE {p}webhook_outbox (
            project_id TEXT NOT NULL, outbox_id TEXT NOT NULL, target_url TEXT NOT NULL, payload_json TEXT NOT NULL,
            status TEXT NOT NULL, attempts INTEGER NOT NULL, next_attempt_at_unix_ms INTEGER NOT NULL,
            lease_until_unix_ms INTEGER, last_status INTEGER, last_error TEXT,
            created_at_unix_ms INTEGER NOT NULL, updated_at_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (project_id, outbox_id)) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_webhook_outbox_due ON {p}webhook_outbox (status, next_attempt_at_unix_ms);
        CREATE INDEX ix_webhook_outbox_target ON {p}webhook_outbox (project_id, target_url, status);
        CREATE TABLE {p}webhook_targets (
            project_id TEXT NOT NULL, target_url TEXT NOT NULL, consecutive_failures INTEGER NOT NULL,
            failing_since_unix_ms INTEGER, last_status INTEGER, last_error TEXT,
            disabled_at_unix_ms INTEGER, disabled_reason TEXT, updated_at_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (project_id, target_url)) STRICT, WITHOUT ROWID;
        """;

    // Additive: one new table. Older binaries never read or write it, so they keep working on the result.
    private static string V7(string p) => $"""
        CREATE TABLE {p}client_outcomes_hourly (
            project_id TEXT NOT NULL, hour INTEGER NOT NULL, server_version TEXT NOT NULL, group_key TEXT NOT NULL,
            library_version TEXT NOT NULL, library_revision INTEGER, client_type TEXT NOT NULL,
            requests INTEGER NOT NULL, rejected INTEGER NOT NULL, server_errors INTEGER NOT NULL,
            PRIMARY KEY (project_id, hour, server_version, group_key)) STRICT, WITHOUT ROWID;
        CREATE INDEX ix_client_outcomes_hour ON {p}client_outcomes_hourly (hour);
        """;

    // Additive: one new table plus five nullable error columns. The previous binary's named-column
    // INSERTs and SELECTs keep working; existing rows keep NULL, which the Errors tab shows as unknown.
    private static string V5(string p) => $"""
        ALTER TABLE {p}storage_errors ADD COLUMN library_version TEXT;
        ALTER TABLE {p}storage_errors ADD COLUMN client_type TEXT;
        ALTER TABLE {p}storage_errors ADD COLUMN engine_version TEXT;
        ALTER TABLE {p}storage_errors ADD COLUMN library_revision INTEGER;
        ALTER TABLE {p}storage_errors ADD COLUMN game_revision INTEGER;
        CREATE TABLE {p}client_versions_daily (
            project_id TEXT NOT NULL, day INTEGER NOT NULL, group_key TEXT NOT NULL,
            library_version TEXT NOT NULL, library_revision INTEGER, client_type TEXT NOT NULL, game_revision INTEGER,
            requests INTEGER NOT NULL, last_seen_unix_ms INTEGER NOT NULL, player_sketch TEXT NOT NULL,
            PRIMARY KEY (project_id, day, group_key)) STRICT, WITHOUT ROWID;
        """;

    // Additive: three nullable columns. Existing rows keep NULL, which the Logs tab shows as unknown; no table rebuild.
    private static string V4(string p) => $"""
        ALTER TABLE {p}storage_request_log ADD COLUMN library_version TEXT;
        ALTER TABLE {p}storage_request_log ADD COLUMN client_type TEXT;
        ALTER TABLE {p}storage_request_log ADD COLUMN engine_version TEXT;
        """;

    // The log tables are rebuilt as rowid tables: event_id is the surrogate key, so events that share a
    // project and millisecond are all kept. Existing rows are copied in their previous (project, time) order.
    private static string V3(string p) => $"""
        CREATE INDEX ix_endpoints_project_slug ON {p}endpoints (project_id, slug);
        CREATE INDEX ix_api_keys_project_key_identifier ON {p}api_keys (project_id, key_identifier);
        CREATE TABLE {p}storage_errors_v3 (event_id INTEGER PRIMARY KEY, project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, error_id TEXT, message TEXT, stack_trace TEXT, source TEXT, request_path TEXT, severity TEXT) STRICT;
        INSERT INTO {p}storage_errors_v3 (project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity) SELECT project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity FROM {p}storage_errors ORDER BY project_id, created_at_unix_ms;
        DROP TABLE {p}storage_errors;
        ALTER TABLE {p}storage_errors_v3 RENAME TO storage_errors;
        CREATE INDEX ix_storage_errors_project_time ON {p}storage_errors (project_id, created_at_unix_ms DESC);
        CREATE TABLE {p}storage_request_log_v3 (event_id INTEGER PRIMARY KEY, project_id TEXT NOT NULL, created_at_unix_ms INTEGER NOT NULL, method TEXT, path TEXT, status_code INTEGER, duration_ms INTEGER, api_key_identifier TEXT) STRICT;
        INSERT INTO {p}storage_request_log_v3 (project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier) SELECT project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier FROM {p}storage_request_log ORDER BY project_id, created_at_unix_ms;
        DROP TABLE {p}storage_request_log;
        ALTER TABLE {p}storage_request_log_v3 RENAME TO storage_request_log;
        CREATE INDEX ix_storage_request_log_project_time ON {p}storage_request_log (project_id, created_at_unix_ms DESC);
        """;
    // Projects that exist before the opt-in flag keep their behavior; new projects start with it off.
    private static string V2(string p) => $"""
        UPDATE {p}projects SET payload_json = json_set(payload_json, '$.legacyPlayerProjections', json('true')) WHERE json_valid(payload_json) AND json_type(payload_json) = 'object';
        """;

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

