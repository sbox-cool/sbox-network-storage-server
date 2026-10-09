using System.Security.Cryptography;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>Config folder versus state folder: layout resolution, the overlay allowlist and where runtime files land.</summary>
public sealed class StateLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-layout-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");
    private string DataDir => Path.Combine(_root, "data");
    private string StateDir => Path.Combine(DataDir, "state");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private EffectiveConfig Load() => ConfigLoader.Load(ConfigDir, DataDir, environment: _ => null);

    private void Write(string folder, string relativePath, string content)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteOverlay(string name, string content) => Write(StateDir, Path.Combine("conf.d", name), content);

    // ---- overlay allowlist and precedence -----------------------------------------------------------------------

    [Theory]
    [InlineData("[updates]\nchannel = \"canary\"\n", "updates.channel")]
    [InlineData("[database]\nprovider = \"postgres\"\n", "database.provider")]
    [InlineData("[adminpanel]\nenabled = false\n", "adminpanel.enabled")]
    [InlineData("[tls]\ncertificate_path = \"x.pem\"\n", "tls.certificate_path")]
    [InlineData("[server]\ndata_dir = \"/elsewhere\"\n", "server.data_dir")]
    public void An_overlay_key_outside_the_allowlist_fails_the_load_naming_file_and_key(string overlay, string key)
    {
        WriteOverlay("zzzz-tunnel.toml", overlay);

        var config = Load();

        Assert.False(config.IsValid);
        var issue = Assert.Single(config.Issues);
        Assert.Contains("zzzz-tunnel.toml", issue.File);
        Assert.Equal(2, issue.Line);
        Assert.Contains($"'{key}'", issue.Message);
        Assert.Equal(SettingSource.Default, config.Values[key].Source);
    }

    [Fact]
    public void Allowlisted_overlay_keys_apply_when_the_operator_config_does_not_mention_them()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        WriteOverlay("zzzz-tunnel.toml", "[tunnel]\nlocal_port = 9100\nauto = true\n");
        WriteOverlay("telemetry.toml", "[telemetry]\nenabled = true\n");

        // `tunnel.auto` is not a setting: unknown keys still fail, allowlisted or not.
        Assert.Contains(Load().Issues, i => i.Message.Contains("tunnel.auto"));

        WriteOverlay("zzzz-tunnel.toml", "[tunnel]\nlocal_port = 9100\n");
        var config = Load();

        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        Assert.Equal(9100, config.GetInteger("tunnel.local_port"));
        Assert.True(config.GetBoolean("telemetry.enabled"));
        Assert.Equal(SettingSource.ConfD, config.Values["tunnel.local_port"].Source);
        Assert.Contains(Path.Combine(StateDir, "conf.d", "zzzz-tunnel.toml"), config.LoadedFiles);
    }

    [Fact]
    public void The_operator_file_wins_for_keys_the_overlay_does_not_set_and_the_overlay_wins_for_keys_it_manages()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>
        {
            ["server.listen"] = "0.0.0.0:8080", ["server.public_url"] = "https://operator.example.test", ["telemetry.enabled"] = false
        });
        WriteOverlay("telemetry.toml", "[telemetry]\nenabled = true\n");
        WriteOverlay("zzzz-tunnel.toml", "[server]\npublic_url = \"https://managed.example.test\"\n");

        var config = Load();

        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        Assert.Equal("0.0.0.0:8080", config.GetString("server.listen"));
        Assert.Equal("https://managed.example.test", config.GetString("server.public_url"));
        Assert.True(config.GetBoolean("telemetry.enabled"));
    }

    [Fact]
    public void Overlay_files_load_in_name_order_and_environment_still_beats_them()
    {
        WriteOverlay("zzzz-tunnel.toml", "[dns]\nauto_address = true\n");
        WriteOverlay("zzzzz-dns.toml", "[dns]\nauto_address = false\n");

        Assert.False(Load().GetBoolean("dns.auto_address"));
        Assert.True(ConfigLoader.Load(ConfigDir, DataDir, environment: key => key == "NS_DNS__AUTO_ADDRESS" ? "true" : null).GetBoolean("dns.auto_address"));
    }

    [Fact]
    public void The_allowlist_is_the_keys_the_managed_overlays_write()
    {
        string[] allowed =
        [
            "server.listen", "server.public_url", "tls.mode", "tls.acme_domain", "tls.acme_email", "tls.acme_accept_terms",
            "telemetry.enabled", "tunnel.enabled", "tunnel.previous_listen", "dns.enabled", "dns.hostname"
        ];
        string[] denied =
        [
            "updates.channel", "updates.auto_install", "database.provider", "database.postgres.password_file", "tls.certificate_path",
            "tls.key_path", "tls.https_listen", "telemetry.endpoint", "server.data_dir", "adminpanel.enabled", "alerts.discord.enabled",
            "auth.session_secret_file", "tunnelx.enabled"
        ];

        Assert.All(allowed, key => Assert.True(StateLayout.IsAllowedOverlayKey(key), key));
        Assert.All(denied, key => Assert.False(StateLayout.IsAllowedOverlayKey(key), key));
        Assert.All(StateLayout.OverlayKeyPatterns.Where(p => !p.EndsWith('*')), key => Assert.NotNull(SettingDefinitions.Find(key)));
    }

    [Fact]
    public void In_the_legacy_layout_conf_d_stays_in_the_config_folder_and_is_unrestricted()
    {
        Write(ConfigDir, "secrets/auth_session_secret", "legacy");
        Write(ConfigDir, "conf.d/10-operator.toml", "[updates]\nchannel = \"canary\"\n");
        WriteOverlay("zzzz-tunnel.toml", "[updates]\nchannel = \"stable\"\n");

        var config = Load();

        Assert.Equal(ConfigLayout.Legacy, config.Layout);
        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        Assert.Equal("canary", config.GetString("updates.channel"));
        Assert.DoesNotContain(config.LoadedFiles, f => f.StartsWith(StateDir, StringComparison.Ordinal));
    }

    // ---- layout resolution --------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_install_uses_the_state_layout_and_creating_the_state_folder_writes_the_marker()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        var config = Load();

        Assert.Equal(ConfigLayout.State, config.Layout);
        Assert.Equal(StateDir, config.StateDirectory);
        Assert.Equal(StateDir, config.RuntimeDirectory);
        Assert.False(File.Exists(StateLayout.MarkerPath(DataDir)));

        config.EnsureRuntimeDirectory();

        Assert.Equal(StateLayout.CurrentVersion + "\n", File.ReadAllText(StateLayout.MarkerPath(DataDir)));
        Assert.Equal(LayoutHealth.Current, StateLayout.Health(config));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(StateDir));
        }
    }

    [Theory]
    [InlineData("secrets/auth_session_secret")]
    [InlineData("secrets/storage_encryption_key")]
    [InlineData("secrets/security_signing_key.pem")]
    [InlineData("secrets/identity_ecdsa_p256.pem")]
    [InlineData("secrets/tunnel_token")]
    [InlineData("connectors/2026.10.0/cloudflared")]
    [InlineData(".tunnel.lock")]
    [InlineData(".dns.lock")]
    [InlineData("conf.d/zzzz-tunnel.toml")]
    [InlineData("conf.d/zzzzz-dns.toml")]
    public void Without_a_marker_runtime_files_in_the_config_folder_mean_the_legacy_layout(string artifact)
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Write(ConfigDir, artifact, "x");

        var config = Load();

        Assert.Equal(ConfigLayout.Legacy, config.Layout);
        Assert.Equal(ConfigDir, config.RuntimeDirectory);
        Assert.Equal(LayoutHealth.Legacy, StateLayout.Health(config));
        Assert.Contains("layout migrate", StateLayout.Describe(config).Message);
    }

    [Fact]
    public void The_marker_wins_over_leftover_files_and_files_that_are_not_runtime_state_do_not_count()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Write(ConfigDir, "secrets/postgres_password", "operator secret");
        Write(ConfigDir, "conf.d/10-operator.toml", "[logging]\nlevel = \"Debug\"\n");
        Write(DataDir, "telemetry-id", Guid.NewGuid().ToString());
        Assert.Equal(ConfigLayout.State, Load().Layout);

        Write(ConfigDir, "secrets/auth_session_secret", "leftover");
        Assert.Equal(ConfigLayout.Legacy, Load().Layout);
        Write(StateDir, StateLayout.MarkerFile, "1\n");
        Assert.Equal(ConfigLayout.State, Load().Layout);
    }

    [Fact]
    public void Generated_secret_files_outside_the_config_folder_do_not_make_an_install_legacy()
    {
        var outside = Path.Combine(_root, "elsewhere", "session");
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["auth.session_secret_file"] = outside });
        Write(_root, Path.Combine("elsewhere", "session"), "operator provided");

        Assert.Equal(ConfigLayout.State, Load().Layout);
    }

    [Fact]
    public void Runtime_files_in_both_places_without_a_marker_are_reported_as_partial()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Write(ConfigDir, "secrets/tunnel_token", "old");
        Write(StateDir, "secrets/tunnel_token", "new");

        var (health, message) = StateLayout.Describe(Load());

        Assert.Equal(LayoutHealth.Partial, health);
        Assert.Contains("partial", message);
        Assert.Contains("layout migrate", message);
    }

    // ---- where runtime files land ---------------------------------------------------------------------------------

    [Fact]
    public void Runtime_paths_resolve_under_the_state_folder_in_the_state_layout_and_the_config_folder_in_legacy()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        var state = Load();
        Assert.Equal(Path.Combine(StateDir, "secrets", "tunnel_token"), TunnelManager.TokenPath(state));
        Assert.Equal(Path.Combine(StateDir, "secrets", "identity_ecdsa_p256.pem"), TunnelManager.IdentityPath(state));
        Assert.Equal(Path.Combine(StateDir, "bin", CloudflaredInstaller.Version, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared"),
            CloudflaredInstaller.ExecutablePath(state.ExecutablesDirectory));
        Assert.Equal(Path.Combine(StateDir, "conf.d"), state.OverlayDirectory);
        Assert.Equal(Path.Combine(StateDir, "telemetry-id"), state.TelemetryIdPath);

        Write(ConfigDir, ".dns.lock", "");
        var legacy = Load();
        Assert.Equal(ConfigLayout.Legacy, legacy.Layout);
        Assert.Equal(Path.Combine(ConfigDir, "secrets", "tunnel_token"), TunnelManager.TokenPath(legacy));
        Assert.Equal(Path.Combine(ConfigDir, "secrets", "identity_ecdsa_p256.pem"), TunnelManager.IdentityPath(legacy));
        Assert.Equal(Path.Combine(ConfigDir, "connectors", CloudflaredInstaller.Version, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared"),
            CloudflaredInstaller.ExecutablePath(legacy.ExecutablesDirectory));
        Assert.Equal(Path.Combine(ConfigDir, "conf.d"), legacy.OverlayDirectory);
        Assert.Equal(Path.Combine(DataDir, "telemetry-id"), legacy.TelemetryIdPath);
    }

    [Fact]
    public void Generated_secrets_are_created_in_the_state_folder_and_never_touch_the_config_folder()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        var before = Snapshot(ConfigDir);

        var created = new List<string>();
        var secrets = ServerSecrets.EnsureAndLoad(Load(), created.Add);

        Assert.Equal(3, created.Count);
        Assert.All(created, path => Assert.StartsWith(Path.Combine(StateDir, "secrets"), path));
        Assert.Equal(before, Snapshot(ConfigDir));
        Assert.True(File.Exists(StateLayout.MarkerPath(DataDir)));
        Assert.Equal(File.ReadAllText(Path.Combine(StateDir, "secrets", "auth_session_secret")).Trim(), secrets.AuthSessionSecret);

        // A second load keeps them.
        Assert.Empty(CreatedOnSecondLoad());
    }

    [Fact]
    public void Generated_secrets_stay_in_the_config_folder_in_the_legacy_layout_and_absolute_paths_are_kept()
    {
        var absolute = Path.Combine(_root, "operator-secrets", "encryption");
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["auth.storage_encryption_key_file"] = absolute });
        Write(ConfigDir, ".tunnel.lock", "");

        var created = new List<string>();
        ServerSecrets.EnsureAndLoad(Load(), created.Add);

        Assert.Contains(Path.Combine(ConfigDir, "secrets", "auth_session_secret"), created);
        Assert.Contains(absolute, created);
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public async Task Tunnel_enable_writes_identity_token_overlay_and_connector_only_under_the_state_folder()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>
        {
            ["server.listen"] = "0.0.0.0:4488", ["server.public_url"] = "https://own.example.test", ["tls.mode"] = "off"
        });
        var before = Snapshot(ConfigDir);

        await EnableTunnelAsync();

        var config = Load();
        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        Assert.True(config.GetBoolean("tunnel.enabled"));
        Assert.Equal("127.0.0.1:4488", config.GetString("server.listen"));
        Assert.True(File.Exists(Path.Combine(StateDir, "conf.d", TunnelManager.ManagedFile)));
        Assert.True(File.Exists(Path.Combine(StateDir, "secrets", "identity_ecdsa_p256.pem")));
        Assert.Equal("test-token", File.ReadAllText(Path.Combine(StateDir, "secrets", "tunnel_token")));
        Assert.True(File.Exists(CloudflaredInstaller.ExecutablePath(config.ExecutablesDirectory)));
        Assert.StartsWith(Path.Combine(StateDir, "bin"), CloudflaredInstaller.ExecutablePath(config.ExecutablesDirectory));
        Assert.True(File.Exists(Path.Combine(StateDir, StateLayout.TunnelLockFile)));
        Assert.Equal(before, Snapshot(ConfigDir));
    }

    [Fact]
    public async Task Tunnel_enable_keeps_writing_the_config_folder_while_the_install_is_still_legacy()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>
        {
            ["server.listen"] = "0.0.0.0:4488", ["server.public_url"] = "https://own.example.test", ["tls.mode"] = "off"
        });
        Write(ConfigDir, "secrets/auth_session_secret", "legacy");

        await EnableTunnelAsync();

        var config = Load();
        Assert.Equal(ConfigLayout.Legacy, config.Layout);
        Assert.True(config.GetBoolean("tunnel.enabled"));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "conf.d", TunnelManager.ManagedFile)));
        Assert.Equal("test-token", File.ReadAllText(Path.Combine(ConfigDir, "secrets", "tunnel_token")));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "connectors", CloudflaredInstaller.Version, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared")));
        Assert.False(Directory.Exists(StateDir));
    }

    [Fact]
    public async Task Telemetry_enable_and_disable_write_the_overlay_and_never_the_operator_server_file()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["updates.check"] = false });
        var before = Snapshot(ConfigDir);
        var context = new CliContext(CliArguments.Parse(["telemetry", "enable", "--config-dir", ConfigDir, "--data-dir", DataDir]));

        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(context));

        Assert.True(Load().GetBoolean("telemetry.enabled"));
        Assert.Equal("[telemetry]\nenabled = true\n", string.Join('\n', File.ReadAllLines(Path.Combine(StateDir, "conf.d", StateLayout.TelemetryOverlayFile)).Skip(1)) + "\n");
        Assert.True(File.Exists(Load().TelemetryIdPath));
        Assert.StartsWith(StateDir, Load().TelemetryIdPath);
        Assert.Equal(before, Snapshot(ConfigDir));

        var disable = new CliContext(CliArguments.Parse(["telemetry", "disable", "--config-dir", ConfigDir, "--data-dir", DataDir]));
        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(disable));
        Assert.False(Load().GetBoolean("telemetry.enabled"));
        Assert.Equal(before, Snapshot(ConfigDir));
    }

    [Fact]
    public async Task Telemetry_enable_writes_server_toml_in_the_legacy_layout()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["updates.check"] = false });
        Write(ConfigDir, "secrets/auth_session_secret", "legacy");
        var context = new CliContext(CliArguments.Parse(["telemetry", "enable", "--config-dir", ConfigDir, "--data-dir", DataDir]));

        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(context));

        Assert.True(Load().GetBoolean("telemetry.enabled"));
        Assert.Contains("enabled = true", File.ReadAllText(Path.Combine(ConfigDir, "server.toml")));
        Assert.Equal(Path.Combine(DataDir, "telemetry-id"), Load().TelemetryIdPath);
        Assert.False(Directory.Exists(StateDir));
    }

    // ---- who writes runtime files ---------------------------------------------------------------------------------

    [Fact]
    public void A_root_run_command_does_its_runtime_file_work_as_the_owner_of_the_state_folder()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        Directory.CreateDirectory(StateDir);
        var config = Load();
        var host = new FakeIdentityHost { EffectiveUser = 0, StateOwner = new UnixOwner(990, 985), Account = new UnixOwner(1, 1) };

        using (RuntimeIdentity.Enter(config, host))
        {
            Assert.Equal([new UnixOwner(990, 985)], host.Assumed);
            Assert.Empty(host.Restored);
        }

        Assert.Equal([new UnixOwner(0, 0)], host.Restored);
    }

    [Fact]
    public void Without_a_state_folder_yet_the_service_account_owns_what_root_creates_and_non_root_never_switches()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>());
        var config = Load();

        Assert.Equal(new UnixOwner(990, 985), RuntimeIdentity.TargetFor(config, new FakeIdentityHost { EffectiveUser = 0, Account = new UnixOwner(990, 985) }));
        Assert.Null(RuntimeIdentity.TargetFor(config, new FakeIdentityHost { EffectiveUser = 0 }));
        Assert.Null(RuntimeIdentity.TargetFor(config, new FakeIdentityHost { EffectiveUser = 1000, Account = new UnixOwner(990, 985) }));
        Assert.Null(RuntimeIdentity.TargetFor(config, new FakeIdentityHost { EffectiveUser = 0, IsLinux = false, Account = new UnixOwner(990, 985) }));
        Assert.Null(RuntimeIdentity.TargetFor(config, new FakeIdentityHost { EffectiveUser = 0, StateOwner = new UnixOwner(0, 0) }));
    }

    private sealed class FakeIdentityHost : IIdentityHost
    {
        public bool IsLinux { get; init; } = true;
        public uint EffectiveUser { get; init; }
        public UnixOwner EffectiveOwner => new(EffectiveUser, 0);
        public UnixOwner? StateOwner { get; init; }
        public UnixOwner? Account { get; init; }
        public List<UnixOwner> Assumed { get; } = [];
        public List<UnixOwner> Restored { get; } = [];

        public UnixOwner? OwnerOf(string path) => StateOwner;

        public UnixOwner? Lookup(string name) => Account;

        public void Assume(UnixOwner owner) => Assumed.Add(owner);

        public void Restore(UnixOwner original) => Restored.Add(original);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private List<string> CreatedOnSecondLoad()
    {
        var created = new List<string>();
        ServerSecrets.EnsureAndLoad(Load(), created.Add);
        return created;
    }

    private async Task EnableTunnelAsync()
    {
        using var registryHttp = new HttpClient(new TunnelLifecycleTests.RegistryHandler());
        byte[] binary = [1, 2, 3, 4];
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(binary));
        var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))));
        await manager.EnableAsync(Load(), CancellationToken.None);
    }

    /// <summary>Relative path and content hash of everything under <paramref name="folder"/>.</summary>
    private static string[] Snapshot(string folder)
        => Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path) + (File.Exists(path) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "/"))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
