using System.Text.Json;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// <c>layout migrate [--revert]</c> on real folders. The host (root, systemd, account lookups, chown) is faked,
/// so nothing needs privileges; modes are applied for real.
/// </summary>
public sealed class LayoutMigratorTests : IDisposable
{
    private static readonly UnixOwner Service = new(1000, 1000);

    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-migrate-").FullName;
    private readonly string _configRoot;
    private readonly string _dataRoot;
    private readonly string _binary;
    private readonly UpdateState _updateState;
    private readonly FakeSystem _system;
    private readonly FakeHost _host;
    private readonly List<string> _log = [];

    public LayoutMigratorTests()
    {
        _configRoot = Path.Combine(_root, "etc");
        _dataRoot = Path.Combine(_root, "var");
        _binary = Path.Combine(_root, "bin", "sbox-ns");
        Directory.CreateDirectory(Path.GetDirectoryName(_binary)!);
        File.WriteAllText(_binary, "1.0.0");
        _updateState = new UpdateState(Path.Combine(_root, "update-state"), FolderTrust.Host);
        _system = new FakeSystem(Path.Combine(_root, "units"));
        _host = new FakeHost();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Etc(string relative) => Path.Combine(_configRoot, relative);

    private string State(string relative) => Path.Combine(_dataRoot, "state", relative);

    private LayoutMigrator Migrator(bool manageUnits = true)
        => new(_system, _host, _updateState, _binary, _log.Add) { ManageUnits = manageUnits };

    private IReadOnlyList<ServerInstance> Instances() => ServerInstances.Enumerate(_configRoot, _dataRoot, _ => null);

    private ServerInstance Instance() => Instances().Single();

    /// <summary>An install as v0.4.0 left it: operator files plus every runtime file in the config folder.</summary>
    private void CreateLegacyInstall()
    {
        ConfigFiles.WriteAll(_configRoot, new Dictionary<string, object> { ["updates.check"] = false });
        Put(Etc("secrets/auth_session_secret"), "session-secret");
        Put(Etc("secrets/storage_encryption_key"), new string('a', 64));
        Put(Etc("secrets/security_signing_key.pem"), "pem");
        Put(Etc("secrets/identity_ecdsa_p256.pem"), "identity");
        Put(Etc("secrets/tunnel_token"), "token");
        Put(Etc("secrets/postgres_password"), "operator password");
        Put(Etc("connectors/2026.10.0/cloudflared"), "connector");
        Put(Etc(".tunnel.lock"), "");
        Put(Etc(".dns.lock"), "");
        Put(Etc("conf.d/zzzz-tunnel.toml"), "[tunnel]\nlocal_port = 9100\n");
        Put(Etc("conf.d/10-operator.toml"), "[logging]\nlevel = \"Debug\"\n");
        Put(Path.Combine(_dataRoot, "telemetry-id"), Guid.NewGuid().ToString("D"));
        Put(Path.Combine(_dataRoot, "updates", "last-update.json"), JsonSerializer.Serialize(
            new UpdateRecord("0.4.0", "0.4.1", _binary, _binary + ".previous", "/backup/pre.db", new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero), Mode: "auto")));
        Put(_binary + ".previous", "0.4.0");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Etc("connectors/2026.10.0/cloudflared"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Etc("secrets/auth_session_secret"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var instance = Instance();
        Assert.Equal(ConfigLayout.Legacy, instance.Config.Layout);
        _system.Units[_system.UnitPathFor(instance)] = ServiceCommands.SystemdUnit(_binary, instance.Config, "sbox-ns", writableConfig: true);
    }

    /// <summary>The <c>ReadWritePaths</c> line of the installed default unit.</summary>
    private string ReadWritePaths()
        => _system.Units[_system.UnitPathFor(Instance())].Split('\n').Single(l => l.Contains("ReadWritePaths=", StringComparison.Ordinal)).Trim();

    private static void Put(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly string[] RuntimeRelative =
    [
        "secrets/auth_session_secret", "secrets/storage_encryption_key", "secrets/security_signing_key.pem",
        "secrets/identity_ecdsa_p256.pem", "secrets/tunnel_token", ".tunnel.lock", ".dns.lock", "conf.d/zzzz-tunnel.toml"
    ];

    // ---- migrate --------------------------------------------------------------------------------------------------

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_moves_runtime_files_into_the_state_folder_and_leaves_operator_files()
    {
        CreateLegacyInstall();
        var telemetryId = File.ReadAllText(Path.Combine(_dataRoot, "telemetry-id"));

        var outcome = await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        Assert.Equal(LayoutOutcome.Migrated, outcome);
        foreach (var relative in RuntimeRelative)
        {
            Assert.False(File.Exists(Etc(relative)), relative);
            Assert.True(File.Exists(State(relative)), relative);
        }

        Assert.Equal("connector", File.ReadAllText(State("bin/2026.10.0/cloudflared")));
        Assert.False(Directory.Exists(Etc("connectors")));
        Assert.Equal(telemetryId, File.ReadAllText(State("telemetry-id")));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "telemetry-id")));
        // Operator files stay: server.toml, the operator's own drop-in and the operator-provided database password.
        Assert.True(File.Exists(Etc("server.toml")));
        Assert.True(File.Exists(Etc("conf.d/10-operator.toml")));
        Assert.Equal("operator password", File.ReadAllText(Etc("secrets/postgres_password")));
        Assert.Equal(StateLayout.CurrentVersion + "\n", File.ReadAllText(State(StateLayout.MarkerFile)));

        var after = Instance().Config;
        Assert.Equal(ConfigLayout.State, after.Layout);
        Assert.True(after.IsValid, string.Join("; ", after.Issues));
        Assert.Equal(9100, after.GetInteger("tunnel.local_port"));
        var created = new List<string>();
        Assert.Equal("session-secret", ServerSecrets.EnsureAndLoad(after, created.Add).AuthSessionSecret);
        Assert.Empty(created);
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_sets_the_config_folder_to_root_and_the_service_group_and_the_state_folder_to_the_service_user()
    {
        CreateLegacyInstall();

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        const UnixFileMode folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        Assert.Equal(folder, File.GetUnixFileMode(_configRoot));
        Assert.Equal(folder, File.GetUnixFileMode(Etc("conf.d")));
        Assert.Equal(file, File.GetUnixFileMode(Etc("server.toml")));
        Assert.Equal(file, File.GetUnixFileMode(Etc("secrets/postgres_password")));
        Assert.Equal(new UnixOwner(0, 1000), _system.OwnerOf(_configRoot));
        Assert.Equal(new UnixOwner(0, 1000), _system.OwnerOf(Etc("server.toml")));
        Assert.Equal(new UnixOwner(0, 1000), _system.OwnerOf(Etc("secrets/postgres_password")));

        const UnixFileMode privateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(privateFolder, File.GetUnixFileMode(State("")));
        Assert.Equal(privateFolder, File.GetUnixFileMode(State("secrets")));
        Assert.Equal(privateFile, File.GetUnixFileMode(State("secrets/tunnel_token")));
        Assert.Equal(privateFile, File.GetUnixFileMode(State("conf.d/zzzz-tunnel.toml")));
        Assert.Equal(privateFolder, File.GetUnixFileMode(State("bin/2026.10.0/cloudflared"))); // keeps the execute bit, owner only
        Assert.Equal(Service, _system.OwnerOf(State("secrets/tunnel_token")));
        Assert.Contains(_system.Chowns, c => c.Path.StartsWith(State(StateLayout.MarkerFile) + ".tmp-", StringComparison.Ordinal) && c.Owner == Service);
        Assert.Equal(Service, _system.OwnerOf(State("")));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_moves_the_updater_state_and_removes_the_old_copies()
    {
        CreateLegacyInstall();

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        var record = _updateState.Read(ServerInstance.DefaultName)!;
        Assert.Equal(("0.4.0", "0.4.1", "/backup/pre.db", "auto"), (record.FromVersion, record.ToVersion, record.BackupPath, record.Mode));
        Assert.Equal(_updateState.PreviousBinaryPath, record.PreviousBinaryPath);
        Assert.Equal("0.4.0", File.ReadAllText(_updateState.PreviousBinaryPath));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "updates", "last-update.json")));
        Assert.False(File.Exists(_binary + ".previous"));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_stops_units_first_re_renders_them_without_the_config_folder_and_writes_the_marker_after_starting()
    {
        CreateLegacyInstall();
        Assert.Equal($"ReadWritePaths={_dataRoot} {_configRoot}", ReadWritePaths());
        _host.OnStart = () => Assert.False(File.Exists(State(StateLayout.MarkerFile)), "the marker is written after the units start");
        _host.OnStop = () => Assert.False(File.Exists(State("secrets/tunnel_token")), "files move only after the units stop");

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        Assert.Equal(["stop sbox-ns", "start sbox-ns"], _host.Calls);
        Assert.Equal($"ReadWritePaths={_dataRoot}", ReadWritePaths());
        Assert.Equal(1, _system.Reloads);
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task A_second_run_changes_nothing_and_stops_no_units()
    {
        CreateLegacyInstall();
        await Migrator().MigrateAsync(Instances(), CancellationToken.None);
        var snapshot = Snapshot();
        var chowns = _system.Chowns.Count;
        _host.Calls.Clear();

        var outcome = await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        Assert.Equal(LayoutOutcome.NothingToDo, outcome);
        Assert.Empty(_host.Calls);
        Assert.Equal(chowns, _system.Chowns.Count);
        Assert.Equal(snapshot, Snapshot());
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(CliApp.Ok, await LayoutCommands.RunAsync(Migrator(), Instances(), revert: false));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("already current", output.ToString());
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_is_safe_to_re_run_after_an_interrupted_run_left_some_files_moved()
    {
        CreateLegacyInstall();
        Directory.CreateDirectory(State("secrets"));
        File.Move(Etc("secrets/tunnel_token"), State("secrets/tunnel_token"));
        File.Copy(Etc("secrets/identity_ecdsa_p256.pem"), State("secrets/identity_ecdsa_p256.pem")); // identical copy in both places

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        Assert.Equal("token", File.ReadAllText(State("secrets/tunnel_token")));
        Assert.Equal("identity", File.ReadAllText(State("secrets/identity_ecdsa_p256.pem")));
        Assert.False(File.Exists(Etc("secrets/identity_ecdsa_p256.pem")));
        Assert.True(File.Exists(State(StateLayout.MarkerFile)));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_refuses_while_the_update_lock_is_held_without_stopping_units_or_moving_files()
    {
        CreateLegacyInstall();
        var snapshot = Snapshot();
        using var held = new FileStream(_binary + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<CliException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));

        Assert.Contains("another sbox-ns update is running", error.Message);
        Assert.Empty(_host.Calls);
        Assert.Empty(_system.Chowns);
        Assert.Equal(snapshot, Snapshot());
        Assert.False(File.Exists(State(StateLayout.MarkerFile)));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_refuses_outside_linux_and_without_root()
    {
        CreateLegacyInstall();
        var snapshot = Snapshot();

        _system.IsLinux = false;
        var notLinux = await Assert.ThrowsAsync<CliException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));
        Assert.Contains("Linux only", notLinux.Message);

        _system.IsLinux = true;
        _system.IsRoot = false;
        var notRoot = await Assert.ThrowsAsync<CliException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));
        Assert.Contains("as root", notRoot.Message);

