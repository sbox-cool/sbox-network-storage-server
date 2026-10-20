using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class ProjectSecretStoreTests : IAsyncLifetime
{
    private RelationalNetworkStorageStore? _store;
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();
    public async Task InitializeAsync() { _store = Create(); await _store.MigrateAsync(CancellationToken.None); }
    public async Task DisposeAsync() { if (_store is not null) await _store.DisposeAsync(); await Cleanup(); }

    private IProjectSecretStore Secrets => _store!;

    private Task<ProjectSecretWriteResult> Set(string projectId, string name, string value = "sealed-value", long updatedAt = 1_000, int max = 64) =>
        Secrets.SetProjectSecretAsync(projectId, name, value, updatedAt, max, CancellationToken.None);

    [SkippableFact]
    public async Task Set_creates_then_replaces_and_lists_only_names()
    {
        Assert.Equal(ProjectSecretWriteResult.Created, await Set("proj_example", "API_KEY", "v1", 1_000));
        Assert.Equal(ProjectSecretWriteResult.Replaced, await Set("proj_example", "API_KEY", "v2", 2_000));
        Assert.Equal(new[] { new ProjectSecretSummary("API_KEY", 2_000) },
            await Secrets.ListProjectSecretsAsync("proj_example", CancellationToken.None));
        Assert.Equal("v2", await Secrets.GetProjectSecretSealedAsync("proj_example", "API_KEY", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Reads_are_scoped_to_project_and_names_are_case_sensitive()
    {
        await Set("proj_example", "Token", "sealed");
        Assert.Null(await Secrets.GetProjectSecretSealedAsync("proj_other", "Token", CancellationToken.None));
        Assert.Null(await Secrets.GetProjectSecretSealedAsync("proj_example", "token", CancellationToken.None));
        Assert.Equal("sealed", await Secrets.GetProjectSecretSealedAsync("proj_example", "Token", CancellationToken.None));
        Assert.Empty(await Secrets.ListProjectSecretsAsync("proj_other", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Delete_reports_whether_the_name_existed_and_touches_only_that_project()
    {
        await Set("proj_example", "A");
        await Set("proj_other", "A");
        Assert.True(await Secrets.DeleteProjectSecretAsync("proj_example", "A", CancellationToken.None));
        Assert.False(await Secrets.DeleteProjectSecretAsync("proj_example", "A", CancellationToken.None));
        Assert.Null(await Secrets.GetProjectSecretSealedAsync("proj_example", "A", CancellationToken.None));
        Assert.Equal("sealed-value", await Secrets.GetProjectSecretSealedAsync("proj_other", "A", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Cap_refuses_new_names_but_still_replaces_existing_ones()
    {
        Assert.Equal(ProjectSecretWriteResult.Created, await Set("proj_example", "A", max: 2));
        Assert.Equal(ProjectSecretWriteResult.Created, await Set("proj_example", "B", max: 2));
        Assert.Equal(ProjectSecretWriteResult.LimitReached, await Set("proj_example", "C", max: 2));
        Assert.Equal(ProjectSecretWriteResult.Replaced, await Set("proj_example", "A", "new", 2_000, max: 2));
        Assert.Null(await Secrets.GetProjectSecretSealedAsync("proj_example", "C", CancellationToken.None));
        Assert.Equal(2, (await Secrets.ListProjectSecretsAsync("proj_example", CancellationToken.None)).Count);
    }

    [SkippableFact]
    public async Task Parallel_new_names_cannot_exceed_the_cap()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Set("proj_example", $"S{i}", max: 3)));
        Assert.Equal(3, results.Count(r => r == ProjectSecretWriteResult.Created));
        Assert.Equal(7, results.Count(r => r == ProjectSecretWriteResult.LimitReached));
        Assert.Equal(3, (await Secrets.ListProjectSecretsAsync("proj_example", CancellationToken.None)).Count);
    }
}

public sealed class SqliteProjectSecretStoreTests : ProjectSecretStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}

public sealed class PostgresProjectSecretStoreTests : ProjectSecretStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
