namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract partial class StoreConformanceTests
{
    // ── projects ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Project_upsert_read_overwrite_delete()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadProjectAsync("p1", Ct));

        await s.UpsertProjectAsync("p1", Json.Parse("""{"name":"One","workspaceId":"w1","n":1}"""), 1, Ct);
        Json.Equal("""{"name":"One","workspaceId":"w1","n":1}""", await s.ReadProjectAsync("p1", Ct));

        await s.UpsertProjectAsync("p1", Json.Parse("""{"name":"Two"}"""), 2, Ct);
        Json.Equal("""{"name":"Two"}""", await s.ReadProjectAsync("p1", Ct));

        await s.DeleteProjectAsync("p1", Ct);
        Assert.Null(await s.ReadProjectAsync("p1", Ct));
        await s.DeleteProjectAsync("p1", Ct); // deleting a missing row is a no-op
    }

    [SkippableFact]
    public async Task Project_delete_cleans_usage_counters_and_query_telemetry()
    {
        var s = await StoreAsync();
        await s.UpsertProjectAsync("p1", Json.Parse("{}"), 1, Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-08", "ep", Delta(requests: 1, storage: 100), Ct);
        await s.IncrementProjectUsageAsync("p2", "2026-10", "2026-10-08", "ep", Delta(requests: 1, storage: 50), Ct);
        await s.RecordQueryRunAsync("p1", "q1", "2026-10-08T00:00:00Z", 5, 1, 1, false, Ct);

        await s.DeleteProjectAsync("p1", Ct);

        Assert.Null(await s.ReadProjectUsageMonthlyAsync("p1", "2026-10", Ct));
        Assert.Empty(await s.ReadProjectUsageDailyAsync("p1", "2026-10", Ct));
        Assert.Empty(await s.ReadProjectUsageEndpointsAsync("p1", "2026-10", 10, Ct));
        Assert.Equal(0, await s.ReadProjectStorageBytesAsync("p1", Ct));
        Assert.Null(await s.ReadQueryLastRunAsync("p1", "q1", Ct));
        Assert.Empty(await s.ListQueryLogsAsync("p1", "q1", 10, Ct));
        Assert.Equal(50, await s.ReadProjectStorageBytesAsync("p2", Ct));
    }

    // ── collections ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task Collection_rows_overwrite_order_and_delete()
    {
        var s = await StoreAsync();
        await s.UpsertCollectionAsync("p1", "coins", "Coins", "public", Json.Parse("""{"fields":[1,2]}"""), 2, Ct);
        Json.Equal($$"""{"collection_id":"coins","name":"Coins","visibility":"public","definition_json":{"fields":[1,2]},"version":2,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadCollectionAsync("p1", "coins", Ct));

        Time.Advance(1000);
        await s.UpsertCollectionAsync("p1", "coins", "Gold", "private", Json.Parse("[]"), 3, Ct);
        Json.Equal($$"""{"collection_id":"coins","name":"Gold","visibility":"private","definition_json":[],"version":3,"updated_at_unix_ms":{{Start + 1000}}}""",
            await s.ReadCollectionAsync("p1", "coins", Ct));

        foreach (var id in new[] { "b", "a_1", "B", "a" })
            await s.UpsertCollectionAsync("p1", id, id, "public", Json.Parse("{}"), 1, Ct);
        await s.UpsertCollectionAsync("p2", "zz", "other", "public", Json.Parse("{}"), 1, Ct);
        var ids = (await s.ListCollectionsAsync("p1", Ct)).Select(r => Json.Str(r, "collection_id")).ToArray();
        Assert.Equal(new[] { "B", "a", "a_1", "b", "coins" }, ids); // bytewise clustering order

        await s.DeleteCollectionAsync("p1", "coins", Ct);
        Assert.Null(await s.ReadCollectionAsync("p1", "coins", Ct));
        Assert.Equal(4, (await s.ListCollectionsAsync("p1", Ct)).Count);
        Assert.Empty(await s.ListCollectionsAsync("p3", Ct));
    }

    [SkippableFact]
    public async Task Collection_reserved_namespace_is_rejected()
    {
        var s = await StoreAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertCollectionAsync("p1", "__sbox_players", "x", "public", Json.Parse("{}"), 1, Ct));
        Assert.Null(await s.ReadCollectionAsync("p1", "__sbox_players", Ct));
    }

    // ── endpoints / workflows ───────────────────────────────────────

    [SkippableFact]
    public async Task Endpoint_rows_overwrite_order_and_delete()
    {
        var s = await StoreAsync();
        await s.UpsertEndpointAsync("p1", "ep2", "buy-item", "POST", false, Json.Parse("""{"steps":["a"]}"""), null, 7, Ct);
        Json.Equal($$"""{"endpoint_id":"ep2","slug":"buy-item","method":"POST","enabled":false,"definition_json":{"steps":["a"]},"version_hash":null,"version":7,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadEndpointAsync("p1", "ep2", Ct));

        await s.UpsertEndpointAsync("p1", "ep2", "sell-item", "GET", true, Json.Parse("{}"), "hash", 8, Ct);
        await s.UpsertEndpointAsync("p1", "ep1", "a", "GET", true, Json.Parse("{}"), "h", 1, Ct);
        var rows = await s.ListEndpointsAsync("p1", Ct);
        Assert.Equal(new[] { "ep1", "ep2" }, rows.Select(r => Json.Str(r, "endpoint_id")));
        Json.Equal($$"""{"endpoint_id":"ep2","slug":"sell-item","method":"GET","enabled":true,"definition_json":{},"version_hash":"hash","version":8,"updated_at_unix_ms":{{Start}}}""", rows[1]);

        await s.DeleteEndpointAsync("p1", "ep2", Ct);
        Assert.Null(await s.ReadEndpointAsync("p1", "ep2", Ct));
        Assert.Single(await s.ListEndpointsAsync("p1", Ct));
    }

    [SkippableFact]
    public async Task Workflow_rows_overwrite_order_and_delete()
    {
        var s = await StoreAsync();
        await s.UpsertWorkflowAsync("p1", "wf-b", "Grant", Json.Parse("""{"a":true}"""), "v1", 1, Ct);
        await s.UpsertWorkflowAsync("p1", "wf-a", "Reset", Json.Parse("""{"b":null}"""), null, 2, Ct);
        Json.Equal($$"""{"workflow_id":"wf-b","name":"Grant","definition_json":{"a":true},"version_hash":"v1","version":1,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadWorkflowAsync("p1", "wf-b", Ct));
        Assert.Equal(new[] { "wf-a", "wf-b" }, (await s.ListWorkflowsAsync("p1", Ct)).Select(r => Json.Str(r, "workflow_id")));

        await s.UpsertWorkflowAsync("p1", "wf-b", "Grant2", Json.Parse("{}"), null, 5, Ct);
        Json.Equal($$"""{"workflow_id":"wf-b","name":"Grant2","definition_json":{},"version_hash":null,"version":5,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadWorkflowAsync("p1", "wf-b", Ct));

        await s.DeleteWorkflowAsync("p1", "wf-b", Ct);
        Assert.Null(await s.ReadWorkflowAsync("p1", "wf-b", Ct));
    }

    // ── game values / rate limit rules ──────────────────────────────

    [SkippableFact]
    public async Task Game_values_and_rate_limit_rules_overwrite_and_delete()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadGameValuesAsync("p1", Ct));
        await s.UpsertGameValuesAsync("p1", Json.Parse("""{"maxHp":100,"names":["a"]}"""), "gv1", 4, Ct);
        Json.Equal($$"""{"payload_json":{"maxHp":100,"names":["a"]},"version_hash":"gv1","version":4,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadGameValuesAsync("p1", Ct));
        Time.Advance(5);
        await s.UpsertGameValuesAsync("p1", Json.Parse("""{"maxHp":200}"""), null, 5, Ct);
        Json.Equal($$"""{"payload_json":{"maxHp":200},"version_hash":null,"version":5,"updated_at_unix_ms":{{Start + 5}}}""",
            await s.ReadGameValuesAsync("p1", Ct));
        await s.DeleteGameValuesAsync("p1", Ct);
        Assert.Null(await s.ReadGameValuesAsync("p1", Ct));

        await s.UpsertRateLimitRulesAsync("p1", Json.Parse("""[{"limit":10}]"""), 9, Ct);
        Json.Equal($$"""{"rules_json":[{"limit":10}],"version":9,"updated_at_unix_ms":{{Start + 5}}}""", await s.ReadRateLimitRulesAsync("p1", Ct));
        await s.UpsertRateLimitRulesAsync("p1", Json.Parse("[]"), 10, Ct);
        Json.Equal($$"""{"rules_json":[],"version":10,"updated_at_unix_ms":{{Start + 5}}}""", await s.ReadRateLimitRulesAsync("p1", Ct));
        await s.DeleteRateLimitRulesAsync("p1", Ct);
        Assert.Null(await s.ReadRateLimitRulesAsync("p1", Ct));
    }

    // ── queries + run tracking ──────────────────────────────────────

    [SkippableFact]
    public async Task Query_rows_overwrite_order_and_delete()
    {
        var s = await StoreAsync();
        await s.UpsertQueryAsync("p1", "top", "Top players", true, Json.Parse("""{"from":"scores"}"""), 1, Ct);
        await s.UpsertQueryAsync("p1", "all", "All", false, Json.Parse("{}"), 1, Ct);
        Json.Equal($$"""{"query_id":"top","name":"Top players","requires_secret_key":true,"definition_json":{"from":"scores"},"version":1,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadQueryAsync("p1", "top", Ct));
        Assert.Equal(new[] { "all", "top" }, (await s.ListQueriesAsync("p1", Ct)).Select(r => Json.Str(r, "query_id")));

        await s.UpsertQueryAsync("p1", "top", "Renamed", false, Json.Parse("{}"), 2, Ct);
        Assert.Equal("Renamed", Json.Str((await s.ReadQueryAsync("p1", "top", Ct))!.Value, "name"));
        Assert.False((await s.ReadQueryAsync("p1", "top", Ct))!.Value.GetProperty("requires_secret_key").GetBoolean());

        await s.DeleteQueryAsync("p1", "top", Ct);
        Assert.Null(await s.ReadQueryAsync("p1", "top", Ct));
        Assert.Single(await s.ListQueriesAsync("p1", Ct));
    }

    [SkippableFact]
    public async Task Query_runs_track_last_run_and_newest_first_logs()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadQueryLastRunAsync("p1", "q1", Ct));
        Assert.Empty(await s.ListQueryLogsAsync("p1", "q1", 10, Ct));

        await s.RecordQueryRunAsync("p1", "q1", "2026-10-08T10:00:00Z", 12, 30, 3, false, Ct);
        Json.Equal($$"""{"run_at":"2026-10-08T10:00:00Z","duration_ms":12,"keys_scanned":30,"records_returned":3,"from_cache":false,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadQueryLastRunAsync("p1", "q1", Ct));

        Time.Advance(10);
        await s.RecordQueryRunAsync("p1", "q1", "2026-10-08T10:00:01Z", 1, 0, 3, true, Ct);
        Time.Advance(10);
        await s.RecordQueryRunAsync("p1", "q0", "2026-10-08T10:00:02Z", 2, 2, 2, false, Ct);

        Json.Equal($$"""{"run_at":"2026-10-08T10:00:01Z","duration_ms":1,"keys_scanned":0,"records_returned":3,"from_cache":true,"updated_at_unix_ms":{{Start + 10}}}""",
            await s.ReadQueryLastRunAsync("p1", "q1", Ct));

        var lastRuns = await s.ListQueryLastRunsAsync("p1", Ct);
        Assert.Equal(new[] { "q0", "q1" }, lastRuns.Select(r => Json.Str(r, "query_id")));
        Json.Equal($$"""{"query_id":"q1","run_at":"2026-10-08T10:00:01Z","duration_ms":1,"keys_scanned":0,"records_returned":3,"from_cache":true,"updated_at_unix_ms":{{Start + 10}}}""", lastRuns[1]);

        var logs = await s.ListQueryLogsAsync("p1", "q1", 10, Ct);
        Assert.Equal(new[] { Start + 10, Start }, logs.Select(r => Json.Long(r, "created_at_unix_ms")));
        Json.Equal($$"""{"created_at_unix_ms":{{Start + 10}},"log_type":"run","duration_ms":1,"keys_scanned":0,"records_returned":3,"from_cache":true,"changes_json":null}""", logs[0]);
        Assert.Single(await s.ListQueryLogsAsync("p1", "q1", 1, Ct));
        Assert.Single(await s.ListQueryLogsAsync("p1", "q1", 0, Ct)); // clamped to [1, 200]
        Assert.Equal(2, (await s.ListQueryLogsAsync("p1", "q1", 5000, Ct)).Count);
    }

    [SkippableFact]
    public async Task Query_run_logs_overwrite_within_a_millisecond_and_expire_after_90_days()
    {
        var s = await StoreAsync();
        await s.RecordQueryRunAsync("p1", "q1", "a", 1, 1, 1, false, Ct);
        await s.RecordQueryRunAsync("p1", "q1", "b", 2, 2, 2, false, Ct);
        var logs = await s.ListQueryLogsAsync("p1", "q1", 10, Ct);
        Assert.Single(logs);
        Assert.Equal(2, Json.Long(logs[0], "duration_ms"));

        Time.Advance((long)TimeSpan.FromDays(90).TotalMilliseconds);
        Assert.Empty(await s.ListQueryLogsAsync("p1", "q1", 10, Ct));
        Assert.NotNull(await s.ReadQueryLastRunAsync("p1", "q1", Ct)); // only the log table has a TTL
    }

    // ── api keys ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Api_key_rows_overwrite_order_and_delete()
    {
        var s = await StoreAsync();
        await s.UpsertApiKeyAsync("p1", "sk_b", "u1", "secret", "hash1", "sk_b…", "Server", true, Json.Parse("""{"records":"rw"}"""), 1, Ct);
        await s.UpsertApiKeyAsync("p1", "pk_a", "u2", "public", "hash2", "pk_a…", "Client", false, Json.Parse("{}"), 1, Ct);
        Json.Equal($$"""{"api_key":"sk_b","user_id":"u1","key_type":"secret","key_hash":"hash1","key_identifier":"sk_b…","label":"Server","enabled":true,"permissions_json":{"records":"rw"},"version":1,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadApiKeyAsync("p1", "sk_b", Ct));
        Assert.Equal(new[] { "pk_a", "sk_b" }, (await s.ListApiKeysAsync("p1", Ct)).Select(r => Json.Str(r, "api_key")));

        await s.UpsertApiKeyAsync("p1", "sk_b", "u1", "secret", "hash1", "sk_b…", "Renamed", false, Json.Parse("""{"records":"r"}"""), 2, Ct);
        var row = (await s.ReadApiKeyAsync("p1", "sk_b", Ct))!.Value;
        Assert.Equal("Renamed", Json.Str(row, "label"));
        Assert.False(row.GetProperty("enabled").GetBoolean());
        Assert.Equal("r", row.GetProperty("permissions_json").GetProperty("records").GetString());

        await s.DeleteApiKeyAsync("p1", "sk_b", Ct);
        Assert.Null(await s.ReadApiKeyAsync("p1", "sk_b", Ct));
        Assert.Single(await s.ListApiKeysAsync("p1", Ct));
    }

    // ── project members / pages ─────────────────────────────────────

    [SkippableFact]
    public async Task Project_memberships_are_partitioned_by_user()
    {
        var s = await StoreAsync();
        await s.UpsertProjectMembershipAsync("user-1", "p2", "editor", 200, Ct);
        await s.UpsertProjectMembershipAsync("user-1", "p1", "owner", 100, Ct);
        await s.UpsertProjectMembershipAsync("user-2", "p1", "viewer", 300, Ct);

        var rows = await s.ListProjectsForUserAsync("user-1", Ct);
        Assert.Equal(2, rows.Count);
        Json.Equal("""{"project_id":"p1","role":"owner","created_at_unix_ms":100}""", rows[0]);
        Json.Equal("""{"project_id":"p2","role":"editor","created_at_unix_ms":200}""", rows[1]);

        await s.UpsertProjectMembershipAsync("user-1", "p1", "admin", 150, Ct);
        Json.Equal("""{"project_id":"p1","role":"admin","created_at_unix_ms":150}""", (await s.ListProjectsForUserAsync("user-1", Ct))[0]);

        await s.DeleteProjectMembershipAsync("user-1", "p1", Ct);
        Assert.Equal(new[] { "p2" }, (await s.ListProjectsForUserAsync("user-1", Ct)).Select(r => Json.Str(r, "project_id")));
        Assert.Single(await s.ListProjectsForUserAsync("user-2", Ct));
        Assert.Empty(await s.ListProjectsForUserAsync("nobody", Ct));
    }

    [SkippableFact]
    public async Task Pages_keep_content_json_as_raw_text()
    {
        var s = await StoreAsync();
        await s.UpsertPageAsync("p1", "welcome", "Welcome", """{"blocks":[1]}""", 10, 20, Ct);
        await s.UpsertPageAsync("p1", "about", "About", "not json at all", 5, 6, Ct);
        Json.Equal("""{"page_slug":"welcome","title":"Welcome","content_json":"{\"blocks\":[1]}","created_at_unix_ms":10,"updated_at_unix_ms":20}""",
            await s.ReadPageAsync("p1", "welcome", Ct));
        var pages = await s.ListPagesAsync("p1", Ct);
        Assert.Equal(new[] { "about", "welcome" }, pages.Select(r => Json.Str(r, "page_slug")));
        Assert.Equal("not json at all", Json.Str(pages[0], "content_json"));

        await s.UpsertPageAsync("p1", "welcome", "Hi", "{}", 10, 30, Ct);
        Json.Equal("""{"page_slug":"welcome","title":"Hi","content_json":"{}","created_at_unix_ms":10,"updated_at_unix_ms":30}""",
            await s.ReadPageAsync("p1", "welcome", Ct));

        await s.DeletePageAsync("p1", "welcome", Ct);
        Assert.Null(await s.ReadPageAsync("p1", "welcome", Ct));
        Assert.Single(await s.ListPagesAsync("p1", Ct));
    }

    protected static UsageDelta Delta(long requests = 0, long reads = 0, long writes = 0, long endpointCalls = 0, long bytesIn = 0, long bytesOut = 0,
        long errors = 0, long durationMsSum = 0, long durationSamples = 0, long storage = 0, long computeUnits = 0)
        => new(requests, reads, writes, endpointCalls, bytesIn, bytesOut, errors, durationMsSum, durationSamples, storage, computeUnits);
}
