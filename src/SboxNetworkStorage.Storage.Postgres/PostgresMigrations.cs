using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Postgres;

/// <summary>
/// Forward-only PostgreSQL migrations. Never edit a released migration; append
/// a new version instead. Tables mirror the production CQL schema. Key text
/// columns use <c>COLLATE "C"</c> so ordering is bytewise, exactly like
/// The store clustering order on <c>text</c>.
/// </summary>
internal static class PostgresMigrations
{
    public static readonly IReadOnlyList<SchemaMigration> All =
    [
        new(1, "Network Storage tables, usage counters, workspace objects", V1),
        new(2, "Existing projects keep the built-in player projections", V2),
        new(3, "Endpoint slug and API key identifier indexes; request log and error surrogate ids", V3),
    ];

    // Identity values are generated for existing rows too. Project/time indexes retain chronological reads.
    private static string V3(string p) => $"""
        CREATE INDEX ix_endpoints_project_slug ON {p}endpoints (project_id, slug);
        CREATE INDEX ix_api_keys_project_key_identifier ON {p}api_keys (project_id, key_identifier);
        ALTER TABLE {p}storage_errors DROP CONSTRAINT storage_errors_pkey;
        ALTER TABLE {p}storage_errors ADD COLUMN event_id bigint GENERATED ALWAYS AS IDENTITY;
        ALTER TABLE {p}storage_errors ADD PRIMARY KEY (event_id);
        CREATE INDEX ix_storage_errors_project_time ON {p}storage_errors (project_id, created_at_unix_ms DESC);
        ALTER TABLE {p}storage_request_log DROP CONSTRAINT storage_request_log_pkey;
        ALTER TABLE {p}storage_request_log ADD COLUMN event_id bigint GENERATED ALWAYS AS IDENTITY;
        ALTER TABLE {p}storage_request_log ADD PRIMARY KEY (event_id);
        CREATE INDEX ix_storage_request_log_project_time ON {p}storage_request_log (project_id, created_at_unix_ms DESC);
        """;
    // Projects that exist before the opt-in flag keep their behavior; new projects start with it off.
    private static string V2(string p) => $"""
        UPDATE {p}projects SET payload_json = (payload_json::jsonb || jsonb_build_object('legacyPlayerProjections', true))::text WHERE substr(ltrim(payload_json), 1, 1) = chr(123);
        """;

    private const string Key = "text COLLATE \"C\" NOT NULL";

