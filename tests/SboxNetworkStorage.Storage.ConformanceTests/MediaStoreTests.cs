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
