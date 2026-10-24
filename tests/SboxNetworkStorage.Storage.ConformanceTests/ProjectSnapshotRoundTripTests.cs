using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class ProjectSnapshotRoundTripTests : IAsyncLifetime
{
    private RelationalNetworkStorageStore _store = null!;
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();
    public async Task InitializeAsync() { _store = Create(); await _store.MigrateAsync(CancellationToken.None); }
    public async Task DisposeAsync() { if (_store is not null) await _store.DisposeAsync(); await Cleanup(); }

    [SkippableFact]
    public async Task Replacement_restores_secrets_media_and_unused_grants_and_removes_post_snapshot_rows()
    {
        const string project = "snapshot_project";
        var ct = CancellationToken.None;
        await _store.UpsertProjectAsync(project, Json.Parse("{\"name\":\"original\"}"), 1, ct);
        await _store.SetProjectSecretAsync(project, "TOKEN", "opaque-sealed-ciphertext", 100, 10, ct);
        var media = new MediaObjectRecord(project, "avatar", "avatar", new string('a', 64), "image/png", 100, 16, 16,
            "76561198000000123", "private", 100, 1000);
        await _store.InsertMediaObjectAsync(media, ct);
        await _store.TryInsertMediaGrantAsync(new MediaGrantRecord(project, "grant", "avatar", "76561198000000123", 100, 200), 0, 10, ct);
        var rows = new List<ProjectSnapshotRow>();
        await foreach (var row in _store.ExportProjectRowsAsync(project, ct)) rows.Add(row);
        await _store.SetProjectSecretAsync(project, "TOKEN", "changed", 200, 10, ct);
        await _store.SetProjectSecretAsync(project, "STALE", "must-disappear", 200, 10, ct);
        await _store.InsertMediaObjectAsync(media with { MediaId = "stale" }, ct);

        await _store.ReplaceProjectRowsAsync(project, Snapshot(), ct);

        Assert.Equal("opaque-sealed-ciphertext", await _store.GetProjectSecretSealedAsync(project, "TOKEN", ct));
        Assert.Null(await _store.GetProjectSecretSealedAsync(project, "STALE", ct));
        Assert.Equal(media, await _store.GetMediaObjectAsync(project, "avatar", ct));
        Assert.Null(await _store.GetMediaObjectAsync(project, "stale", ct));
        Assert.True(await _store.TryConsumeGrantAsync(project, "grant", 150, ct));
        async IAsyncEnumerable<ProjectSnapshotRow> Snapshot()
        {
            foreach (var row in rows) yield return row;
            await Task.CompletedTask;
        }
    }

    [SkippableFact]
    public async Task Project_deletion_revokes_keys_and_removes_all_seeded_state_without_touching_another_project()
    {
        var ct = CancellationToken.None;
        foreach (var project in new[] { "deleted", "retained" })
        {
            await _store.UpsertProjectAsync(project, Json.Parse("""{"name":"game"}"""), 1, ct);
            await _store.UpsertApiKeyAsync(project, "public_key", "42", "public", "", "", "game", true, Json.Parse("{}"), 1, ct);
            await _store.UpsertApiKeyAsync(project, "masked_secret", "42", "secret", "hash", "identifier", "server", true, Json.Parse("{}"), 1, ct);
            await _store.UpsertProjectMembershipAsync("42", project, "owner", 100, ct);
            await _store.SetProjectSecretAsync(project, "TOKEN", "sealed-secret", 100, 10, ct);
            await _store.InsertMediaObjectAsync(new MediaObjectRecord(project, "avatar", "avatar", new string('a', 64),
                "image/png", 100, 16, 16, "76561198000000123", "private", 100, 1000), ct);
            await _store.TryInsertMediaGrantAsync(new MediaGrantRecord(project, "grant", "avatar", "76561198000000123", 100, 200), 0, 10, ct);
        }

        await _store.DeleteProjectAsync("deleted", ct);
        await _store.DeleteProjectAsync("deleted", ct);

        Assert.Null(await _store.ReadProjectAsync("deleted", ct));
        Assert.Null(await _store.ReadApiKeyAsync("deleted", "public_key", ct));
        Assert.Null(await _store.ReadApiKeyAsync("deleted", "masked_secret", ct));
        Assert.Null(await _store.GetProjectSecretSealedAsync("deleted", "TOKEN", ct));
        Assert.Null(await _store.GetMediaObjectAsync("deleted", "avatar", ct));
        Assert.False(await _store.TryConsumeGrantAsync("deleted", "grant", 150, ct));
        await foreach (var row in _store.ExportProjectRowsAsync("deleted", ct))
            Assert.Fail($"Deleted project retained a row in {row.Table}.");
        Assert.NotNull(await _store.ReadApiKeyAsync("retained", "public_key", ct));
        Assert.Equal("sealed-secret", await _store.GetProjectSecretSealedAsync("retained", "TOKEN", ct));
        Assert.NotNull(await _store.GetMediaObjectAsync("retained", "avatar", ct));
        Assert.True(await _store.TryConsumeGrantAsync("retained", "grant", 150, ct));
        Assert.Equal("retained", (await _store.ListProjectsForUserAsync("42", ct)).Single().GetProperty("project_id").GetString());
    }
}

public sealed class SqliteProjectSnapshotRoundTripTests : ProjectSnapshotRoundTripTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}

public sealed class PostgresProjectSnapshotRoundTripTests : ProjectSnapshotRoundTripTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
