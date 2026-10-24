using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// <c>sbox-ns db verify-backup</c>: a SQLite backup is checked on a disposable copy and never opened in place; a backup
/// that cannot be restored here, or whose schema this build cannot run, fails closed. The live database is never written.
/// </summary>
public sealed class BackupVerifyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-verify-backup-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pooled SQLite handles can briefly keep the database file open on Windows.
        }
    }

    [Fact]
    public async Task Verified_sqlite_backup_is_restore_rehearsed_and_leaves_the_live_database_untouched()
    {
        var server = CreateServerFolders("live");
        Assert.Equal(CliApp.Ok, await DatabaseCommands.RunAsync(Context(server, "db", "migrate")));
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context(server, "project", "create", "Verified Game")));
        var config = Load(server);
        var backup = await DatabaseBackup.BackupAsync(config, Path.Combine(_root, "backup.db"), CancellationToken.None);
        var live = Path.GetFullPath(StoreRegistration.SqlitePath(config));
        var liveHash = Hash(live);

        var report = await BackupVerifier.VerifyAsync(config, backup, null, CancellationToken.None);

        Assert.Equal("verified", report.Result);
        Assert.True(report.RestoreTested);
        Assert.Equal("sqlite-copy-rehearsal", report.VerificationLevel);
        Assert.Contains(report.Steps, s => s.Name == "integrity" && s.Status == "passed");
        Assert.Contains(report.Steps, s => s.Name == "reads" && s.Status == "passed" && s.Detail.Contains("projects 1", StringComparison.Ordinal));
        Assert.Contains(report.Steps, s => s.Name == "cleanup" && s.Status == "passed");
        Assert.Contains(report.Steps, s => s.Name == "source-unchanged" && s.Status == "passed");
        Assert.Equal(liveHash, Hash(live));
    }

    [Fact]
    public async Task Truncated_sqlite_backup_fails_and_is_not_restore_tested()
    {
        var config = await CreateBackupAsync("truncated");
        var backup = Path.Combine(_root, "truncated.db");
        var bytes = File.ReadAllBytes(Path.Combine(_root, "backup-truncated.db"));
        File.WriteAllBytes(backup, bytes[..(bytes.Length / 2)]);

        var report = await BackupVerifier.VerifyAsync(config, backup, null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.False(report.RestoreTested);
    }

    [Fact]
    public async Task Non_database_file_fails_the_format_check()
    {
        var config = CreateConfigOnly();
        var backup = Path.Combine(_root, "notes.txt");
        File.WriteAllText(backup, "this is not a database backup");

        var report = await BackupVerifier.VerifyAsync(config, backup, null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.Equal("unknown", report.Provider);
        Assert.Contains(report.Steps, s => s.Name == "format" && s.Status == "failed");
    }

    [Fact]
    public async Task Postgres_archive_under_a_sqlite_server_fails_the_provider_check()
    {
        var config = CreateConfigOnly();
        var backup = Path.Combine(_root, "archive.dump");
        File.WriteAllBytes(backup, [.. "PGDMP"u8.ToArray(), 0x01, 0x0E, 0x00, 0x00]);

        var report = await BackupVerifier.VerifyAsync(config, backup, null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.Equal("postgres", report.Provider);
        Assert.Contains(report.Steps, s => s.Name == "provider" && s.Status == "failed");
    }

    [Fact]
    public async Task Backup_with_a_schema_this_build_cannot_run_fails_the_schema_check()
    {
        var config = await CreateBackupAsync("future");
        var backup = Path.Combine(_root, "backup-future.db");
        await using (var connection = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_version (version, applied_at_unix_ms, description, compat, min_binary_schema) VALUES (999, 0, 'future', 'breaking', 999)";
            await insert.ExecuteNonQueryAsync();
        }

        var report = await BackupVerifier.VerifyAsync(config, backup, null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.False(report.RestoreTested);
        Assert.Contains(report.Steps, s => s.Name == "schema" && s.Status == "failed" && s.Detail.Contains("needs a binary of at least 999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scratch_database_is_refused_for_sqlite_backups()
    {
        var config = await CreateBackupAsync("scratch");
        var backup = Path.Combine(_root, "backup-scratch.db");

        var report = await BackupVerifier.VerifyAsync(config, backup, "scratch_rehearsal", CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.Contains(report.Steps, s => s.Name == "arguments" && s.Status == "failed");
    }

    [Fact]
    public async Task Live_database_file_is_never_verified_in_place()
    {
        var server = CreateServerFolders("in-place");
        Assert.Equal(CliApp.Ok, await DatabaseCommands.RunAsync(Context(server, "db", "migrate")));
        var config = Load(server);
        var live = Path.GetFullPath(StoreRegistration.SqlitePath(config));
        var liveHash = Hash(live);

        var report = await BackupVerifier.VerifyAsync(config, live, null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.Contains(report.Steps, s => s.Name == "live-isolation" && s.Status == "failed");
        Assert.Equal(liveHash, Hash(live));
    }

    [Fact]
    public async Task Missing_backup_file_fails_without_touching_anything()
    {
        var config = CreateConfigOnly();

        var report = await BackupVerifier.VerifyAsync(config, Path.Combine(_root, "absent.db"), null, CancellationToken.None);

        Assert.Equal("failed", report.Result);
        Assert.Contains(report.Steps, s => s.Name == "file" && s.Status == "failed");
    }

    [Theory]
    [InlineData("sbox", "sbox")]
    [InlineData("sbox", "SBOX")]
    [InlineData("sbox", "postgres")]
    [InlineData("sbox", "template0")]
    [InlineData("sbox", "template1")]
    [InlineData("sbox", "")]
    [InlineData("sbox", "   ")]
    [InlineData("sbox", "scratch\nname")]
    [InlineData("sbox", "dbname=sbox")]
    [InlineData("sbox", "postgresql://localhost/sbox")]
    [InlineData("sbox", "scratch name")]
    public void Scratch_name_that_is_not_a_separate_database_is_refused(string live, string scratch)
    {
        Assert.Throws<InvalidOperationException>(() => PostgresBackupRehearsal.ProveScratchIsolated(live, scratch));
    }

    [Fact]
    public void Redaction_removes_every_secret_and_ignores_empty_values()
    {
        Assert.Equal("password=*** host=localhost", VerificationRun.Redact("password=hunter2 host=localhost", ["hunter2", ""]));
    }

    private async Task<EffectiveConfig> CreateBackupAsync(string name)
    {
        var server = CreateServerFolders(name);
        Assert.Equal(CliApp.Ok, await DatabaseCommands.RunAsync(Context(server, "db", "migrate")));
        var config = Load(server);
        await DatabaseBackup.BackupAsync(config, Path.Combine(_root, $"backup-{name}.db"), CancellationToken.None);
        return config;
    }

    private EffectiveConfig CreateConfigOnly() => Load(CreateServerFolders("config-only"));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private (string Config, string Data) CreateServerFolders(string name)
    {
        var config = Path.Combine(_root, name, "config");
        ConfigFiles.WriteAll(config, new Dictionary<string, object> { ["updates.check"] = false });
        return (config, Path.Combine(_root, name, "data"));
    }

    private static CliContext Context((string Config, string Data) server, params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", server.Config, "--data-dir", server.Data]));

    private static EffectiveConfig Load((string Config, string Data) server)
        => ConfigLoader.Load(server.Config, server.Data, null, _ => null);
}
