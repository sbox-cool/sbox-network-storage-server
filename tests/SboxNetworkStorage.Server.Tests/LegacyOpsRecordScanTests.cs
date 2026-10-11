using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Authority;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Records that sbox-ns 0.4.0 replaced with the raw update-ops request body are found, and nothing else is.</summary>
public abstract class LegacyOpsRecordScanTests<TFactory> : IDisposable
    where TFactory : SelfHostFactory, new()
{
    private readonly TFactory factory = new();

    protected LegacyOpsRecordScanTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);

    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task Only_records_holding_nothing_but_operation_objects_are_reported_across_pages()
    {
        var project = await factory.CreateProjectAsync("Ops scan");
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.SeedCollectionAsync(project.ProjectId, "players", "players", "private", new { collectionType = "player" });
        await store.SeedCollectionAsync(project.ProjectId, "clean", "clean", "private", new { collectionType = "player" });

        var damaged = StoreSeeding.ToJson(new { ops = new object[] { new { op = "inc", path = "gold", value = 25 } } });
        // 501 damaged records forces a second page; the lookalikes below must never count.
        for (var i = 0; i < 501; i++)
            await store.UpsertRecordAsync(project.ProjectId, "players", $"7656119800000{i:D4}", damaged, false, 1, CancellationToken.None);
        foreach (var (key, payload) in new (string, object)[]
        {
            ("with-gold", new { ops = new object[] { new { op = "set", path = "a", value = 1 } }, gold = 1 }),
            ("not-operations", new { ops = new object[] { "inc" } }),
            ("ops-is-text", new { ops = "inc" }),
            ("no-ops", new { ops = Array.Empty<object>() }),
            ("normal", new { gold = 5, playerName = "x" }),
        })
            await store.UpsertRecordAsync(project.ProjectId, "clean", key, StoreSeeding.ToJson(payload), false, 1, CancellationToken.None);

        var result = await LegacyOpsRecordScan.RunAsync(store, project.ProjectId, CancellationToken.None);

        var only = Assert.Single(result);
        Assert.Equal("players", only.Collection);
        Assert.Equal(501, only.Records);
        Assert.Equal(5, only.SampleKeys.Count);
    }
}

public sealed class LegacyOpsShapeTests
{
    [Theory]
    [InlineData("""{"ops":[{"op":"inc"}]}""", true)]
    [InlineData("""{"ops":[{"op":"inc"},{"op":"set"}]}""", true)]
    [InlineData("""{"ops":[{"op":"inc"}],"gold":1}""", false)]
    [InlineData("""{"ops":[]}""", false)]
    [InlineData("""{"ops":[{"path":"gold"}]}""", false)]
    [InlineData("""[{"op":"inc"}]""", false)]
    public void Shape_check(string json, bool expected)
        => Assert.Equal(expected, LegacyOpsRecordScan.IsOpsOnly(JsonDocument.Parse(json).RootElement));
}

public sealed class SqliteLegacyOpsRecordScanTests : LegacyOpsRecordScanTests<SqliteHostFactory>
{
}

public sealed class PostgresLegacyOpsRecordScanTests : LegacyOpsRecordScanTests<PostgresHostFactory>
{
}
