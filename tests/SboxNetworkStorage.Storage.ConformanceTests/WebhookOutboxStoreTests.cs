using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>Relational webhook outbox SQL: pending cap, cancel, expiry and stale-target pruning.</summary>
public abstract class WebhookOutboxStoreTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Project = "proj_conf";
    private const string Target = "https://discord.com/api/webhooks/1/conf";
    private const string OtherTarget = "https://discord.com/api/webhooks/2/conf";

    protected abstract RelationalNetworkStorageStore CreateStore();

    protected abstract Task CleanupAsync();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task Enqueue_refuses_rows_past_the_pending_cap_per_target()
    {
        await using var store = CreateStore();
        await store.MigrateAsync(Ct);
        IWebhookOutboxStore outbox = store;

        Assert.True(await outbox.EnqueueAsync(Row("o1", Target, 1), 2, Ct));
        Assert.True(await outbox.EnqueueAsync(Row("o2", Target, 2), 2, Ct));
        Assert.False(await outbox.EnqueueAsync(Row("o3", Target, 3), 2, Ct));
        Assert.True(await outbox.EnqueueAsync(Row("o4", OtherTarget, 4), 2, Ct));

        var claimed = await outbox.ClaimDueAsync(10, 60_010, 10, Ct);
        Assert.Equal(new[] { "o1", "o2", "o4" }, claimed.Select(r => r.OutboxId).OrderBy(id => id).ToArray());
    }

    [SkippableFact]
    public async Task Cancel_suppresses_pending_rows_for_the_project_only()
    {
        await using var store = CreateStore();
        await store.MigrateAsync(Ct);
        IWebhookOutboxStore outbox = store;
        await outbox.EnqueueAsync(Row("c1", Target, 1), 10, Ct);
        await outbox.EnqueueAsync(Row("c2", OtherTarget, 1), 10, Ct);
        await outbox.EnqueueAsync(Row("c3", Target, 1, "proj_other"), 10, Ct);

        Assert.Equal(2, await outbox.CancelPendingAsync(Project, "outbox disabled", 5, Ct));

        var remaining = Assert.Single(await outbox.ClaimDueAsync(10, 60_010, 10, Ct));
        Assert.Equal("proj_other", remaining.ProjectId);
    }

    [SkippableFact]
    public async Task Expire_fails_only_pending_rows_created_before_the_cutoff()
    {
        await using var store = CreateStore();
        await store.MigrateAsync(Ct);
        IWebhookOutboxStore outbox = store;
        await outbox.EnqueueAsync(Row("e1", Target, 1_000), 10, Ct);
        await outbox.EnqueueAsync(Row("e2", Target, 9_000), 10, Ct);

        Assert.Equal(1, await outbox.ExpirePendingAsync(5_000, 10_000, Ct));

        var remaining = Assert.Single(await outbox.ClaimDueAsync(10_000, 70_000, 10, Ct));
        Assert.Equal("e2", remaining.OutboxId);
    }

    [SkippableFact]
    public async Task Prune_removes_only_idle_enabled_targets_without_outbox_rows()
    {
        const string idle = "https://discord.com/api/webhooks/3/idle";
        const string disabled = "https://discord.com/api/webhooks/4/disabled";
        await using var store = CreateStore();
        await store.MigrateAsync(Ct);
        IWebhookOutboxStore outbox = store;
        await outbox.WriteTargetAsync(new WebhookTargetState(Project, idle, 0, null, null, null, null, null, 1), Ct);
        await outbox.WriteTargetAsync(new WebhookTargetState(Project, disabled, 5, 1, 404, "x", 1, "HTTP 404: webhook not found", 1), Ct);
        await outbox.WriteTargetAsync(new WebhookTargetState(Project, Target, 0, null, null, null, null, null, 1), Ct);
        await outbox.EnqueueAsync(Row("p1", Target, 1), 10, Ct);

        Assert.Equal(1, await outbox.PruneStaleTargetsAsync(2, Ct));
        Assert.Null(await outbox.ReadTargetAsync(Project, idle, Ct));
        Assert.NotNull(await outbox.ReadTargetAsync(Project, disabled, Ct));
        Assert.NotNull(await outbox.ReadTargetAsync(Project, Target, Ct));
    }

    private static WebhookOutboxRow Row(string id, string target, long createdAt, string project = Project)
        => new(project, id, target, "{}", WebhookOutboxStatus.Pending, 0, createdAt, null, null, null, createdAt, createdAt);
}

public sealed class SqliteWebhookOutboxStoreTests : WebhookOutboxStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();

    protected override RelationalNetworkStorageStore CreateStore() => TestDatabases.CreateSqlite(_path, null);

    protected override Task CleanupAsync()
    {
        TestDatabases.DeleteSqlite(_path);
        return Task.CompletedTask;
    }
}

public sealed class PostgresWebhookOutboxStoreTests : WebhookOutboxStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();

    protected override RelationalNetworkStorageStore CreateStore()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.CreatePostgres(_schema, null);
    }

    protected override Task CleanupAsync()
        => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
