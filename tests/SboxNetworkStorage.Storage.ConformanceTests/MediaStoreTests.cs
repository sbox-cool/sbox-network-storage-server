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

    private static MediaObjectRecord Record(string mediaId, string projectId = "proj_example", string owner = "76561198000000001", string purpose = "avatar", long size = 1000) =>
        new(projectId, mediaId, purpose, new string('a', 64), "image/png", size, 64, 32, owner, "private", 1_000, 2_000);

    private static MediaGrantRecord Grant(string grantId, string projectId = "proj_example", string owner = "76561198000000001",
        string purpose = "avatar", long issuedAt = 1_000, long expiresAt = 5_000) =>
        new(projectId, grantId, purpose, owner, issuedAt, expiresAt);

    [SkippableFact]
    public async Task Grant_is_consumed_once_per_project()
    {
        await Media.InsertMediaGrantAsync(Grant("grant-1"), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-2"), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-1", projectId: "proj_other"), 0, CancellationToken.None);
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 1_000, CancellationToken.None));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 1_100, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-2", nowUnixMs: 1_100, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_other", "grant-1", nowUnixMs: 1_200, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Grant_cannot_be_consumed_at_or_after_expiry_or_when_unknown()
    {
        await Media.InsertMediaGrantAsync(Grant("grant-1", expiresAt: 5_000), 0, CancellationToken.None);
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 5_000, CancellationToken.None));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-missing", nowUnixMs: 1_000, CancellationToken.None));
        Assert.True(await Media.TryConsumeGrantAsync("proj_example", "grant-1", nowUnixMs: 4_999, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Grant_count_is_scoped_to_owner_purpose_and_window()
    {
        await Media.InsertMediaGrantAsync(Grant("grant-1", issuedAt: 1_000), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-2", issuedAt: 5_000), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-3", owner: "76561198000000002", issuedAt: 5_000), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-4", purpose: "banner", issuedAt: 5_000), 0, CancellationToken.None);
        Assert.Equal(2, await Media.CountMediaGrantsSinceAsync("proj_example", "76561198000000001", "avatar", 0, CancellationToken.None));
        Assert.Equal(1, await Media.CountMediaGrantsSinceAsync("proj_example", "76561198000000001", "avatar", 2_000, CancellationToken.None));
        Assert.Equal(0, await Media.CountMediaGrantsSinceAsync("proj_other", "76561198000000001", "avatar", 0, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Grant_insert_prunes_rows_issued_before_the_cutoff()
    {
        await Media.InsertMediaGrantAsync(Grant("grant-old", issuedAt: 1_000), 0, CancellationToken.None);
        await Media.InsertMediaGrantAsync(Grant("grant-new", issuedAt: 9_000, expiresAt: 10_000), 5_000, CancellationToken.None);
        Assert.Equal(1, await Media.CountMediaGrantsSinceAsync("proj_example", "76561198000000001", "avatar", 0, CancellationToken.None));
        Assert.False(await Media.TryConsumeGrantAsync("proj_example", "grant-old", nowUnixMs: 1_100, CancellationToken.None));
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
        Assert.Equal(1000, await Media.SumMediaObjectBytesAsync("proj_example", "avatar", CancellationToken.None));
        Assert.Equal(0, await Media.SumMediaObjectBytesAsync("proj_other", "avatar", CancellationToken.None));
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
