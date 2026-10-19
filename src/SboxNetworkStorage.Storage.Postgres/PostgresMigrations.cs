using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Postgres;

/// <summary>
/// Forward-only PostgreSQL migrations. Never edit a released migration; append
/// a new version instead. Tables mirror the production CQL schema. Key text
/// columns use <c>COLLATE "C"</c> so ordering is bytewise, exactly like
/// The store clustering order on <c>text</c>.
/// </summary>
public static class PostgresMigrations
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
        new(8, "Structured game logs: per-project entries with GIN-indexed tokens and jsonb attrs (off by default)", SchemaChange.Additive, V8),
    ];

    // Additive: one new table, its indexes and GIN indexes. Older binaries never touch them.
    // search_tokens is the same whitespace-split token list the C# tokenizer writes to search_text (see GameLogTokenizer.SearchText).
    private static string V8(string p) => $"""
        CREATE TABLE {p}game_log_entries (
            project_id {Key}, entry_id {Key}, ts_unix_ms bigint NOT NULL, stream text NOT NULL, level text NOT NULL, event text NOT NULL,
            actor_steam_id text, target_steam_id text, revision_id bigint, client_type text, library_version text,
            message text NOT NULL, attrs_json jsonb NOT NULL, trace_id text, search_text text NOT NULL,
            search_tokens text[] GENERATED ALWAYS AS (string_to_array(search_text, ' ')) STORED,
            PRIMARY KEY (project_id, entry_id));
        CREATE INDEX ix_game_log_project_ts ON {p}game_log_entries (project_id, ts_unix_ms DESC, entry_id DESC);
        CREATE INDEX ix_game_log_project_actor_ts ON {p}game_log_entries (project_id, actor_steam_id, ts_unix_ms DESC);
        CREATE INDEX ix_game_log_project_target_ts ON {p}game_log_entries (project_id, target_steam_id, ts_unix_ms DESC);
        CREATE INDEX ix_game_log_project_event_ts ON {p}game_log_entries (project_id, event, ts_unix_ms DESC);
        CREATE INDEX ix_game_log_search_tokens ON {p}game_log_entries USING gin (search_tokens);
        CREATE INDEX ix_game_log_attrs ON {p}game_log_entries USING gin (attrs_json jsonb_path_ops);
        """;

    // Additive: three new tables plus two indexes. No existing table is touched, so the
    // previous binary's statements keep working and older binaries ignore the new tables.
    private static string V6(string p) => $"""
        CREATE TABLE {p}integrity_flags (project_id {Key}, flag_id {Key}, created_at_unix_ms bigint NOT NULL, steam_id text NOT NULL, detector text NOT NULL, severity text NOT NULL, state text NOT NULL, note text, evidence_json text, PRIMARY KEY (project_id, flag_id));
        CREATE INDEX ix_integrity_flags_project_state ON {p}integrity_flags (project_id, state, created_at_unix_ms DESC);
        CREATE TABLE {p}player_blocks (project_id {Key}, steam_id {Key}, scope text NOT NULL, reason text, created_at_unix_ms bigint NOT NULL, expires_at_unix_ms bigint, created_by text, PRIMARY KEY (project_id, steam_id));
        CREATE TABLE {p}player_action_log (project_id {Key}, steam_id {Key}, created_at_unix_ms bigint NOT NULL, entry_id {Key}, endpoint_slug text, revision_id bigint, outcome text, changed_fields_json text, body_json text, PRIMARY KEY (project_id, steam_id, created_at_unix_ms, entry_id));
        CREATE INDEX ix_player_action_log_project_time ON {p}player_action_log (project_id, created_at_unix_ms DESC);
        """;

    // Additive: one new table. Older binaries never read or write it, so they keep working on the result.
    private static string V7(string p) => $"""
        CREATE TABLE {p}client_outcomes_hourly (
            project_id {Key}, hour bigint NOT NULL, server_version text NOT NULL, group_key {Key},
            library_version text NOT NULL, library_revision bigint, client_type text NOT NULL,
            requests bigint NOT NULL, rejected bigint NOT NULL, server_errors bigint NOT NULL,
            PRIMARY KEY (project_id, hour, server_version, group_key));
        CREATE INDEX ix_client_outcomes_hour ON {p}client_outcomes_hourly (hour);
        """;

    // Additive: one new table plus five nullable error columns. The previous binary's named-column
    // INSERTs and SELECTs keep working; existing rows keep NULL, which the Errors tab shows as unknown.
    private static string V5(string p) => $"""
        ALTER TABLE {p}storage_errors ADD COLUMN library_version text;
        ALTER TABLE {p}storage_errors ADD COLUMN client_type text;
        ALTER TABLE {p}storage_errors ADD COLUMN engine_version text;
        ALTER TABLE {p}storage_errors ADD COLUMN library_revision bigint;
        ALTER TABLE {p}storage_errors ADD COLUMN game_revision bigint;
        CREATE TABLE {p}client_versions_daily (
            project_id {Key}, day integer NOT NULL, group_key {Key},
            library_version text NOT NULL, library_revision bigint, client_type text NOT NULL, game_revision bigint,
            requests bigint NOT NULL, last_seen_unix_ms bigint NOT NULL, player_sketch text NOT NULL,
            PRIMARY KEY (project_id, day, group_key));
        """;

    // Additive: three nullable columns. Existing rows keep NULL, which the Logs tab shows as unknown.
    private static string V4(string p) => $"""
        ALTER TABLE {p}storage_request_log ADD COLUMN library_version text;
        ALTER TABLE {p}storage_request_log ADD COLUMN client_type text;
        ALTER TABLE {p}storage_request_log ADD COLUMN engine_version text;
        """;

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

