namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract partial class StoreConformanceTests
{
    // ── audit logs ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task Audit_logs_are_newest_first_with_raw_json_strings_and_limit()
    {
        var s = await StoreAsync();
        await s.InsertAuditLogAsync("p1", 100, "log-b", "u1", "update", "{\"id\":\"u1\"}", "{}", "{\"n\":1}", "[]", Ct);
        await s.InsertAuditLogAsync("p1", 300, "log-c", "u2", "delete", "{}", "{}", "{}", "{}", Ct);
        await s.InsertAuditLogAsync("p1", 100, "log-a", "u3", "create", "{}", "{}", "{}", "{}", Ct);
        await s.InsertAuditLogAsync("p2", 999, "log-z", "u9", "create", "{}", "{}", "{}", "{}", Ct);

        var logs = await s.ListAuditLogsAsync("p1", 10, Ct);
        Assert.Equal(new[] { "log-c", "log-a", "log-b" }, logs.Select(r => Json.Str(r, "log_id")));
        Json.Equal("""{"created_at_unix_ms":100,"log_id":"log-b","user_id":"u1","action":"update","actor_json":"{\"id\":\"u1\"}","target_json":"{}","summary_json":"{\"n\":1}","diff_json":"[]"}""", logs[2]);
        Assert.Equal(new[] { "log-c", "log-a" }, (await s.ListAuditLogsAsync("p1", 2, Ct)).Select(r => Json.Str(r, "log_id")));

        await s.InsertAuditLogAsync("p1", 300, "log-c", "u2", "restore", "{}", "{}", "{}", "{}", Ct);
        Assert.Equal("restore", Json.Str((await s.ListAuditLogsAsync("p1", 1, Ct))[0], "action"));

        await s.DeleteAuditLogsAsync("p1", Ct);
        Assert.Empty(await s.ListAuditLogsAsync("p1", 10, Ct));
        Assert.Single(await s.ListAuditLogsAsync("p2", 10, Ct));
    }

    // ── player analytics (legacy) ───────────────────────────────────

    [SkippableFact]
    public async Task Legacy_player_analytics_are_per_record_key_newest_first()
    {
        var s = await StoreAsync();
        await s.InsertPlayerAnalyticsEventAsync("p1", "players", "steam:1", 10, "e2", "login", Json.Parse("""{"a":1}"""), Ct);
        await s.InsertPlayerAnalyticsEventAsync("p1", "players", "steam:1", 20, "e3", "buy", Json.Parse("{}"), Ct);
        await s.InsertPlayerAnalyticsEventAsync("p1", "players", "steam:1", 10, "e1", "logout", Json.Parse("[]"), Ct);
        await s.InsertPlayerAnalyticsEventAsync("p1", "players", "steam:1:alt", 50, "e9", "x", Json.Parse("{}"), Ct);

        var events = await s.ListPlayerAnalyticsEventsAsync("p1", "players", "steam:1", 10, Ct);
        Assert.Equal(new[] { "e3", "e1", "e2" }, events.Select(r => Json.Str(r, "event_id")));
        Json.Equal("""{"record_key":"steam:1","created_at_unix_ms":10,"event_id":"e2","event_type":"login","payload_json":{"a":1}}""", events[2]);
        Assert.Equal(2, (await s.ListPlayerAnalyticsEventsAsync("p1", "players", "steam:1", 2, Ct)).Count);

        await s.DeletePlayerAnalyticsEventsAsync("p1", "players", "steam:1", Ct);
        Assert.Empty(await s.ListPlayerAnalyticsEventsAsync("p1", "players", "steam:1", 10, Ct));
        Assert.Single(await s.ListPlayerAnalyticsEventsAsync("p1", "players", "steam:1:alt", 10, Ct));
    }

    // ── player_analytics_events (V4) ────────────────────────────────

    [SkippableFact]
    public async Task Player_events_filter_inclusive_time_range_newest_first_and_count()
    {
        var s = await StoreAsync();
        Assert.Equal(0, await s.CountPlayerEventsAsync("p1", "7656", Ct));
        foreach (var (ts, id) in new[] { (100L, "a"), (200L, "b"), (200L, "a"), (300L, "c"), (400L, "d") })
            await s.InsertPlayerAnalyticsEventV2Async("p1", "7656", ts, id, "evt", "cat", "lbl", null!, null!, Json.Parse($$"""{"ts":{{ts}}}"""), Ct);
        await s.InsertPlayerAnalyticsEventV2Async("p1", "other", 250, "x", "evt", "cat", "lbl", "ep", "col", Json.Parse("{}"), Ct);

        var range = await s.ListPlayerEventsAsync("p1", "7656", 200, 300, 10, Ct);
        Assert.Equal(new[] { (300L, "c"), (200L, "a"), (200L, "b") }, range.Select(r => (Json.Long(r, "created_at_unix_ms"), Json.Str(r, "event_id"))));
        Json.Equal("""{"steam_id":"7656","created_at_unix_ms":300,"event_id":"c","event_type":"evt","category":"cat","label":"lbl","endpoint_slug":"","collection_id":"","payload_json":{"ts":300}}""", range[0]);

        var limited = await s.ListPlayerEventsAsync("p1", "7656", 0, long.MaxValue, 2, Ct);
        Assert.Equal(new[] { "d", "c" }, limited.Select(r => Json.Str(r, "event_id")));
        Assert.Empty(await s.ListPlayerEventsAsync("p1", "7656", 301, 399, 10, Ct));

        Assert.Equal(5, await s.CountPlayerEventsAsync("p1", "7656", Ct));
        await s.InsertPlayerAnalyticsEventV2Async("p1", "7656", 400, "d", "evt2", "cat", "lbl", "ep", "col", Json.Parse("{}"), Ct);
        Assert.Equal(5, await s.CountPlayerEventsAsync("p1", "7656", Ct)); // same key overwrites
        Assert.Equal("evt2", Json.Str((await s.ListPlayerEventsAsync("p1", "7656", 400, 400, 1, Ct))[0], "event_type"));
        Assert.Equal(1, await s.CountPlayerEventsAsync("p1", "other", Ct));
    }

    // ── player profiles / sessions ──────────────────────────────────

    [SkippableFact]
    public async Task Player_profiles_store_empty_string_defaults_and_overwrite()
    {
        var s = await StoreAsync();
        await s.UpsertPlayerProfileAsync("p1", "200", null!, true, null, 1000, null, null, null, 0, 0, null, null, null!, 1001, Ct);
        Json.Equal("""{"steam_id":"200","player_name":"","is_online":true,"online_since_unix_ms":null,"last_seen_unix_ms":1000,"last_heartbeat_unix_ms":null,"current_session_id":"","current_session_last_seconds":null,"total_seconds":0,"session_count":0,"last_event_type":"","last_endpoint_slug":"","managed_counters_json":{},"updated_at_unix_ms":1001}""",
            await s.ReadPlayerProfileAsync("p1", "200", Ct));

        await s.UpsertPlayerProfileAsync("p1", "200", "Alice", false, 500, 2000, 1900, "s1", 60, 3600, 4, "logout", "save", """{"kills":3}""", 2001, Ct);
        Json.Equal("""{"steam_id":"200","player_name":"Alice","is_online":false,"online_since_unix_ms":500,"last_seen_unix_ms":2000,"last_heartbeat_unix_ms":1900,"current_session_id":"s1","current_session_last_seconds":60,"total_seconds":3600,"session_count":4,"last_event_type":"logout","last_endpoint_slug":"save","managed_counters_json":{"kills":3},"updated_at_unix_ms":2001}""",
            await s.ReadPlayerProfileAsync("p1", "200", Ct));

        await s.UpsertPlayerProfileAsync("p1", "100", "Bob", false, null, 1, null, null, null, 0, 0, null, null, "corrupt{", 1, Ct);
        var profiles = await s.ReadProjectProfilesAsync("p1", Ct);
        Assert.Equal(new[] { "100", "200" }, profiles.Select(r => Json.Str(r, "steam_id")));
        Assert.Equal(JsonValueKind.Null, profiles[0].GetProperty("managed_counters_json").ValueKind); // corrupt JSON degrades to null
        Assert.Null(await s.ReadPlayerProfileAsync("p1", "300", Ct));
        Assert.Empty(await s.ReadProjectProfilesAsync("p2", Ct));
    }

    [SkippableFact]
    public async Task Player_sessions_overwrite_and_parse_json_columns()
    {
        var s = await StoreAsync();
        await s.InsertPlayerSessionAsync("p1", "200", "sess-1", 100, null, null, null, null, Ct);
        Json.Equal("""{"session_id":"sess-1","started_at_unix_ms":100,"last_heartbeat_at_unix_ms":null,"ended_at_unix_ms":null,"last_metrics_json":null,"summary_json":null}""",
            await s.ReadPlayerSessionAsync("p1", "200", "sess-1", Ct));

        await s.InsertPlayerSessionAsync("p1", "200", "sess-1", null, 150, 200, """{"fps":60}""", """{"duration":100}""", Ct);
        Json.Equal("""{"session_id":"sess-1","started_at_unix_ms":null,"last_heartbeat_at_unix_ms":150,"ended_at_unix_ms":200,"last_metrics_json":{"fps":60},"summary_json":{"duration":100}}""",
            await s.ReadPlayerSessionAsync("p1", "200", "sess-1", Ct));
        Assert.Null(await s.ReadPlayerSessionAsync("p1", "200", "sess-2", Ct));
        Assert.Null(await s.ReadPlayerSessionAsync("p1", "201", "sess-1", Ct));
    }

    // ── project issues ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Project_issues_are_bucketed_by_date_newest_first()
    {
        var s = await StoreAsync();
        await s.InsertProjectIssueAsync("p1", "2026-10-08", 10, "e2", "7656", "error", "endpoint_failed", "Boom", Json.Parse("""{"status":500}"""), Ct);
        await s.InsertProjectIssueAsync("p1", "2026-10-08", 30, "e3", "7656", "warn", "slow", "Slow", Json.Parse("{}"), Ct);
        await s.InsertProjectIssueAsync("p1", "2026-10-08", 10, "e1", "7657", "error", "x", "X", Json.Parse("{}"), Ct);
        await s.InsertProjectIssueAsync("p1", "2026-10-07", 99, "e0", "7656", "error", "x", "X", Json.Parse("{}"), Ct);

        var issues = await s.ListProjectIssuesAsync("p1", "2026-10-08", 10, Ct);
        Assert.Equal(new[] { "e3", "e1", "e2" }, issues.Select(r => Json.Str(r, "event_id")));
        Json.Equal("""{"created_at_unix_ms":10,"event_id":"e2","steam_id":"7656","category":"error","event_type":"endpoint_failed","label":"Boom","payload_json":{"status":500}}""", issues[2]);
        Assert.Single(await s.ListProjectIssuesAsync("p1", "2026-10-08", 1, Ct));
        Assert.Single(await s.ListProjectIssuesAsync("p1", "2026-10-07", 10, Ct));
        Assert.Empty(await s.ListProjectIssuesAsync("p1", "2026-10-06", 10, Ct));
    }

    // ── storage errors / request log ────────────────────────────────

    [SkippableFact]
    public async Task Storage_errors_newest_first_overwrite_same_millisecond_and_purge_strictly_before()
    {
        var s = await StoreAsync();
        await s.InsertStorageErrorAsync("p1", 100, "err-1", "first", null, null, null, "error", Ct);
        await s.InsertStorageErrorAsync("p1", 200, "err-2", "second", "trace", "worker", "/v3/x", "warning", Ct);
        await s.InsertStorageErrorAsync("p1", 300, "err-3", "third", null, null, null, "error", Ct);
        await s.InsertStorageErrorAsync("p1", 300, "err-4", "fourth", null, null, null, "error", Ct); // same ms: overwrites

        var errors = await s.ListStorageErrorsAsync("p1", 10, Ct);
        Assert.Equal(new[] { "err-4", "err-2", "err-1" }, errors.Select(r => Json.Str(r, "error_id")));
        Json.Equal("""{"created_at_unix_ms":100,"error_id":"err-1","message":"first","stack_trace":"","source":"","request_path":"","severity":"error"}""", errors[2]);
        Json.Equal("""{"created_at_unix_ms":200,"error_id":"err-2","message":"second","stack_trace":"trace","source":"worker","request_path":"/v3/x","severity":"warning"}""", errors[1]);
        Assert.Single(await s.ListStorageErrorsAsync("p1", 1, Ct));

        await s.PurgeStorageErrorsAsync("p1", 200, Ct);
        Assert.Equal(new long[] { 300, 200 }, (await s.ListStorageErrorsAsync("p1", 10, Ct)).Select(r => Json.Long(r, "created_at_unix_ms")));
        await s.PurgeStorageErrorsAsync("p2", long.MaxValue, Ct);
        Assert.Equal(2, (await s.ListStorageErrorsAsync("p1", 10, Ct)).Count);
    }

    [SkippableFact]
    public async Task Request_log_newest_first_limit_and_purge_strictly_before()
    {
        var s = await StoreAsync();
        await s.InsertStorageRequestLogAsync("p1", 1000, "GET", "/v3/records", 200, 12, "pk_1", Ct);
        await s.InsertStorageRequestLogAsync("p1", 3000, "POST", "/v3/endpoints/buy", 500, 99, null, Ct);
        await s.InsertStorageRequestLogAsync("p1", 2000, "GET", "/v3/x", 404, 1, null, Ct);
        await s.InsertStorageRequestLogAsync("p2", 5000, "GET", "/", 200, 1, null, Ct);

        var log = await s.ListStorageRequestLogAsync("p1", 10, Ct);
        Assert.Equal(new long[] { 3000, 2000, 1000 }, log.Select(r => Json.Long(r, "created_at_unix_ms")));
        Json.Equal("""{"created_at_unix_ms":3000,"method":"POST","path":"/v3/endpoints/buy","status_code":500,"duration_ms":99,"api_key_identifier":""}""", log[0]);
        Json.Equal("""{"created_at_unix_ms":1000,"method":"GET","path":"/v3/records","status_code":200,"duration_ms":12,"api_key_identifier":"pk_1"}""", log[2]);
        Assert.Equal(2, (await s.ListStorageRequestLogAsync("p1", 2, Ct)).Count);

        await s.PurgeStorageRequestLogAsync("p1", 2001, Ct);
        Assert.Equal(new long[] { 3000 }, (await s.ListStorageRequestLogAsync("p1", 10, Ct)).Select(r => Json.Long(r, "created_at_unix_ms")));
        Assert.Single(await s.ListStorageRequestLogAsync("p2", 10, Ct));
    }

    // ── validation shared by every driver ───────────────────────────

    [SkippableFact]
    public async Task Invalid_ids_record_keys_payloads_and_limits_are_rejected()
    {
        var s = await StoreAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReadProjectAsync("bad id", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReadProjectAsync("", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertProjectAsync(new string('a', 129), Json.Parse("{}"), 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReadCollectionAsync("p1", "a:b", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertRecordAsync("p1", "c", "bad/key", Json.Parse("{}"), false, 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReadPlayerProfileAsync("p1", "steam id", Ct));

        var tooLarge = Json.Parse("\"" + new string('x', 64 * 1024) + "\"");
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertRecordAsync("p1", "c", "k", tooLarge, false, 1, Ct));
        Assert.Null(await s.ReadRecordAsync("p1", "c", "k", Ct));
        var justFits = Json.Parse("\"" + new string('x', 64 * 1024 - 2) + "\"");
        await s.UpsertRecordAsync("p1", "c", "k", justFits, false, 1, Ct);
        Assert.NotNull(await s.ReadRecordAsync("p1", "c", "k", Ct));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.ListAuditLogsAsync("p1", 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.ListStorageErrorsAsync("p1", -1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.ListPlayerEventsAsync("p1", "s", 0, 1, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.ReadProjectUsageEndpointsAsync("p1", "2026-10", 0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.PutWorkspaceObjectAsync("//", "x", Ct));
    }
}
