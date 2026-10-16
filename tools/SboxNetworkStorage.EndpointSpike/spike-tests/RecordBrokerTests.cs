using System.Text;
using SboxNetworkStorage.EndpointSpike.Broker;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Runtime;
using SboxNetworkStorage.EndpointSpike.Stores;

namespace SboxNetworkStorage.EndpointSpike.Tests;

public sealed class RecordBrokerTests
{
    private const string Players = "players";
    private const string Scores = "scores";

    private static Task<OpenedStore> OpenAsync(int maxPayloadBytes = 8 * 1024 * 1024) =>
        StoreFactory.OpenSqliteAsync(SpikeFixtures.TempDb(), maxPayloadBytes, CancellationToken.None);

    /// <summary>Creates a project with both test collections declared, mirroring the harness seeding.</summary>
    private static async Task<string> SeedProjectAsync(OpenedStore opened)
    {
        var project = $"proj-{Guid.NewGuid():N}";
        await opened.Store.UpsertProjectAsync(project, PlanReader.ToJsonElement(new Dictionary<string, object?>()), 1, CancellationToken.None);
        foreach (var name in new[] { Players, Scores })
        {
            await opened.Store.UpsertCollectionAsync(project, name, name, "private",
                PlanReader.ToJsonElement(new Dictionary<string, object?>()), 1, CancellationToken.None);
        }
        return project;
    }

    [Fact]
    public async Task Reading_an_undeclared_collection_is_refused()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var broker = new RecordBroker(opened.Store, project, [Players]);

        var error = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            broker.ReadAsync(Scores, "k", CancellationToken.None));

        Assert.Equal("COLLECTION_NOT_DECLARED", error.Code);
    }

    [Fact]
    public async Task Writing_to_an_undeclared_collection_is_refused_at_accept()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var broker = new RecordBroker(opened.Store, project, [Players]);
        var write = new PendingWrite(Scores, "k", new Dictionary<string, object?> { ["v"] = 1.0 });

        var error = Assert.Throws<BudgetExceededException>(() => broker.Accept([write]));

        Assert.Equal("COLLECTION_NOT_DECLARED", error.Code);
        Assert.Empty(broker.PendingWrites);
    }

    [Fact]
    public async Task Empty_record_key_is_refused_at_accept()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var broker = new RecordBroker(opened.Store, project, [Players]);

        var error = Assert.Throws<BudgetExceededException>(() =>
            broker.Accept([new PendingWrite(Players, "", new Dictionary<string, object?>())]));

        Assert.Equal("WRITE_KEY_EMPTY", error.Code);
    }

    [Fact]
    public async Task Payload_exactly_at_the_store_limit_is_accepted_and_one_byte_more_is_refused()
    {
        const int limit = 1024;
        var opened = await OpenAsync(maxPayloadBytes: limit);
        var project = await SeedProjectAsync(opened);
        var broker = new RecordBroker(opened.Store, project, [Players]);
        var overhead = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(new Dictionary<string, object?> { ["p"] = "" }));

        var atLimit = new Dictionary<string, object?> { ["p"] = new string('x', limit - overhead) };
        var overLimit = new Dictionary<string, object?> { ["p"] = new string('x', limit - overhead + 1) };

        broker.Accept([new PendingWrite(Players, "a", atLimit)]);
        var error = Assert.Throws<BudgetExceededException>(() =>
            broker.Accept([new PendingWrite(Players, "b", overLimit)]));

        Assert.Equal("PAYLOAD_TOO_LARGE", error.Code);
        Assert.Single(broker.PendingWrites);
    }

    [Fact]
    public async Task Materialized_bytes_past_the_budget_stop_the_run_on_the_read_that_crosses_it()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var big = new Dictionary<string, object?> { ["p"] = new string('a', 2_500_000) };
        await opened.Store.UpsertRecordAsync(project, Players, "big",
            PlanReader.ToJsonElement(big), deleted: false, version: 1, CancellationToken.None);
        var broker = new RecordBroker(opened.Store, project, [Players]);

        await broker.ReadAsync(Players, "big", CancellationToken.None);
        var error = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            broker.ReadAsync(Players, "big", CancellationToken.None));

        Assert.Equal(RuntimeLimits.CodeMaterialized, error.Code);
    }

    [Fact]
    public async Task Soft_deleted_and_missing_records_read_as_null()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        await opened.Store.UpsertRecordAsync(project, Players, "gone",
            PlanReader.ToJsonElement(new Dictionary<string, object?> { ["v"] = 1.0 }),
            deleted: true, version: 1, CancellationToken.None);
        var broker = new RecordBroker(opened.Store, project, [Players]);

        Assert.Null(await broker.ReadAsync(Players, "gone", CancellationToken.None));
        Assert.Null(await broker.ReadAsync(Players, "never-written", CancellationToken.None));
    }

    [Fact]
    public async Task Accepted_writes_are_not_persisted_until_commit_and_then_all_land()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var broker = new RecordBroker(opened.Store, project, [Players, Scores]);
        broker.Accept(
        [
            new PendingWrite(Players, "p1", new Dictionary<string, object?> { ["v"] = 1.0 }),
            new PendingWrite(Scores, "s1", new Dictionary<string, object?> { ["v"] = 2.0 }),
        ]);

        Assert.Null(await opened.Store.ReadRecordAsync(project, Players, "p1", CancellationToken.None));

        await broker.CommitAsync(CancellationToken.None);

        Assert.NotNull(await opened.Store.ReadRecordAsync(project, Players, "p1", CancellationToken.None));
        Assert.NotNull(await opened.Store.ReadRecordAsync(project, Scores, "s1", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_brokers_committing_distinct_records_all_persist()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        const int workers = 16;

        var commits = Enumerable.Range(0, workers).Select(async i =>
        {
            var broker = new RecordBroker(opened.Store, project, [Players]);
            broker.Accept([new PendingWrite(Players, $"w{i}", new Dictionary<string, object?> { ["i"] = (double)i })]);
            await broker.CommitAsync(CancellationToken.None);
        });
        await Task.WhenAll(commits);

        for (var i = 0; i < workers; i++)
        {
            Assert.NotNull(await opened.Store.ReadRecordAsync(project, Players, $"w{i}", CancellationToken.None));
        }
    }

    /// <summary>
    /// Concurrent commits to one record must leave exactly one intact payload from one of the runs. This asserts no
    /// torn or merged value. It does not assert which writer wins: the spike has no compare-at-commit.
    /// </summary>
    [Fact]
    public async Task Concurrent_commits_to_one_record_leave_exactly_one_intact_payload()
    {
        var opened = await OpenAsync();
        var project = await SeedProjectAsync(opened);
        var first = new RecordBroker(opened.Store, project, [Players]);
        var second = new RecordBroker(opened.Store, project, [Players]);
        first.Accept([new PendingWrite(Players, "p", new Dictionary<string, object?> { ["who"] = "first", ["n"] = 1.0 })]);
        second.Accept([new PendingWrite(Players, "p", new Dictionary<string, object?> { ["who"] = "second", ["n"] = 2.0 })]);

        await Task.WhenAll(first.CommitAsync(CancellationToken.None), second.CommitAsync(CancellationToken.None));

        var reader = new RecordBroker(opened.Store, project, [Players]);
        var stored = Assert.IsType<Dictionary<string, object?>>(
            await reader.ReadAsync(Players, "p", CancellationToken.None));
        var who = stored["who"] as string;
        var n = stored["n"] as double?;
        Assert.True((who, n) is ("first", 1.0) or ("second", 2.0), $"torn payload: who={who} n={n}");
    }
}