    private static string V1(string p) => $"""
        CREATE TABLE {p}projects (project_id {Key} PRIMARY KEY, workspace_id text, storage_owner_user_id text, payload_json text, version bigint, updated_at_unix_ms bigint);
        CREATE TABLE {p}collections (project_id {Key}, collection_id {Key}, name text, visibility text, definition_json text, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, collection_id));
        CREATE TABLE {p}endpoints (project_id {Key}, endpoint_id {Key}, slug text, method text, enabled boolean, definition_json text, version_hash text, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, endpoint_id));
        CREATE TABLE {p}workflows (project_id {Key}, workflow_id {Key}, name text, definition_json text, version_hash text, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, workflow_id));
        CREATE TABLE {p}game_values (project_id {Key} PRIMARY KEY, payload_json text, version_hash text, version bigint, updated_at_unix_ms bigint);
        CREATE TABLE {p}rate_limit_rules (project_id {Key} PRIMARY KEY, rules_json text, version bigint, updated_at_unix_ms bigint);
        CREATE TABLE {p}queries (project_id {Key}, query_id {Key}, name text, requires_secret_key boolean, definition_json text, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, query_id));
        CREATE TABLE {p}query_last_run (project_id {Key}, query_id {Key}, run_at text, duration_ms bigint, keys_scanned integer, records_returned integer, from_cache boolean, updated_at_unix_ms bigint, PRIMARY KEY (project_id, query_id));
        CREATE TABLE {p}query_run_logs (project_id {Key}, query_id {Key}, created_at_unix_ms bigint NOT NULL, log_type text, duration_ms bigint, keys_scanned integer, records_returned integer, from_cache boolean, changes_json text, PRIMARY KEY (project_id, query_id, created_at_unix_ms));
        CREATE TABLE {p}records (project_id {Key}, collection_id {Key}, record_key {Key}, payload_json text, deleted boolean, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, collection_id, record_key));
        CREATE TABLE {p}record_idempotency (project_id {Key}, collection_id {Key}, record_key {Key}, idempotency_key {Key}, result_record_version bigint, result_hash text, payload_json text, created_at_unix_ms bigint, PRIMARY KEY (project_id, collection_id, record_key, idempotency_key));
        CREATE TABLE {p}global_records (project_id {Key}, collection_id {Key}, record_id {Key}, payload_json text, version bigint, created_at_unix_ms bigint, PRIMARY KEY (project_id, collection_id, record_id));
        CREATE TABLE {p}ledger_entries (project_id {Key}, collection_id {Key}, record_key {Key}, sequence bigint NOT NULL, entry_json text, created_at_unix_ms bigint, PRIMARY KEY (project_id, collection_id, record_key, sequence));
        CREATE TABLE {p}checkpoint_cursor (project_id {Key} PRIMARY KEY, latest_sequence bigint, manifest_path text, updated_at_unix_ms bigint, version bigint);
        CREATE TABLE {p}api_keys (project_id {Key}, api_key {Key}, user_id text, key_type text, key_hash text, key_identifier text, label text, enabled boolean, permissions_json text, version bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, api_key));
        CREATE TABLE {p}project_audit_logs (project_id {Key}, created_at_unix_ms bigint NOT NULL, log_id {Key}, user_id text, action text, actor_json text, target_json text, summary_json text, diff_json text, PRIMARY KEY (project_id, created_at_unix_ms, log_id));
        CREATE TABLE {p}player_analytics (project_id {Key}, collection_id {Key}, record_key {Key}, created_at_unix_ms bigint NOT NULL, event_id {Key}, event_type text, payload_json text, PRIMARY KEY (project_id, collection_id, record_key, created_at_unix_ms, event_id));
        CREATE TABLE {p}player_analytics_events (project_id {Key}, steam_id {Key}, created_at_unix_ms bigint NOT NULL, event_id {Key}, event_type text, category text, label text, endpoint_slug text, collection_id text, payload_json text, PRIMARY KEY (project_id, steam_id, created_at_unix_ms, event_id));
        CREATE TABLE {p}player_profiles (project_id {Key}, steam_id {Key}, player_name text, is_online boolean, online_since_unix_ms bigint, last_seen_unix_ms bigint, last_heartbeat_unix_ms bigint, current_session_id text, current_session_last_seconds bigint, total_seconds bigint, session_count bigint, last_event_type text, last_endpoint_slug text, managed_counters_json text, updated_at_unix_ms bigint, PRIMARY KEY (project_id, steam_id));
        CREATE TABLE {p}player_sessions (project_id {Key}, steam_id {Key}, session_id {Key}, started_at_unix_ms bigint, last_heartbeat_at_unix_ms bigint, ended_at_unix_ms bigint, last_metrics_json text, summary_json text, PRIMARY KEY (project_id, steam_id, session_id));
        CREATE TABLE {p}project_analytics_issues (project_id {Key}, bucket_date {Key}, created_at_unix_ms bigint NOT NULL, event_id {Key}, steam_id text, category text, event_type text, label text, payload_json text, PRIMARY KEY (project_id, bucket_date, created_at_unix_ms, event_id));
        CREATE TABLE {p}project_members (user_id {Key}, project_id {Key}, role text, created_at_unix_ms bigint, PRIMARY KEY (user_id, project_id));
        CREATE TABLE {p}pages (project_id {Key}, page_slug {Key}, title text, content_json text, created_at_unix_ms bigint, updated_at_unix_ms bigint, PRIMARY KEY (project_id, page_slug));
        CREATE TABLE {p}storage_errors (project_id {Key}, created_at_unix_ms bigint NOT NULL, error_id text, message text, stack_trace text, source text, request_path text, severity text, PRIMARY KEY (project_id, created_at_unix_ms));
        CREATE TABLE {p}storage_request_log (project_id {Key}, created_at_unix_ms bigint NOT NULL, method text, path text, status_code integer, duration_ms integer, api_key_identifier text, PRIMARY KEY (project_id, created_at_unix_ms));
        CREATE TABLE {p}project_usage_monthly (project_id {Key}, month {Key}, requests bigint NOT NULL DEFAULT 0, reads bigint NOT NULL DEFAULT 0, writes bigint NOT NULL DEFAULT 0, endpoint_calls bigint NOT NULL DEFAULT 0, bytes_in bigint NOT NULL DEFAULT 0, bytes_out bigint NOT NULL DEFAULT 0, errors bigint NOT NULL DEFAULT 0, duration_ms_sum bigint NOT NULL DEFAULT 0, duration_samples bigint NOT NULL DEFAULT 0, compute_units bigint NOT NULL DEFAULT 0, storage_delta_bytes bigint NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month));
        CREATE TABLE {p}project_usage_daily (project_id {Key}, month {Key}, day {Key}, requests bigint NOT NULL DEFAULT 0, bytes_in bigint NOT NULL DEFAULT 0, bytes_out bigint NOT NULL DEFAULT 0, errors bigint NOT NULL DEFAULT 0, compute_units bigint NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month, day));
        CREATE TABLE {p}project_usage_endpoints (project_id {Key}, month {Key}, endpoint_slug {Key}, calls bigint NOT NULL DEFAULT 0, errors bigint NOT NULL DEFAULT 0, duration_ms_sum bigint NOT NULL DEFAULT 0, compute_units bigint NOT NULL DEFAULT 0, PRIMARY KEY (project_id, month, endpoint_slug));
        CREATE TABLE {p}workspace_objects (path {Key} PRIMARY KEY, content text NOT NULL, size_bytes bigint NOT NULL, updated_at_unix_ms bigint NOT NULL);
        """;
}

