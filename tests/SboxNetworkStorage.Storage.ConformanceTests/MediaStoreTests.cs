using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class MediaStoreTests : IAsyncLifetime
{
    private RelationalNetworkStorageStore? _store;
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();
    public async Task InitializeAsync() { _store = Create(); await _store.MigrateAsync(CancellationToken.None); }
    public async Task DisposeAsync() { if (_store is not null) await _store.DisposeAsync(); await Cleanup(); }

    private IMediaStore Media => _store!;

    private static MediaObjectRecord Record(string mediaId, string projectId = "proj_example", string owner = "76561198000000001", string purpose = "avatar", long size = 1000, long expiresAt = 2_000) =>
        new(projectId, mediaId, purpose, new string('a', 64), "image/png", size, 64, 32, owner, "private", 1_000, expiresAt);

    private static MediaGrantRecord Grant(string grantId, string projectId = "proj_example", string owner = "76561198000000001",
        string purpose = "avatar", long issuedAt = 1_000, long expiresAt = 5_000) =>
        new(projectId, grantId, purpose, owner, issuedAt, expiresAt);

    private Task<bool> Gate(MediaGrantRecord grant, long windowStart = 0, int limit = 10) =>
        Media.TryInsertMediaGrantAsync(grant, windowStart, limit, CancellationToken.None);

    [SkippableFact]
    public async Task Grant_is_consumed_once_per_project()
    {
        Assert.True(await Gate(Grant("grant-1")));
        Assert.True(await Gate(Grant("grant-2")));
        Assert.True(await Gate(Grant("grant-1", projectId: "proj_other")));
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 1_000, CancellationToken.None));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 1_100, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-2", nowUnixMs: 1_100, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_other", "grant-1", nowUnixMs: 1_200, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Grant_cannot_be_consumed_at_or_after_expiry_or_when_unknown()
    {
        Assert.True(await Gate(Grant("grant-1", expiresAt: 5_000)));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 5_000, CancellationToken.None));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-missing", nowUnixMs: 1_000, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 4_999, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Grant_gate_refuses_past_the_per_owner_limit_within_the_window()
    {
        Assert.True(await Gate(Grant("grant-1"), limit: 2));
        Assert.True(await Gate(Grant("grant-2"), limit: 2));
        Assert.False(await Gate(Grant("grant-3"), limit: 2));
        Assert.True(await Gate(Grant("grant-4", owner: "76561198000000002"), limit: 2));
        Assert.True(await Gate(Grant("grant-5", purpose: "banner"), limit: 2));
        Assert.True(await Gate(Grant("grant-6", projectId: "proj_other"), limit: 2));
    }

    [SkippableFact]
    public async Task Grant_gate_counts_only_grants_inside_the_window()
    {
        Assert.True(await Gate(Grant("grant-1", issuedAt: 1_000, expiresAt: 10_000), limit: 2));
        Assert.True(await Gate(Grant("grant-2", issuedAt: 5_000, expiresAt: 10_000), limit: 2));
        Assert.False(await Gate(Grant("grant-3", issuedAt: 5_000, expiresAt: 10_000), limit: 2));
        Assert.True(await Gate(Grant("grant-4", issuedAt: 6_000, expiresAt: 10_000), windowStart: 2_000, limit: 2));
        Assert.False(await Gate(Grant("grant-5", issuedAt: 6_000, expiresAt: 10_000), windowStart: 2_000, limit: 2));
    }

    [SkippableFact]
    public async Task Grant_gate_prunes_rows_issued_before_the_window()
    {
        Assert.True(await Gate(Grant("grant-old", issuedAt: 1_000)));
        Assert.True(await Gate(Grant("grant-new", issuedAt: 9_000, expiresAt: 10_000), windowStart: 5_000));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-old", nowUnixMs: 1_100, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Parallel_grant_requests_cannot_exceed_the_per_owner_limit()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Gate(Grant($"grant-{i}", issuedAt: 1_000 + i), limit: 5)));
        Assert.Equal(5, results.Count(granted => granted));
    }

    [SkippableFact]
    public async Task Object_round_trips_and_stays_inside_its_project()
    {
        var record = Record("med_1");
        await Media.InsertMediaObjectAsync(record, CancellationToken.None);
        Assert.Equal(record, await Media.GetMediaObjectAsync("proj_example", "med_1", CancellationToken.None));
        Assert.Null(await Media.GetMediaObjectAsync("proj_other", "med_1", CancellationToken.None));
        Assert.Null(await Media.GetMediaObjectAsync("proj_example", "med_missing", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Listing_returns_every_object_in_the_project_including_expired_ones()
    {
        await Media.UpsertMediaObjectAsync(Record("med_b", expiresAt: 1_500), CancellationToken.None);
        await Media.UpsertMediaObjectAsync(Record("med_a", size: 9), CancellationToken.None);
        await Media.UpsertMediaObjectAsync(Record("med_c", projectId: "proj_other"), CancellationToken.None);
        var listed = await Media.ListMediaObjectsAsync("proj_example", CancellationToken.None);
        Assert.Equal(new[] { "med_a", "med_b" }, listed.Select(o => o.MediaId));
    }

    [SkippableFact]
    public async Task Upsert_replaces_the_row_for_the_same_media_id()
    {
        await Media.UpsertMediaObjectAsync(Record("med_1", size: 700), CancellationToken.None);
        var replaced = Record("med_1", size: 900);
        await Media.UpsertMediaObjectAsync(replaced, CancellationToken.None);
        Assert.Equal(new[] { replaced }, await Media.ListMediaObjectsAsync("proj_example", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Quota_sums_sizes_per_purpose_within_a_project()
    {
        await Media.InsertMediaObjectAsync(Record("med_1", size: 700), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_2", size: 300), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_3", purpose: "banner", size: 5_000), CancellationToken.None);
        Assert.Equal(1000, await Media.SumMediaObjectBytesAsync("proj_example", "avatar", 1_000, CancellationToken.None));
        Assert.Equal(0, await Media.SumMediaObjectBytesAsync("proj_other", "avatar", 1_000, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Quota_ignores_expired_objects()
    {
        await Media.InsertMediaObjectAsync(Record("med_live", size: 700, expiresAt: 5_000), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_expired", size: 4_000, expiresAt: 1_500), CancellationToken.None);
        Assert.Equal(4_700, await Media.SumMediaObjectBytesAsync("proj_example", "avatar", 1_000, CancellationToken.None));
        Assert.Equal(700, await Media.SumMediaObjectBytesAsync("proj_example", "avatar", 1_500, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Expired_listing_pages_by_project_and_media_id_and_skips_live_rows()
    {
        await Media.InsertMediaObjectAsync(Record("med_c", expiresAt: 1_500), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_a", expiresAt: 1_500), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_b", expiresAt: 9_000), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_d", expiresAt: 1_500), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_e", expiresAt: 2_000), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_o", projectId: "proj_other", expiresAt: 1_500), CancellationToken.None);

        var first = await Media.ListExpiredMediaObjectsAsync(2_000, null, 2, CancellationToken.None);
        Assert.Equal(new[] { "med_a", "med_c" }, first.Select(o => o.MediaId));
        var second = await Media.ListExpiredMediaObjectsAsync(2_000, (first[^1].ProjectId, first[^1].MediaId), 2, CancellationToken.None);
        Assert.Equal(new[] { "med_d", "med_e" }, second.Select(o => o.MediaId));
        var third = await Media.ListExpiredMediaObjectsAsync(2_000, (second[^1].ProjectId, second[^1].MediaId), 2, CancellationToken.None);
        Assert.Equal(new[] { "med_o" }, third.Select(o => o.MediaId));
        Assert.Empty(await Media.ListExpiredMediaObjectsAsync(2_000, (third[^1].ProjectId, third[^1].MediaId), 2, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Sha_lookup_lists_every_row_for_the_bytes_within_the_project()
    {
        var shared = new string('b', 64);
        await Media.InsertMediaObjectAsync(Record("med_1") with { Sha256 = shared }, CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_2", expiresAt: 1_500) with { Sha256 = shared }, CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_3", projectId: "proj_other") with { Sha256 = shared }, CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_4") with { Sha256 = new string('c', 64) }, CancellationToken.None);

        var references = await Media.ListMediaReferencesBySha256Async("proj_example", shared, CancellationToken.None);
        Assert.Equal(new[] { ("med_1", 2_000L), ("med_2", 1_500L) },
            references.OrderBy(r => r.MediaId, StringComparer.Ordinal).Select(r => (r.MediaId, r.ExpiresAtUnixMs)));
    }

    [SkippableFact]
    public async Task Delete_expired_removes_only_rows_that_are_expired_at_the_given_time()
    {
        await Media.InsertMediaObjectAsync(Record("med_live", expiresAt: 9_000), CancellationToken.None);
        await Media.InsertMediaObjectAsync(Record("med_expired", expiresAt: 1_500), CancellationToken.None);

        Assert.False(await Media.DeleteExpiredMediaObjectAsync("proj_example", "med_live", 2_000, CancellationToken.None));
        Assert.False(await Media.DeleteExpiredMediaObjectAsync("proj_other", "med_expired", 2_000, CancellationToken.None));
        Assert.False(await Media.DeleteExpiredMediaObjectAsync("proj_example", "med_expired", 1_499, CancellationToken.None));
        Assert.True(await Media.DeleteExpiredMediaObjectAsync("proj_example", "med_expired", 2_000, CancellationToken.None));
        Assert.False(await Media.DeleteExpiredMediaObjectAsync("proj_example", "med_expired", 2_000, CancellationToken.None));
        Assert.Equal(new[] { "med_live" }, (await Media.ListMediaObjectsAsync("proj_example", CancellationToken.None)).Select(o => o.MediaId));
    }
}

public sealed class SqliteMediaStoreTests : MediaStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}

public sealed class PostgresMediaStoreTests : MediaStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
