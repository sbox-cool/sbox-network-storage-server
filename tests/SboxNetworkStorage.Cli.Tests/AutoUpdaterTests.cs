using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// The multi-instance install of <c>update --auto</c> with fake service control and HTTP:
/// real files, real SQLite backups/restores, no systemd.
/// </summary>
public sealed class AutoUpdaterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-auto-update-").FullName;
    private readonly string _configRoot;
    private readonly string _dataRoot;
    private readonly string _binary;
    private readonly string _staged;

    public AutoUpdaterTests()
    {
        _configRoot = Path.Combine(_root, "etc");
        _dataRoot = Path.Combine(_root, "var");
        _binary = Path.Combine(_root, "bin", "sbox-ns");
        _staged = Path.Combine(_root, "staged", "sbox-ns");
        Directory.CreateDirectory(Path.GetDirectoryName(_binary)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_staged)!);
        // The fake binaries hold their version, which the fake /health reports.
        File.WriteAllText(_binary, "1.0.0");
        File.WriteAllText(_staged, "1.1.0");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Enumeration_finds_the_default_instance_and_valid_named_instances()
    {
        ConfigFiles.WriteAll(_configRoot, new Dictionary<string, object> { ["server.listen"] = "0.0.0.0:8080" });
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "beta", 8102, autoUpdate: false);
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "alpha", 8101, autoUpdate: true);
        Directory.CreateDirectory(Path.Combine(_configRoot, "secrets"));
        ConfigFiles.WriteAll(Path.Combine(_configRoot, "Not_Valid"), new Dictionary<string, object>());

        var instances = ServerInstances.Enumerate(_configRoot, _dataRoot);

        Assert.Equal(["default", "alpha", "beta"], instances.Select(i => i.Name));
        Assert.Equal(["sbox-ns", "sbox-ns@alpha", "sbox-ns@beta"], instances.Select(i => i.Unit));
        Assert.Equal(Path.Combine(_dataRoot, "alpha"), instances[1].Config.DataDirectory);
        Assert.Equal(Path.Combine(_configRoot, "alpha"), instances[1].Config.ConfigDirectory);
        Assert.Equal("http://127.0.0.1:8080/health", instances[0].HealthUrl);
        Assert.Equal("http://127.0.0.1:8101/health", instances[1].HealthUrl);
        Assert.True(instances[1].Config.GetBoolean("updates.auto_install"));
        Assert.False(instances[2].Config.GetBoolean("updates.auto_install"));
        Assert.True(Directory.Exists(Path.Combine(_dataRoot, "beta")));

        // Re-running with another port keeps the folder and moves the listener.
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "beta", 8200, autoUpdate: false);
        Assert.Equal("127.0.0.1:8200", ServerInstances.Enumerate(_configRoot, _dataRoot)[2].Config.GetString("server.listen"));
    }

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("a-1", true)]
    [InlineData("default", false)]
    [InlineData("conf.d", false)]
    [InlineData("Upper", false)]
    [InlineData("-lead", false)]
    [InlineData("", false)]
    public void Instance_names_are_safe_path_and_unit_segments(string name, bool valid)
        => Assert.Equal(valid, ServerInstances.IsValidName(name));

    [Fact]
    public async Task Success_updates_every_instance_and_records_success()
    {
        var instances = CreateInstances();
        var host = new FakeHost(this);
        var updater = Updater(host, broken: null);

        var result = await updater.InstallAsync("1.0.0", "1.1.0", _staged, _binary, instances, "auto", CancellationToken.None);

        Assert.Equal(InstallOutcome.Succeeded, result.Outcome);
        Assert.Equal("1.1.0", File.ReadAllText(_binary));
        Assert.Equal("1.0.0", File.ReadAllText(_binary + ".previous"));
        Assert.Equal(["stop sbox-ns@alpha", "stop sbox-ns@beta", "migrate alpha", "migrate beta", "start sbox-ns@alpha", "start sbox-ns@beta"], host.Calls);
        foreach (var instance in instances)
        {
            Assert.Equal(["before", "migrated"], Rows(instance));
            var record = UpdateRecord.Read(instance.Config)!;
            Assert.Equal(UpdateRecord.Succeeded, record.Status);
            Assert.Equal(("1.0.0", "1.1.0", "auto"), (record.FromVersion, record.ToVersion, record.Mode));
            Assert.True(File.Exists(record.BackupPath));
        }

        // rollback --all-instances undoes it for every instance.
        host.Calls.Clear();
        var records = instances.Select(i => (i, UpdateRecord.Read(i.Config)!)).ToList();
        Assert.Empty(await updater.RollbackRecordedAsync(records, CancellationToken.None));
        Assert.Equal("1.0.0", File.ReadAllText(_binary));
        Assert.All(instances, instance => Assert.Equal(["before"], Rows(instance)));
        Assert.All(instances, instance => Assert.Null(UpdateRecord.Read(instance.Config)));
    }

    [Fact]
    public async Task Health_check_failure_rolls_back_the_binary_and_every_database()
    {
        var instances = CreateInstances();
        var host = new FakeHost(this);
        // beta never reports the new version, but serves the old one again after the rollback.
        var updater = Updater(host, broken: "beta");

        var result = await updater.InstallAsync("1.0.0", "1.1.0", _staged, _binary, instances, "auto", CancellationToken.None);

        Assert.Equal(InstallOutcome.RolledBack, result.Outcome);
        Assert.Contains("beta did not report healthy 1.1.0", result.Reason);
        Assert.Equal("1.0.0", File.ReadAllText(_binary));
        Assert.Equal(
            ["stop sbox-ns@alpha", "stop sbox-ns@beta", "migrate alpha", "migrate beta", "start sbox-ns@alpha", "start sbox-ns@beta",
             "stop sbox-ns@alpha", "stop sbox-ns@beta", "start sbox-ns@alpha", "start sbox-ns@beta"],
            host.Calls);
        foreach (var instance in instances)
        {
            Assert.Equal(["before"], Rows(instance));
            var record = UpdateRecord.Read(instance.Config)!;
            Assert.True(record.IsFailed);
            Assert.Equal("1.1.0", record.ToVersion);
            Assert.Contains("beta did not report healthy", record.Reason);
        }

        var rollback = await Assert.ThrowsAsync<CliException>(() => Task.Run(() => UpdateCommands.RequireRollbackable(UpdateRecord.Read(instances[0].Config)!)));
        Assert.Contains("automatic recovery was attempted", rollback.Message);
        var next = AutoUpdatePolicy.EvaluateRelease(new AutoUpdateSettings(true, "stable", new UpdateWindow(TimeSpan.Zero, TimeSpan.FromHours(24)), 0),
            "1.0.0", new ReleaseInfo("1.1.0", null, false, false, null, null), UpdateRecord.Read(instances[0].Config)!.ToVersion, DateTimeOffset.UtcNow);
        Assert.False(next.Install);
    }

    [Fact]
    public async Task Migration_failure_restores_databases_and_reports_an_incomplete_rollback()
    {
        var instances = CreateInstances();
        var host = new FakeHost(this) { FailMigrationOf = "beta" };
        var updater = Updater(host, broken: "alpha-after-rollback");

        var result = await updater.InstallAsync("1.0.0", "1.1.0", _staged, _binary, instances, "auto", CancellationToken.None);

        Assert.Equal(InstallOutcome.RollbackFailed, result.Outcome);
        Assert.Contains("migrate beta exited with code 1", result.Reason);
        Assert.Contains("rollback incomplete", result.Reason);
        Assert.Equal("1.0.0", File.ReadAllText(_binary));
        Assert.All(instances, instance => Assert.Equal(["before"], Rows(instance)));
        Assert.All(instances, instance => Assert.True(UpdateRecord.Read(instance.Config)!.IsFailed));
    }

    [Fact]
    public async Task Rollback_stop_failure_never_restores_a_database_under_a_running_service()
    {
        var instances = CreateInstances();
        var host = new FakeHost(this) { FailStopAfterMigrationOf = "sbox-ns@beta" };
        var result = await Updater(host, broken: "beta").InstallAsync("1.0.0", "1.1.0", _staged, _binary, instances, "auto", CancellationToken.None);

        Assert.Equal(InstallOutcome.RollbackFailed, result.Outcome);
        Assert.Contains("stop sbox-ns@beta", result.Reason);
        Assert.Equal("1.1.0", File.ReadAllText(_binary));
        Assert.All(instances, instance => Assert.Equal(["before", "migrated"], Rows(instance)));
        Assert.All(instances, instance => Assert.True(File.Exists(UpdateRecord.Read(instance.Config)!.BackupPath)));
    }

    private AutoUpdater Updater(FakeHost host, string? broken)
        => new(host, new HttpClient(new FakeHealth(this, broken)), _ => { })
        {
            HealthTimeout = TimeSpan.FromMilliseconds(300),
            HealthPollInterval = TimeSpan.FromMilliseconds(20),
        };

    private IReadOnlyList<ServerInstance> CreateInstances()
    {
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "alpha", 8101, autoUpdate: true);
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "beta", 8102, autoUpdate: true);
        var instances = ServerInstances.Enumerate(_configRoot, _dataRoot);
        foreach (var instance in instances)
        {
            Execute(instance, "CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker VALUES ('before');");
        }

        return instances;
    }

    private static void Execute(ServerInstance instance, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={StoreRegistration.SqlitePath(instance.Config)}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Rows(ServerInstance instance)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={StoreRegistration.SqlitePath(instance.Config)}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM marker ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private sealed class FakeHost(AutoUpdaterTests test) : IUpdateHost
    {
        public List<string> Calls { get; } = [];

        public string? FailMigrationOf { get; init; }

        public string? FailStopAfterMigrationOf { get; init; }

        private bool _migrationAttempted;

        public Task<bool> IsActiveAsync(string unit, CancellationToken ct) => Task.FromResult(true);

        public Task<int> StartAsync(string unit, CancellationToken ct)
        {
            Calls.Add($"start {unit}");
            return Task.FromResult(0);
        }

        public Task<int> StopAsync(string unit, CancellationToken ct)
        {
            Calls.Add($"stop {unit}");
            return Task.FromResult(_migrationAttempted && unit == FailStopAfterMigrationOf ? 1 : 0);
        }

        public Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct)
        {
            _migrationAttempted = true;
            Calls.Add($"migrate {instance.Name}");
            Assert.Equal(test._binary, binary);
            if (instance.Name == FailMigrationOf)
            {
                return Task.FromResult(1);
            }

            Execute(instance, "INSERT INTO marker VALUES ('migrated');");
            return Task.FromResult(0);
        }
    }

    /// <summary>/health reports the version written in the fake binary; the broken instance never reports 1.1.0.</summary>
    private sealed class FakeHealth(AutoUpdaterTests test, string? broken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = File.ReadAllText(test._binary);
            var instance = request.RequestUri!.Port == 8101 ? "alpha" : "beta";
            var unhealthy = (broken == instance && version != "1.0.0") || (broken == $"{instance}-after-rollback" && version == "1.0.0");
            return Task.FromResult(unhealthy
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"status":"database-unavailable"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"status":"ok","version":"{{version}}"}""", Encoding.UTF8, "application/json") });
        }
    }
}
