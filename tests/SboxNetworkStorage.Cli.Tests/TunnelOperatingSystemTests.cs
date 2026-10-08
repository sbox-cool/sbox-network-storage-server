using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class TunnelOperatingSystemTests
{
    private const string OwnerFixtureVariable = "SBOX_NS_TEST_TUNNEL_OWNER_FIXTURE";
    private const string OwnerNameVariable = "SBOX_NS_TEST_TUNNEL_OWNER_NAME";
    private const string OwnerUidVariable = "SBOX_NS_TEST_TUNNEL_OWNER_UID";
    private const string OwnerGidVariable = "SBOX_NS_TEST_TUNNEL_OWNER_GID";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [SupportedOSPlatform("linux")]
    [LinuxRootFact]
    public async Task Root_administration_preserves_service_owner_access_and_unprivileged_reenable()
    {
        // No accounts are created or reused. These numeric identities own only this fixture.
        var uid = RandomNumberGenerator.GetInt32(2_000_000_000, 2_100_000_000).ToString();
        var gid = RandomNumberGenerator.GetInt32(2_100_000_000, int.MaxValue).ToString();
        await RunAsync("/usr/bin/getent", ["passwd", uid], expectedExitCode: 2);
        await RunAsync("/usr/bin/getent", ["group", gid], expectedExitCode: 2);
        var fixture = Path.Combine("/tmp", "sbox-ns-root-tunnel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.SetUnixFileMode(fixture, PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var configDirectory = Path.Combine(fixture, "config");
            var dataDirectory = Path.Combine(fixture, "data");
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(dataDirectory);
            Configure(configDirectory);
            foreach (var path in Directory.EnumerateFiles(configDirectory))
                File.SetUnixFileMode(path, PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.SetUnixFileMode(Path.Combine(configDirectory, ConfigLoader.ConfDirectory), PrivateDirectory);
            foreach (var directory in new[] { configDirectory, dataDirectory })
            {
                File.SetUnixFileMode(directory, PrivateDirectory);
                await RunAsync("/usr/bin/chown", ["--", uid + ":" + gid, directory]);
            }

            // Copy the test runtime so the child never needs access to a root-owned checkout/home.
            var childRuntime = Path.Combine(fixture, "runtime");
            CopyRuntime(AppContext.BaseDirectory, childRuntime);
            var assembly = Path.Combine(childRuntime, Path.GetFileName(typeof(TunnelOperatingSystemTests).Assembly.Location));
            using var registryHttp = new HttpClient(new TunnelLifecycleTests.RegistryHandler());
            using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(File.ReadAllBytes("/bin/true")));
            var manager = CreateManager(registryHttp, downloads);
            string? originalName = null;
            for (var administration = 0; administration < 2; administration++)
            {
                await manager.EnableAsync(Load(configDirectory, dataDirectory), CancellationToken.None);
                var enabled = Load(configDirectory, dataDirectory);
                originalName ??= enabled.GetString("tunnel.name");
                Assert.Equal(originalName, enabled.GetString("tunnel.name"));

                var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
                // setpriv clears supplemental groups and capabilities as well as dropping UID/GID.
                var childResult = await RunAsync("/usr/bin/setpriv",
                    ["--reuid=" + uid, "--regid=" + gid, "--clear-groups", "--inh-caps=-all", "--ambient-caps=-all",
                     "--bounding-set=-all", "--no-new-privs", "--", dotnet, "vstest", assembly,
                     "/TestCaseFilter:FullyQualifiedName=SboxNetworkStorage.Cli.Tests.TunnelOperatingSystemTests.Service_owner_reads_private_assets_executes_connector_and_reenables",
                     "/Logger:console;verbosity=normal"],
                    new Dictionary<string, string>
                    {
                        [OwnerFixtureVariable] = fixture, [OwnerNameVariable] = originalName,
                        [OwnerUidVariable] = uid, [OwnerGidVariable] = gid,
                        ["DOTNET_CLI_HOME"] = dataDirectory, ["HOME"] = dataDirectory,
                        ["DOTNET_NOLOGO"] = "true", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true"
                    }, TimeSpan.FromMinutes(2));
                Assert.Matches(@"Passed:\s+1\b", childResult);
                Assert.Equal(originalName, Load(configDirectory, dataDirectory).GetString("tunnel.name"));
                // Both UID and GID must be preserved, not merely permissions widened.
                foreach (var path in new[]
                {
                    configDirectory, Path.Combine(configDirectory, ".tunnel.lock"),
                    Path.Combine(configDirectory, "secrets"), TunnelManager.IdentityPath(enabled), TunnelManager.TokenPath(enabled),
                    Path.Combine(configDirectory, "connectors"), Path.GetDirectoryName(CloudflaredInstaller.ExecutablePath(configDirectory))!,
                    CloudflaredInstaller.ExecutablePath(configDirectory), Path.Combine(configDirectory, ConfigLoader.ConfDirectory)
                })
                    Assert.Equal(uid + ":" + gid, (await RunAsync("/usr/bin/stat", ["--format=%u:%g", "--", path])).Trim());
            }
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    [SupportedOSPlatform("linux")]
    [ServiceOwnerFact]
    public async Task Service_owner_reads_private_assets_executes_connector_and_reenables()
    {
        Assert.NotEqual(0u, EffectiveUserId());
        Assert.Equal(Environment.GetEnvironmentVariable(OwnerUidVariable), EffectiveUserId().ToString());
        Assert.Equal(Environment.GetEnvironmentVariable(OwnerGidVariable), EffectiveGroupId().ToString());
        var fixture = Environment.GetEnvironmentVariable(OwnerFixtureVariable)!;
        var configDirectory = Path.Combine(fixture, "config");
        var dataDirectory = Path.Combine(fixture, "data");
        var enabled = Load(configDirectory, dataDirectory);
        Assert.True(enabled.IsValid, string.Join("; ", enabled.Issues));
        var expectedName = Environment.GetEnvironmentVariable(OwnerNameVariable);
        // These reads and execution run with actual OS credentials, without root capabilities.
        using (var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(enabled)))
            Assert.Equal(expectedName, identity.Name);
        Assert.Equal("test-token", File.ReadAllText(TunnelManager.TokenPath(enabled)));
        Assert.Equal(PrivateFile, File.GetUnixFileMode(TunnelManager.IdentityPath(enabled)));
        Assert.Equal(PrivateFile, File.GetUnixFileMode(TunnelManager.TokenPath(enabled)));
        Assert.Equal(PrivateDirectory, File.GetUnixFileMode(Path.Combine(configDirectory, "secrets")));
        await RunAsync(CloudflaredInstaller.ExecutablePath(configDirectory), []);

        using var registryHttp = new HttpClient(new TunnelLifecycleTests.RegistryHandler());
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(File.ReadAllBytes("/bin/true")));
        await CreateManager(registryHttp, downloads).EnableAsync(enabled, CancellationToken.None);
        var reenabled = Load(configDirectory, dataDirectory);
        Assert.True(reenabled.GetBoolean("tunnel.enabled"));
        Assert.Equal(expectedName, reenabled.GetString("tunnel.name"));
        Assert.Equal("test-token", File.ReadAllText(TunnelManager.TokenPath(reenabled)));
        await RunAsync(CloudflaredInstaller.ExecutablePath(configDirectory), []);
    }

    [UnixFact]
    public async Task Status_persistence_failure_immediately_after_start_terminates_actual_child()
    {
        var fixture = Directory.CreateTempSubdirectory("sbox-ns-child-cleanup-").FullName;
        int? childPid = null;
        try
        {
            Configure(fixture);
            using var registryHttp = new HttpClient(new TunnelLifecycleTests.RegistryHandler());
            // A real long-lived process, not output replay. exec keeps the launched PID unchanged.
            var binary = System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\nexec /bin/sleep 300\n");
            using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(binary));
            await new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads,
                new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))))
                .EnableAsync(Load(fixture, Path.Combine(fixture, "data")), CancellationToken.None);
            var config = Load(fixture, Path.Combine(fixture, "data"));
            // Replacing the status file with a directory makes the first atomic persistence fail,
            // independent of permissions (including when the suite happens to run as root).
            Directory.CreateDirectory(TunnelConnectorState.StatePath(config));
            var state = new TunnelConnectorState(config);
            var logger = new StartFailureLogger(state, pid => childPid = pid);
            using var service = new TunnelConnectorService(config, state, logger);
            var failure = await Record.ExceptionAsync(async () =>
            {
                await service.StartAsync(CancellationToken.None);
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            });
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.True(childPid.HasValue, "The connector must actually start before status persistence fails.");
            Assert.False(IsAlive(childPid!.Value), "The connector was orphaned after its running status could not be persisted.");
        }
        finally
        {
            // Also clean up against the pre-fix runtime, so a failing regression leaves no orphan.
            if (childPid is { } pid)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class StartFailureLogger(TunnelConnectorState state, Action<int> capturePid) : ILogger<TunnelConnectorService>
    {
        public IDisposable? BeginScope<TState>(TState value) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState value, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Set updates the in-memory status before attempting disk persistence. Observe the PID
            // on the failure warning, before the subsequent backoff/finally statuses replace it.
            if (level == LogLevel.Warning && state.Current.ProcessId is { } pid) capturePid(pid);
        }
    }

    private static EffectiveConfig Load(string configDirectory, string dataDirectory)
        => ConfigLoader.Load(configDirectory, dataDirectory, environment: _ => null);

    private static void Configure(string directory)
        => ConfigFiles.WriteAll(directory, new Dictionary<string, object>
        {
            ["server.listen"] = "0.0.0.0:4488", ["server.public_url"] = "https://own.example.test", ["tls.mode"] = "off"
        });

    private static TunnelManager CreateManager(HttpClient registry, HttpClient downloads)
    {
        var binary = File.ReadAllBytes("/bin/true");
        return new TunnelManager(new TunnelRegistryClient(registry), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))));
    }

    [SupportedOSPlatform("linux")]
    private static void CopyRuntime(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        File.SetUnixFileMode(destination, PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var path in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(path));
            File.Copy(path, target);
            File.SetUnixFileMode(target, PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        foreach (var path in Directory.EnumerateDirectories(source))
            CopyRuntime(path, Path.Combine(destination, Path.GetFileName(path)));
    }

    private static async Task<string> RunAsync(string executable, string[] arguments,
        Dictionary<string, string>? environment = null, TimeSpan? timeout = null, int expectedExitCode = 0)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = "/tmp"
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Test subprocess did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == expectedExitCode,
            $"Test subprocess {Path.GetFileName(executable)} exited {process.ExitCode} (expected {expectedExitCode}):\n{output}\n{error}");
        return output;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();
    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint EffectiveGroupId();

    public sealed class LinuxRootFactAttribute : FactAttribute
    {
        public LinuxRootFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || EffectiveUserId() != 0)
                Skip = "Requires Linux root; CI explicitly runs this test through sudo.";
        }
    }

    public sealed class ServiceOwnerFactAttribute : FactAttribute
    {
        public ServiceOwnerFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || EffectiveUserId() == 0 ||
                Environment.GetEnvironmentVariable(OwnerFixtureVariable) is null)
                Skip = "Executed as an isolated unprivileged child of the Linux root regression.";
        }
    }

    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "Requires /bin/sh and /bin/sleep.";
        }
    }
}
