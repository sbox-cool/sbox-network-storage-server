using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class SchemaMismatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-schema-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");
    private string DataDir => Path.Combine(_root, "data");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Task<int> RunAsync(params string[] args)
        => CliApp.RunAsync([.. args, "--config-dir", ConfigDir, "--data-dir", DataDir]);

    [Fact]
    public async Task A_database_written_by_a_newer_release_is_a_clean_failure_not_a_crash()
    {
        Assert.Equal(CliApp.Ok, await RunAsync("setup", "--non-interactive", "--database", "sqlite"));
        Assert.Equal(CliApp.Ok, await RunAsync("project", "create", "Before"));

        using (var connection = new SqliteConnection($"Data Source={StoreRegistration.SqlitePath(ConfigLoader.Load(ConfigDir, DataDir, null, _ => null))}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_version (version, applied_at_unix_ms, description) VALUES (999, 0, 'from the future')";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Without the catch this throws SchemaVersionTooNewException out of RunAsync.
        Assert.Equal(CliApp.Failure, await RunAsync("project", "list"));
    }
}
