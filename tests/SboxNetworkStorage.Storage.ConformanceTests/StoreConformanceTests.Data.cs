namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract partial class StoreConformanceTests
{
    // ── records ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Records_keep_deleted_flag_overwrite_and_list_in_key_order()
    {
        var s = await StoreAsync();
        await s.UpsertRecordAsync("p1", "inv", "steam:2", Json.Parse("""{"gold":5}"""), false, 1, Ct);
        Json.Equal($$"""{"record_key":"steam:2","payload_json":{"gold":5},"deleted":false,"version":1,"updated_at_unix_ms":{{Start}}}""",
            await s.ReadRecordAsync("p1", "inv", "steam:2", Ct));

        Time.Advance(7);
        await s.UpsertRecordAsync("p1", "inv", "steam:2", Json.Parse("null"), true, 2, Ct);
        Json.Equal($$"""{"record_key":"steam:2","payload_json":null,"deleted":true,"version":2,"updated_at_unix_ms":{{Start + 7}}}""",
            await s.ReadRecordAsync("p1", "inv", "steam:2", Ct));

        await s.UpsertRecordAsync("p1", "inv", "steam:10", Json.Parse("[1]"), false, 1, Ct);
        await s.UpsertRecordAsync("p1", "inv", "A", Json.Parse("1"), false, 1, Ct);
        await s.UpsertRecordAsync("p1", "other", "steam:3", Json.Parse("{}"), false, 1, Ct);
        var rows = await s.ListRecordsAsync("p1", "inv", Ct);
        Assert.Equal(new[] { "A", "steam:10", "steam:2" }, rows.Select(r => Json.Str(r, "record_key")));
        Assert.True(rows[2].GetProperty("deleted").GetBoolean()); // tombstoned rows are still listed
        Assert.Equal(1, rows[0].GetProperty("payload_json").GetInt32());

        await s.DeleteRecordAsync("p1", "inv", "steam:2", Ct);
        Assert.Null(await s.ReadRecordAsync("p1", "inv", "steam:2", Ct));
        Assert.Equal(2, (await s.ListRecordsAsync("p1", "inv", Ct)).Count);
        Assert.Single(await s.ListRecordsAsync("p1", "other", Ct));
    }

    [SkippableFact]
    public async Task Conditional_record_mutations_check_missing_version_and_snapshot_atomically()
    {
        var store = await StoreAsync();
        foreach (var global in new[] { false, true })
        {
            var collection = global ? "global" : "player";
            var payload = Json.Parse("""{"gold":1}""");
            var creates = await Task.WhenAll(
                store.TryMutateRecordAsync("p1", collection, "key", global, payload, false, null, Ct),
                store.TryMutateRecordAsync("p1", collection, "key", global, payload, false, null, Ct));
            Assert.Equal(1, creates.Count(success => success));
            var row = global ? await store.ReadGlobalRecordAsync("p1", collection, "key", Ct)
                : await store.ReadRecordAsync("p1", collection, "key", Ct);
            var snapshot = new RecordMutationSnapshot(row!.Value.GetProperty("payload_json").GetRawText(), Time.GetUtcNow().ToUnixTimeMilliseconds());
            Time.Advance(1);
            var updates = await Task.WhenAll(
                store.TryMutateRecordAsync("p1", collection, "key", global, Json.Parse("""{"gold":2}"""), false, 1, Ct, snapshot),
                store.TryMutateRecordAsync("p1", collection, "key", global, Json.Parse("""{"gold":3}"""), false, 1, Ct, snapshot));
            Assert.Equal(1, updates.Count(success => success));
            Assert.False(await store.TryMutateRecordAsync("p1", collection, "key", global, payload, true, 1, Ct, snapshot));
            Assert.True(await store.TryMutateRecordAsync("p1", collection, "key", global, Json.Parse("null"), true, 2, Ct));
            row = global ? await store.ReadGlobalRecordAsync("p1", collection, "key", Ct)
                : await store.ReadRecordAsync("p1", collection, "key", Ct);
            if (global) Assert.Null(row);
            else
            {
                Assert.True(row!.Value.GetProperty("deleted").GetBoolean());
                Assert.Equal(3, row.Value.GetProperty("version").GetInt64());
            }
            Assert.False(await store.TryMutateRecordAsync("p1", collection, "key", global, payload, false, 2, Ct));
            Assert.True(await store.TryMutateRecordAsync("p1", collection, "key", global, payload, false, null, Ct));
            row = global ? await store.ReadGlobalRecordAsync("p1", collection, "key", Ct)
                : await store.ReadRecordAsync("p1", collection, "key", Ct);
            Assert.Equal(global ? 1 : 4, row!.Value.GetProperty("version").GetInt64());
        }
    }

    [SkippableFact]
    public async Task Conditional_snapshot_rejects_unconditional_writer_reusing_version_even_at_same_timestamp()
    {
        var store = await StoreAsync();
        foreach (var global in new[] { false, true })
        {
            var collection = global ? "global" : "player";
            if (global) await store.UpsertGlobalRecordAsync("p1", collection, "key", Json.Parse("""{"gold":1}"""), 1, Ct);
            else await store.UpsertRecordAsync("p1", collection, "key", Json.Parse("""{"gold":1}"""), false, 1, Ct);
            var snapshot = new RecordMutationSnapshot("""{"gold":1}""", Start);
            // Existing data-plane writers can reuse version 1 and share a millisecond.
            if (global) await store.UpsertGlobalRecordAsync("p1", collection, "key", Json.Parse("""{"gold":8}"""), 1, Ct);
            else await store.UpsertRecordAsync("p1", collection, "key", Json.Parse("""{"gold":8}"""), false, 1, Ct);
            Assert.False(await store.TryMutateRecordAsync("p1", collection, "key", global, Json.Parse("""{"gold":99}"""), false, 1, Ct, snapshot));
            Assert.False(await store.TryMutateRecordAsync("p1", collection, "key", global, Json.Parse("null"), true, 1, Ct, snapshot));
            var row = global ? await store.ReadGlobalRecordAsync("p1", collection, "key", Ct)
                : await store.ReadRecordAsync("p1", collection, "key", Ct);
            Assert.Equal(8, row!.Value.GetProperty("payload_json").GetProperty("gold").GetInt32());
            Assert.Equal(1, row.Value.GetProperty("version").GetInt64());
        }
    }

    [SkippableFact]
    public async Task Record_idempotency_rows_round_trip_and_overwrite()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadRecordIdempotencyAsync("p1", "inv", "k", "idem-1", Ct));
        await s.UpsertRecordIdempotencyAsync("p1", "inv", "k", "idem-1", 4, "sha:abc", Json.Parse("""{"ok":true}"""), Ct);
        Json.Equal($$"""{"idempotency_key":"idem-1","result_record_version":4,"result_hash":"sha:abc","payload_json":{"ok":true},"created_at_unix_ms":{{Start}}}""",
            await s.ReadRecordIdempotencyAsync("p1", "inv", "k", "idem-1", Ct));
        Assert.Null(await s.ReadRecordIdempotencyAsync("p1", "inv", "k", "idem-2", Ct));
        Assert.Null(await s.ReadRecordIdempotencyAsync("p1", "inv", "k2", "idem-1", Ct));

        Time.Advance(3);
        await s.UpsertRecordIdempotencyAsync("p1", "inv", "k", "idem-1", 5, "sha:def", Json.Parse("{}"), Ct);
        Json.Equal($$"""{"idempotency_key":"idem-1","result_record_version":5,"result_hash":"sha:def","payload_json":{},"created_at_unix_ms":{{Start + 3}}}""",
            await s.ReadRecordIdempotencyAsync("p1", "inv", "k", "idem-1", Ct));

        await s.DeleteRecordIdempotencyAsync("p1", "inv", "k", "idem-1", Ct);
        Assert.Null(await s.ReadRecordIdempotencyAsync("p1", "inv", "k", "idem-1", Ct));
    }

    [SkippableFact]
    public async Task Global_records_overwrite_and_list_in_id_order()
    {
        var s = await StoreAsync();
        await s.UpsertGlobalRecordAsync("p1", "board", "season-2", Json.Parse("""{"top":[1,2]}"""), 1, Ct);
        await s.UpsertGlobalRecordAsync("p1", "board", "season-1", Json.Parse("{}"), 9, Ct);
        Json.Equal($$"""{"record_id":"season-2","payload_json":{"top":[1,2]},"version":1,"created_at_unix_ms":{{Start}}}""",
            await s.ReadGlobalRecordAsync("p1", "board", "season-2", Ct));
        Assert.Equal(new[] { "season-1", "season-2" }, (await s.ListGlobalRecordsAsync("p1", "board", Ct)).Select(r => Json.Str(r, "record_id")));

        Time.Advance(2);
        await s.UpsertGlobalRecordAsync("p1", "board", "season-2", Json.Parse("""{"top":[]}"""), 2, Ct);
        Json.Equal($$"""{"record_id":"season-2","payload_json":{"top":[]},"version":2,"created_at_unix_ms":{{Start + 2}}}""",
            await s.ReadGlobalRecordAsync("p1", "board", "season-2", Ct));

        await s.DeleteGlobalRecordAsync("p1", "board", "season-2", Ct);
        Assert.Null(await s.ReadGlobalRecordAsync("p1", "board", "season-2", Ct));
        Assert.Single(await s.ListGlobalRecordsAsync("p1", "board", Ct));
    }

    [SkippableFact]
    public async Task Live_records_count_and_page_by_key_with_optional_prefix()
    {
        var s = await StoreAsync();
        foreach (var key in new[] { "z:1", "765_b", "A", "765", "765xa", "7650_x", "765_a" })
            await s.UpsertRecordAsync("p1", "inv", key, Json.Parse("""{"gold":1}"""), false, 1, Ct);
        await s.UpsertRecordAsync("p1", "inv", "765_c", Json.Parse("null"), true, 2, Ct);   // tombstone
        await s.UpsertRecordAsync("p1", "inv", "765_n", Json.Parse("null"), false, 1, Ct);  // no payload
        await s.UpsertRecordAsync("p1", "other", "765_a", Json.Parse("{}"), false, 1, Ct);

        Assert.Equal(7, await s.CountLiveRecordsAsync("p1", "inv", false, null, Ct));
        Assert.Equal(new[] { "765", "7650_x", "765_a", "765_b", "765xa", "A", "z:1" },
            (await s.ListLiveRecordsAsync("p1", "inv", false, null, 0, 50, Ct)).Select(r => Json.Str(r, "record_key")));
        Assert.Equal(new[] { "765_a", "765_b" },
            (await s.ListLiveRecordsAsync("p1", "inv", false, null, 2, 2, Ct)).Select(r => Json.Str(r, "record_key")));
        Assert.Equal(new[] { "z:1" }, (await s.ListLiveRecordsAsync("p1", "inv", false, "", 6, 5, Ct)).Select(r => Json.Str(r, "record_key")));
        Assert.Empty(await s.ListLiveRecordsAsync("p1", "inv", false, null, 10, 5, Ct));

        // The prefix is literal: "_" is not a wildcard, and longer keys sharing digits do not match.
        Assert.Equal(2, await s.CountLiveRecordsAsync("p1", "inv", false, "765_", Ct));
        var page = await s.ListLiveRecordsAsync("p1", "inv", false, "765_", 0, 50, Ct);
        Assert.Equal(new[] { "765_a", "765_b" }, page.Select(r => Json.Str(r, "record_key")));
        Assert.Equal(1, page[0].GetProperty("payload_json").GetProperty("gold").GetInt32());
        Assert.Equal(0, await s.CountLiveRecordsAsync("p1", "inv", false, "zz", Ct));
        Assert.Equal(0, await s.CountLiveRecordsAsync("p1", "missing", false, null, Ct));

        await s.UpsertGlobalRecordAsync("p1", "board", "season-2", Json.Parse("{}"), 1, Ct);
        await s.UpsertGlobalRecordAsync("p1", "board", "season-1", Json.Parse("{}"), 1, Ct);
        await s.UpsertGlobalRecordAsync("p1", "board", "all-time", Json.Parse("{}"), 1, Ct);
        Assert.Equal(3, await s.CountLiveRecordsAsync("p1", "board", true, null, Ct));
        Assert.Equal(2, await s.CountLiveRecordsAsync("p1", "board", true, "season-", Ct));
        Assert.Equal(new[] { "season-2" },
            (await s.ListLiveRecordsAsync("p1", "board", true, "season-", 1, 5, Ct)).Select(r => Json.Str(r, "record_id")));

        await Assert.ThrowsAsync<ArgumentException>(() => s.CountLiveRecordsAsync("p1", "inv", false, "bad key", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.ListLiveRecordsAsync("p1", "inv", false, null, 0, 0, Ct));
    }

    // ── ledger / checkpoint ─────────────────────────────────────────

    [SkippableFact]
    public async Task Ledger_entries_are_ordered_by_numeric_sequence_and_overwrite_per_sequence()
    {
        var s = await StoreAsync();
        foreach (var seq in new long[] { 10, 2, 9, 1 })
            await s.InsertLedgerEntryAsync("p1", "wallet", "steam:1", seq, Json.Parse($$"""{"seq":{{seq}}}"""), Ct);
        // A record key that is a prefix of another must not leak entries ("steam:1" vs "steam:1:x").
        await s.InsertLedgerEntryAsync("p1", "wallet", "steam:1:x", 5, Json.Parse("{}"), Ct);
        await s.InsertLedgerEntryAsync("p1", "wallet", "steam:1", 9, Json.Parse("""{"seq":"nine"}"""), Ct);

        var entries = await s.ListLedgerEntriesAsync("p1", "wallet", "steam:1", Ct);
        Assert.Equal(new long[] { 1, 2, 9, 10 }, entries.Select(r => Json.Long(r, "sequence")));
        Json.Equal($$"""{"sequence":9,"entry_json":{"seq":"nine"},"created_at_unix_ms":{{Start}}}""", entries[2]);

        await s.DeleteLedgerEntriesAsync("p1", "wallet", "steam:1", Ct);
        Assert.Empty(await s.ListLedgerEntriesAsync("p1", "wallet", "steam:1", Ct));
        Assert.Single(await s.ListLedgerEntriesAsync("p1", "wallet", "steam:1:x", Ct));
    }

    [SkippableFact]
    public async Task Checkpoint_cursor_round_trips_overwrites_and_deletes()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadCheckpointCursorAsync("p1", Ct));
        await s.UpsertCheckpointCursorAsync("p1", 42, "manifests/42.json", 3, Ct);
        Json.Equal($$"""{"latest_sequence":42,"manifest_path":"manifests/42.json","updated_at_unix_ms":{{Start}},"version":3}""",
            await s.ReadCheckpointCursorAsync("p1", Ct));
        Time.Advance(1);
        await s.UpsertCheckpointCursorAsync("p1", 43, null, 4, Ct);
        Json.Equal($$"""{"latest_sequence":43,"manifest_path":null,"updated_at_unix_ms":{{Start + 1}},"version":4}""",
            await s.ReadCheckpointCursorAsync("p1", Ct));
        await s.DeleteCheckpointCursorAsync("p1", Ct);
        Assert.Null(await s.ReadCheckpointCursorAsync("p1", Ct));
    }

    // ── usage counters ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Usage_counters_accumulate_per_month_day_and_endpoint()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadProjectUsageMonthlyAsync("p1", "2026-10", Ct));

        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-02", "buy", new UsageDelta(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-01", "buy", new UsageDelta(1, 1, 1, 1, 1, 1, 1, 1, 1, -4, 1), Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-01", "alpha", Delta(endpointCalls: 2, errors: 1, durationMsSum: 30, computeUnits: 3), Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-01", null, Delta(requests: 5), Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-01", "", Delta(requests: 1), Ct);

        Json.Equal("""{"requests":8,"reads":3,"writes":4,"endpoint_calls":7,"bytes_in":6,"bytes_out":7,"errors":9,"duration_ms_sum":39,"duration_samples":10,"compute_units":15,"storage_delta_bytes":6}""",
            await s.ReadProjectUsageMonthlyAsync("p1", "2026-10", Ct));

        var daily = await s.ReadProjectUsageDailyAsync("p1", "2026-10", Ct);
        Assert.Equal(2, daily.Count);
        Json.Equal("""{"day":"2026-10-01","requests":7,"bytes_in":1,"bytes_out":1,"errors":2,"compute_units":4}""", daily[0]);
        Json.Equal("""{"day":"2026-10-02","requests":1,"bytes_in":5,"bytes_out":6,"errors":7,"compute_units":11}""", daily[1]);

        var endpoints = await s.ReadProjectUsageEndpointsAsync("p1", "2026-10", 10, Ct);
        Assert.Equal(2, endpoints.Count); // null/empty slugs never create endpoint rows
        Json.Equal("""{"endpoint_slug":"alpha","calls":2,"errors":1,"duration_ms_sum":30,"compute_units":3}""", endpoints[0]);
        Json.Equal("""{"endpoint_slug":"buy","calls":5,"errors":8,"duration_ms_sum":9,"compute_units":12}""", endpoints[1]);
        Assert.Equal(new[] { "alpha" }, (await s.ReadProjectUsageEndpointsAsync("p1", "2026-10", 1, Ct)).Select(r => Json.Str(r, "endpoint_slug")));

        Assert.Empty(await s.ReadProjectUsageDailyAsync("p1", "2026-09", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.IncrementProjectUsageAsync("p1", "", "2026-10-01", null, Delta(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.IncrementProjectUsageAsync("p1", "2026-10", "", null, Delta(), Ct));
    }

    [SkippableFact]
    public async Task Zero_delta_creates_a_zero_row_like_a_counter_update()
    {
        var s = await StoreAsync();
        await s.IncrementProjectUsageAsync("p1", "2026-11", "2026-11-01", "ep", Delta(), Ct);
        Json.Equal("""{"requests":0,"reads":0,"writes":0,"endpoint_calls":0,"bytes_in":0,"bytes_out":0,"errors":0,"duration_ms_sum":0,"duration_samples":0,"compute_units":0,"storage_delta_bytes":0}""",
            await s.ReadProjectUsageMonthlyAsync("p1", "2026-11", Ct));
    }

    [SkippableFact]
    public async Task Storage_bytes_sum_all_months_and_never_go_negative()
    {
        var s = await StoreAsync();
        Assert.Equal(0, await s.ReadProjectStorageBytesAsync("p1", Ct));
        await s.IncrementProjectUsageAsync("p1", "2026-09", "2026-09-30", null, Delta(storage: 1000), Ct);
        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-01", null, Delta(storage: -300), Ct);
        await s.IncrementProjectUsageAsync("p2", "2026-10", "2026-10-01", null, Delta(storage: 5), Ct);
        Assert.Equal(700, await s.ReadProjectStorageBytesAsync("p1", Ct));

        await s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-02", null, Delta(storage: -5000), Ct);
        Assert.Equal(0, await s.ReadProjectStorageBytesAsync("p1", Ct));
        Assert.Equal(-5300, Json.Long((await s.ReadProjectUsageMonthlyAsync("p1", "2026-10", Ct))!.Value, "storage_delta_bytes"));
    }

    [SkippableFact]
    public async Task Usage_counters_are_atomic_under_concurrency()
    {
        var s = await StoreAsync();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
            s.IncrementProjectUsageAsync("p1", "2026-10", "2026-10-08", "ep", Delta(requests: 1, endpointCalls: 1, storage: 2), Ct))));
        var monthly = (await s.ReadProjectUsageMonthlyAsync("p1", "2026-10", Ct))!.Value;
        Assert.Equal(40, Json.Long(monthly, "requests"));
        Assert.Equal(80, await s.ReadProjectStorageBytesAsync("p1", Ct));
        Assert.Equal(40, Json.Long((await s.ReadProjectUsageEndpointsAsync("p1", "2026-10", 5, Ct))[0], "calls"));
    }

    // ── workspace objects ───────────────────────────────────────────

    [SkippableFact]
    public async Task Workspace_objects_overwrite_read_missing_and_delete_noop()
    {
        var s = await StoreAsync();
        Assert.Null(await s.ReadWorkspaceObjectAsync("projects/p1/config.json", Ct));
        await s.PutWorkspaceObjectAsync("/projects/p1/config.json", "{\"a\":1}", Ct);
        Assert.Equal("{\"a\":1}", await s.ReadWorkspaceObjectAsync("projects\\p1\\config.json/", Ct));

        Time.Advance(50);
        await s.PutWorkspaceObjectAsync("projects/p1/config.json", "é", Ct);
        Assert.Equal("é", await s.ReadWorkspaceObjectAsync("projects/p1/config.json", Ct));
        var entry = Assert.Single(await s.ListWorkspaceObjectsAsync("projects/p1", Ct));
        Assert.Equal(new WorkspaceObjectEntry("config.json", false, 2, DateTimeOffset.FromUnixTimeMilliseconds(Start + 50)), entry);

        await s.DeleteWorkspaceObjectAsync("projects/p1/config.json", Ct);
        Assert.Null(await s.ReadWorkspaceObjectAsync("projects/p1/config.json", Ct));
        await s.DeleteWorkspaceObjectAsync("projects/p1/config.json", Ct);
        await s.DeleteWorkspaceObjectAsync("never/existed", Ct);
        Assert.Empty(await s.ListWorkspaceObjectsAsync("projects/p1", Ct));
        Assert.Equal("", await PutAndRead(s, "empty.txt", ""));
    }

    private static async Task<string?> PutAndRead(INetworkStorageStore s, string path, string content)
    {
        await s.PutWorkspaceObjectAsync(path, content, Ct);
        return await s.ReadWorkspaceObjectAsync(path, Ct);
    }

    [SkippableFact]
    public async Task Workspace_object_listing_returns_immediate_children_in_name_order()
    {
        var s = await StoreAsync();
        await s.PutWorkspaceObjectAsync("ws/b.json", "bb", Ct);
        Time.Advance(10);
        await s.PutWorkspaceObjectAsync("ws/a/deep/x.json", "x", Ct);
        Time.Advance(10);
        await s.PutWorkspaceObjectAsync("ws/a/y.json", "yyy", Ct);
        await s.PutWorkspaceObjectAsync("ws/C.json", "c", Ct);
        await s.PutWorkspaceObjectAsync("ws/a.json", "a", Ct);
        await s.PutWorkspaceObjectAsync("wsx/z.json", "z", Ct);
        await s.PutWorkspaceObjectAsync("root.txt", "r", Ct);

        var children = await s.ListWorkspaceObjectsAsync("ws/", Ct);
        Assert.Equal(new[]
        {
            new WorkspaceObjectEntry("C.json", false, 1, DateTimeOffset.FromUnixTimeMilliseconds(Start + 20)),
            new WorkspaceObjectEntry("a", true, null, DateTimeOffset.FromUnixTimeMilliseconds(Start + 20)),
            new WorkspaceObjectEntry("a.json", false, 1, DateTimeOffset.FromUnixTimeMilliseconds(Start + 20)),
            new WorkspaceObjectEntry("b.json", false, 2, DateTimeOffset.FromUnixTimeMilliseconds(Start)),
        }, children);

        Assert.Equal(new[] { "deep", "y.json" }, (await s.ListWorkspaceObjectsAsync("ws/a", Ct)).Select(e => e.Name));
        Assert.Equal(new[] { "root.txt", "ws", "wsx" }, (await s.ListWorkspaceObjectsAsync("", Ct)).Select(e => e.Name));
        Assert.Equal(new[] { "root.txt", "ws", "wsx" }, (await s.ListWorkspaceObjectsAsync("/", Ct)).Select(e => e.Name));
        Assert.Empty(await s.ListWorkspaceObjectsAsync("missing", Ct));
    }

    [SkippableFact]
    public async Task Workspace_object_prefix_wildcards_do_not_over_match()
    {
        var s = await StoreAsync();
        await s.PutWorkspaceObjectAsync("a_b/one.txt", "1", Ct);
        await s.PutWorkspaceObjectAsync("axb/two.txt", "2", Ct);
        await s.PutWorkspaceObjectAsync("a%/three.txt", "3", Ct);
        await s.PutWorkspaceObjectAsync("abc/four.txt", "4", Ct);
        await s.PutWorkspaceObjectAsync("A_B/five.txt", "5", Ct);

        Assert.Equal(new[] { "one.txt" }, (await s.ListWorkspaceObjectsAsync("a_b", Ct)).Select(e => e.Name));
        Assert.Equal(new[] { "three.txt" }, (await s.ListWorkspaceObjectsAsync("a%", Ct)).Select(e => e.Name));
        Assert.Equal(new[] { "five.txt" }, (await s.ListWorkspaceObjectsAsync("A_B", Ct)).Select(e => e.Name));
        Assert.Empty(await s.ListWorkspaceObjectsAsync("a", Ct));
    }
}
