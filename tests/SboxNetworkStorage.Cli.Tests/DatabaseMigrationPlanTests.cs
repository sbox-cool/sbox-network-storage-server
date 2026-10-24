using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class DatabaseMigrationPlanTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ns-plan-").FullName;
    private string ConfigDir => Path.Combine(root, "config");
    private string DataDir => Path.Combine(root, "data");
    private EffectiveConfig Config => ConfigLoader.Load(ConfigDir, DataDir, null, _ => null);

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task UninitializedPlanningDoesNotCreateDatabaseDataDirectoryOrSecrets()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Assert.Equal(CliApp.Ok, await CliApp.RunAsync(["db", "migrate", "--plan", "--json", "--config-dir", ConfigDir, "--data-dir", DataDir]));
        Assert.False(Directory.Exists(DataDir));
        Assert.False(Directory.Exists(Path.Combine(ConfigDir, "secrets")));
    }

    [Fact]
    public async Task ReadingLegacySchemaDoesNotBackfillFloorOrApplyPendingVersions()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Directory.CreateDirectory(DataDir);
        var path = StoreRegistration.SqlitePath(Config);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_version(version INTEGER PRIMARY KEY, applied_at_unix_ms INTEGER, description TEXT); INSERT INTO schema_version VALUES(1,0,'legacy');";
            await command.ExecuteNonQueryAsync();
        }
        var before = await File.ReadAllBytesAsync(path);
        var plan = await DatabaseMigrationPlan.ReadAsync(Config, default);
        Assert.Equal(1, plan.State.Version);
        Assert.True(plan.Initialized);
        Assert.All(plan.Pending, migration => Assert.True(migration.Version > 1));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(Path.Combine(ConfigDir, "secrets")));
    }

    [Fact]
    public void PlanningPreservesExistingFloorAndTreatsUnknownClassificationConservatively()
    {
        SchemaMigration[] migrations =
        [
            new(1, "base", SchemaChange.Breaking, _ => ""),
            new(2, "add", SchemaChange.Additive, _ => ""),
            new(3, "unknown classification", (SchemaChange)99, _ => "")
        ];
        var plan = DatabaseMigrationPlan.Create("sqlite", true, new SchemaState(1, 1), migrations);
        Assert.Equal(new[] { 2, 3 }, plan.Pending.Select(m => m.Version));
        Assert.Equal(3, plan.ProjectedFloor);
        Assert.Equal("breaking", plan.ToJson()["pending"]![1]!["compat"]!.GetValue<string>());
        var additive = DatabaseMigrationPlan.Create("sqlite", true, new SchemaState(1, 1), migrations[..2]);
        Assert.Equal(1, additive.ProjectedFloor);
        var newer = DatabaseMigrationPlan.Create("sqlite", true, new SchemaState(4, 4), migrations);
        Assert.False(newer.Compatible);
        Assert.Empty(newer.Pending);
        Assert.Equal(4, newer.ProjectedFloor);
        var compatible = DatabaseMigrationPlan.Create("sqlite", true, new SchemaState(4, 1), migrations);
        Assert.True(compatible.Compatible);
        Assert.Empty(compatible.Pending);
    }
}