        Assert.Empty(_host.Calls);
        Assert.Equal(snapshot, Snapshot());
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrate_refuses_a_config_parent_folder_the_service_could_write()
    {
        CreateLegacyInstall();
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        var error = await Assert.ThrowsAsync<CliException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));

        Assert.Contains("writable by group or others", error.Message);
        Assert.Empty(_host.Calls);
        Assert.False(File.Exists(State(StateLayout.MarkerFile)));
    }

    // ---- failure --------------------------------------------------------------------------------------------------

    [TunnelOperatingSystemTests.UnixFact]
    public async Task A_failing_step_undoes_the_moves_and_ownership_writes_no_marker_and_prints_recovery_steps()
    {
        CreateLegacyInstall();
        var snapshot = Snapshot();
        var mode = File.GetUnixFileMode(Etc("server.toml"));
        _system.FailChownOn = State("secrets/tunnel_token");

        var failure = await Assert.ThrowsAsync<LayoutMigrationException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));

        Assert.Contains("tunnel_token", failure.Message);
        Assert.Contains(failure.RecoverySteps, s => s.Contains("no layout marker was written"));
        Assert.False(File.Exists(State(StateLayout.MarkerFile)));
        Assert.Equal(snapshot, Snapshot());
        Assert.Equal(mode, File.GetUnixFileMode(Etc("server.toml")));
        Assert.Equal(LayoutHealth.Legacy, StateLayout.Health(Instance().Config));
        Assert.Equal(["stop sbox-ns", "stop sbox-ns", "start sbox-ns"], _host.Calls); // stopped again for the undo, then running on the untouched layout
        Assert.Equal($"ReadWritePaths={_dataRoot} {_configRoot}", ReadWritePaths());
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task A_failure_is_reported_with_a_non_zero_exit_code_and_the_manual_steps()
    {
        CreateLegacyInstall();
        _system.FailChownOn = State("secrets/tunnel_token");
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            Assert.Equal(CliApp.Failure, await LayoutCommands.RunAsync(Migrator(), Instances(), revert: false));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("layout migrate failed", error.ToString());
        Assert.Contains("no layout marker was written", error.ToString());
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Doctor_reports_a_partial_layout_when_runtime_files_exist_in_both_places_without_a_marker()
    {
        CreateLegacyInstall();
        // The same file with different content in both places: neither can be dropped automatically.
        Put(State("secrets/tunnel_token"), "a different token");

        var failure = await Assert.ThrowsAsync<LayoutMigrationException>(() => Migrator().MigrateAsync(Instances(), CancellationToken.None));

        Assert.Contains("already exists with different content", failure.Message);
        Assert.False(File.Exists(State(StateLayout.MarkerFile)));
        Assert.Equal("token", File.ReadAllText(Etc("secrets/tunnel_token")));
        Assert.Equal("session-secret", File.ReadAllText(Etc("secrets/auth_session_secret"))); // earlier moves were undone
        var (health, message) = StateLayout.Describe(Instance().Config);
        Assert.Equal(LayoutHealth.Partial, health);
        Assert.Contains("partial", message);
    }

    // ---- revert ---------------------------------------------------------------------------------------------------

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Revert_restores_a_layout_the_old_paths_can_read_and_removes_the_marker()
    {
        CreateLegacyInstall();
        await Migrator().MigrateAsync(Instances(), CancellationToken.None);
        _host.Calls.Clear();

        var outcome = await Migrator().RevertAsync(Instances(), CancellationToken.None);

        Assert.Equal(LayoutOutcome.Reverted, outcome);
        Assert.False(File.Exists(State(StateLayout.MarkerFile)));
        Assert.Equal(["stop sbox-ns", "start sbox-ns"], _host.Calls);
        Assert.Equal("identity", File.ReadAllText(Etc("secrets/identity_ecdsa_p256.pem")));
        Assert.Equal("token", File.ReadAllText(Etc("secrets/tunnel_token")));
        Assert.Equal("connector", File.ReadAllText(Etc("connectors/2026.10.0/cloudflared")));
        Assert.True(File.Exists(Etc("conf.d/zzzz-tunnel.toml")));
        Assert.True(File.Exists(Path.Combine(_dataRoot, "telemetry-id")));
        Assert.False(File.Exists(State("secrets/tunnel_token")));

        // What a v0.4.0 binary does: read every runtime file from the config folder and generate nothing.
        var config = Instance().Config;
        Assert.Equal(ConfigLayout.Legacy, config.Layout);
        Assert.Equal(config.ConfigDirectory, config.RuntimeDirectory);
        var created = new List<string>();
        var secrets = ServerSecrets.EnsureAndLoad(config, created.Add);
        Assert.Empty(created);
        Assert.Equal("session-secret", secrets.AuthSessionSecret);
        Assert.Equal("identity", File.ReadAllText(TunnelManager.IdentityPath(config)));

        // The service account owns its config folder again, as the old installer left it, and the unit may write it.
        Assert.Equal(Service, _system.OwnerOf(_configRoot));
        Assert.Equal(Service, _system.OwnerOf(Etc("secrets/tunnel_token")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Etc("secrets/tunnel_token")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Etc("connectors/2026.10.0/cloudflared")));
        Assert.Equal($"ReadWritePaths={_dataRoot} {_configRoot}", ReadWritePaths());
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Revert_carries_the_telemetry_setting_back_into_server_toml()
    {
        CreateLegacyInstall();
        await Migrator().MigrateAsync(Instances(), CancellationToken.None);
        ManagedOverlay.Write(Instance().Config, StateLayout.TelemetryOverlayFile, "managed", new Dictionary<string, object> { ["telemetry.enabled"] = true });

        await Migrator().RevertAsync(Instances(), CancellationToken.None);

        Assert.False(File.Exists(Etc("conf.d/" + StateLayout.TelemetryOverlayFile)));
        Assert.False(File.Exists(State("conf.d/" + StateLayout.TelemetryOverlayFile)));
        Assert.True(Instance().Config.GetBoolean("telemetry.enabled"));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Revert_without_a_marker_changes_nothing_and_migrate_again_after_revert_works()
    {
        CreateLegacyInstall();
        var snapshot = Snapshot();

        Assert.Equal(LayoutOutcome.NothingToDo, await Migrator().RevertAsync(Instances(), CancellationToken.None));
        Assert.Empty(_host.Calls);
        Assert.Equal(snapshot, Snapshot());

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);
        await Migrator().RevertAsync(Instances(), CancellationToken.None);
        Assert.Equal(LayoutOutcome.Migrated, await Migrator().MigrateAsync(Instances(), CancellationToken.None));
        Assert.True(File.Exists(State(StateLayout.MarkerFile)));
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task The_rollback_path_reverts_without_touching_units_or_taking_the_update_lock_again()
    {
        CreateLegacyInstall();
        await Migrator().MigrateAsync(Instances(), CancellationToken.None);
        _host.Calls.Clear();
        using var held = new FileStream(_binary + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var migrator = new LayoutMigrator(_system, _host, _updateState, _binary, _log.Add) { ManageUnits = false, UpdateLockHeld = true };
        await migrator.RevertAsync(Instances(), CancellationToken.None);

        Assert.Empty(_host.Calls);
        Assert.Equal("token", File.ReadAllText(Etc("secrets/tunnel_token")));
    }

    // ---- named instances ------------------------------------------------------------------------------------------

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Named_instances_migrate_with_their_own_state_folder_and_do_not_disturb_each_other()
    {
        CreateLegacyInstall();
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "alpha", 8101, autoUpdate: false);
        Put(Path.Combine(_configRoot, "alpha", "secrets", "auth_session_secret"), "alpha-secret");
        Put(Path.Combine(_configRoot, "alpha", "secrets", "tunnel_token"), "alpha-token");

        await Migrator().MigrateAsync(Instances(), CancellationToken.None);

        var alphaState = Path.Combine(_dataRoot, "alpha", "state");
        Assert.Equal("alpha-secret", File.ReadAllText(Path.Combine(alphaState, "secrets", "auth_session_secret")));
        Assert.Equal("alpha-token", File.ReadAllText(Path.Combine(alphaState, "secrets", "tunnel_token")));
        Assert.Equal("token", File.ReadAllText(State("secrets/tunnel_token")));
        Assert.True(File.Exists(Path.Combine(alphaState, StateLayout.MarkerFile)));
        Assert.False(File.Exists(Path.Combine(_configRoot, "alpha", "secrets", "tunnel_token")));
        Assert.Equal(new UnixOwner(0, 1000), _system.OwnerOf(Path.Combine(_configRoot, "alpha", "server.toml")));
        Assert.Contains("stop sbox-ns@alpha", _host.Calls);
        Assert.Contains("start sbox-ns@alpha", _host.Calls);
    }

    [TunnelOperatingSystemTests.UnixFact]
    public async Task Migrating_one_named_instance_keeps_the_shared_unit_template_writable_while_another_is_still_legacy()
    {
        ConfigFiles.WriteAll(_configRoot, new Dictionary<string, object>());
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "alpha", 8101, autoUpdate: false);
        InstanceServiceCommands.PrepareFolders(_configRoot, _dataRoot, "beta", 8102, autoUpdate: false);
        Put(Path.Combine(_configRoot, "alpha", "secrets", "tunnel_token"), "alpha");
        Put(Path.Combine(_configRoot, "beta", "secrets", "tunnel_token"), "beta");
        var alpha = Instances().Single(i => i.Name == "alpha");
        var beta = Instances().Single(i => i.Name == "beta");
        _system.Hosted = () => Instances();
        var template = _system.UnitPathFor(alpha);
        _system.Units[template] = "ReadWritePaths=old";
        _system.Units[_system.UnitPathFor(beta)] = "ReadWritePaths=old";

        await Migrator().MigrateAsync([alpha], CancellationToken.None);

        Assert.Contains("ReadWritePaths=/var/lib/sbox-ns/%i /etc/sbox-ns/%i", _system.Units[template]);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "alpha", "state", StateLayout.MarkerFile)));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "beta", "state", StateLayout.MarkerFile)));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_configRoot, "beta", "secrets", "tunnel_token")));

        // Once the last legacy instance migrates, the template drops the config folder.
        await Migrator().MigrateAsync([beta], CancellationToken.None);
        Assert.Contains("ReadWritePaths=/var/lib/sbox-ns/%i\n", _system.Units[_system.UnitPathFor(beta)]);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private string[] Snapshot()
        => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".update-lock", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(_root, p) + (File.Exists(p) ? ":" + File.ReadAllText(p).GetHashCode() : "/"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class FakeSystem(string unitFolder) : ILayoutSystem
    {
        private readonly Dictionary<string, UnixOwner> _owners = new(StringComparer.Ordinal);

        public bool IsLinux { get; set; } = true;

        public bool IsRoot { get; set; } = true;

        public string? FailChownOn { get; set; }

        public List<(string Path, UnixOwner Owner)> Chowns { get; } = [];

        public Dictionary<string, string> Units { get; } = new(StringComparer.Ordinal);

        public int Reloads { get; private set; }

        public Func<IReadOnlyList<ServerInstance>> Hosted { get; set; } = () => [];

        public IReadOnlyList<ServerInstance> HostInstances() => Hosted();

        public LayoutAccount ServiceAccount(ServerInstance instance) => new("sbox-ns", Service);

        // Everything starts out owned by whoever created the fixture, as in a v0.4.0 install.
        public UnixOwner? OwnerOf(string path) => _owners.TryGetValue(path, out var owner) ? owner : new UnixOwner(501, 20);

        public void Chown(string path, UnixOwner owner)
        {
            if (FailChownOn is not null && string.Equals(path, FailChownOn, StringComparison.Ordinal))
            {
                throw new IOException($"cannot change the owner of {path}");
            }

            Chowns.Add((path, owner));
            _owners[path] = owner;
        }

        public string UnitPathFor(ServerInstance instance) => Path.Combine(unitFolder, instance.Unit + ".service");

        public string? ReadUnit(string path) => Units.GetValueOrDefault(path);

        public void WriteUnit(string path, string text) => Units[path] = text;

        public Task ReloadUnitsAsync()
        {
            Reloads++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHost : IUpdateHost
    {
        public List<string> Calls { get; } = [];

        public Action? OnStart { get; set; }

        public Action? OnStop { get; set; }

        public Task<bool> IsActiveAsync(string unit, CancellationToken ct) => Task.FromResult(true);

        public Task<int> StartAsync(string unit, CancellationToken ct)
        {
            Calls.Add($"start {unit}");
            OnStart?.Invoke();
            return Task.FromResult(0);
        }

        public Task<int> StopAsync(string unit, CancellationToken ct)
        {
            Calls.Add($"stop {unit}");
            OnStop?.Invoke();
            return Task.FromResult(0);
        }

        public Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> BackupAsync(string binary, ServerInstance instance, string outputPath, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RestoreAsync(string binary, ServerInstance instance, string backupPath, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RevertLayoutAsync(string binary, IReadOnlyList<ServerInstance> instances, CancellationToken ct) => throw new NotSupportedException();
    }
}
