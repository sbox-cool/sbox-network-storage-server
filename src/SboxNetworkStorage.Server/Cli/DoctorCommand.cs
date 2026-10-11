using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Server.Tunnels;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Runs every operational check and prints pass/warn/fail for each.</summary>
public static class DoctorCommand
{
    private enum Outcome { Pass, Warn, Fail }

    public static async Task<int> RunAsync(CliContext context)
    {
        var failures = 0;
        void Report(Outcome outcome, string check, string detail)
        {
            if (outcome == Outcome.Fail) failures++;
            var label = outcome switch { Outcome.Pass => "PASS", Outcome.Warn => "WARN", _ => "FAIL" };
            Console.WriteLine($"[{label}] {check,-14} {detail}");
        }

        Console.WriteLine($"sbox-ns {BuildInfo.Version} ({BuildInfo.RuntimeIdentifier})");
        var config = context.LoadConfig();
        if (config.IsValid)
        {
            Report(Outcome.Pass, "config", $"{config.ConfigDirectory} ({config.LoadedFiles.Count} file(s))");
        }
        else
        {
            Report(Outcome.Fail, "config", string.Join("; ", config.Issues));
            return CliApp.Failure;
        }

        foreach (var ignored in config.IgnoredSettings)
        {
            Report(Outcome.Warn, "config", $"{ignored}; releases come from {UpdateTrust.Compiled.Repository} via {UpdateTrust.Compiled.FeedUrl}");
        }

        if (config.LoadedFiles.Count == 0)
        {
            Report(Outcome.Warn, "config", "no config files found; defaults are in use (run `sbox-ns setup`)");
        }

        CheckLayout(config, Report);

        if (context.Args.Option("project") is { } projectId)
        {
            await using var projectServices = CliServices.Build(config);
            await using var scope = projectServices.CreateAsyncScope();
            return await ProjectCommands.RunAuthorityAsync(scope.ServiceProvider, projectId, context.Args.Flag("json"), CancellationToken.None);
        }
        await using (var services = CliServices.Build(config))
        {
            var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
            try
            {
                var ping = await admin.PingAsync(CancellationToken.None);
                Report(Outcome.Pass, "database", $"{admin.ProviderName} at {admin.RedactedTarget} ({ping.ServerVersion}, {ping.RoundTrip.TotalMilliseconds:F0} ms)");
                var version = await admin.GetSchemaVersionAsync(CancellationToken.None);
                Report(
                    version == admin.SupportedSchemaVersion ? Outcome.Pass : version < admin.SupportedSchemaVersion ? Outcome.Warn : Outcome.Fail,
                    "schema",
                    version == admin.SupportedSchemaVersion
                        ? $"version {version}"
                        : version < admin.SupportedSchemaVersion
                            ? $"version {version}, {admin.SupportedSchemaVersion} pending (applied on next start or `sbox-ns db migrate`)"
                            : $"version {version} is newer than this binary supports ({admin.SupportedSchemaVersion}); upgrade sbox-ns");
            }
            catch (Exception ex)
            {
                Report(Outcome.Fail, "database", $"{admin.ProviderName} at {admin.RedactedTarget}: {ex.Message}");
            }
        }

        ListenAddress.TryParse(config.GetString("server.listen"), out var listen);
        Report(PortStatus(listen), "http port", PortDetail(listen));
        var tlsMode = config.GetString("tls.mode");
        var tunnel = TunnelConnectorState.Read(config);
        Report(!tunnel.Enabled ? Outcome.Pass : tunnel.ConnectorState == "connected" ? Outcome.Pass : Outcome.Warn,
            "tunnel", $"{(tunnel.Enabled ? tunnel.Hostname : "disabled")}; connector {tunnel.ConnectorState}; cloudflared {tunnel.ConnectorVersion}");
        await CheckDnsAsync(config, Report);
        if (tlsMode == "off")
        {
            var publicHttps = Uri.TryCreate(config.GetString("server.public_url"), UriKind.Absolute, out var publicUri) && publicUri.Scheme == "https";
            Report(publicHttps ? Outcome.Pass : Outcome.Warn, "tls", publicHttps
                ? "HTTPS at trusted loopback reverse proxy; origin is HTTP"
                : "off (plain HTTP). Reachability from s&box games over plain HTTP is untested.");
        }
        else
        {
            ListenAddress.TryParse(config.GetString("tls.https_listen"), out var https);
            Report(PortStatus(https), "https port", PortDetail(https));
            if (tlsMode == "certificate")
            {
                var certificate = config.GetPath("tls.certificate_path", config.ConfigDirectory);
                Report(File.Exists(certificate) ? Outcome.Pass : Outcome.Fail, "certificate", certificate);
            }
            else
            {
                Report(Outcome.Pass, "tls", $"acme for {config.GetString("tls.acme_domain")}");
            }
        }

        Directory.CreateDirectory(config.DataDirectory);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(config.DataDirectory))!);
        var freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
        Report(freeGb < 1 ? Outcome.Fail : freeGb < 5 ? Outcome.Warn : Outcome.Pass, "disk", $"{freeGb:F1} GB free at {config.DataDirectory}");

        if (!config.GetBoolean("updates.check"))
        {
            Report(Outcome.Warn, "updates", "update checks disabled (updates.check = false)");
        }
        else
        {
            try
            {
                using var http = ReleaseFeed.CreateHttpClient();
                var latest = await new ReleaseFeed(http, config).GetReleaseAsync(null, CancellationToken.None);
                var notice = UpdateCheckService.Evaluate(BuildInfo.Version, latest, DateTimeOffset.UtcNow);
                Report(notice.UpdateAvailable ? (latest.Security ? Outcome.Fail : Outcome.Warn) : Outcome.Pass, "updates",
                    notice.UpdateAvailable
                        ? $"{latest.Version} is available{(latest.Security ? " (SECURITY)" : string.Empty)}; run `sbox-ns update`"
                        : $"up to date (latest {latest.Version})");
            }
            catch (Exception ex)
            {
                Report(Outcome.Warn, "updates", $"could not check: {ex.Message}");
            }
        }

        CheckUnattendedUpdates(config, Report);

        var publicUrl = config.GetString("server.public_url");
        Report(string.IsNullOrWhiteSpace(publicUrl) ? Outcome.Warn : Outcome.Pass, "public url",
            string.IsNullOrWhiteSpace(publicUrl) ? "server.public_url is not set" : publicUrl);
        Report(Outcome.Pass, "telemetry", config.GetBoolean("telemetry.enabled")
            ? $"anonymous usage statistics enabled ({config.GetString("telemetry.endpoint")}); preview with `sbox-ns telemetry preview`"
            : "anonymous usage statistics disabled (opt-in: `sbox-ns telemetry enable`)");

        return failures == 0 ? CliApp.Ok : CliApp.Failure;
    }

    /// <summary>Reports the config/state split of every instance: current, still legacy (with the migration notice), or partial.</summary>
    private static void CheckLayout(EffectiveConfig config, Action<Outcome, string, string> report)
    {
        IReadOnlyList<ServerInstance> others = OperatingSystem.IsLinux()
            ? ServerInstances.Enumerate().Where(i => i.Config.ConfigDirectory != config.ConfigDirectory).ToList()
            : [];
        foreach (var (name, instance) in new[] { ("layout", config) }.Concat(others.Select(i => ($"layout {i.Name}", i.Config))))
        {
            var (health, message) = StateLayout.Describe(instance);
            report(health switch { LayoutHealth.Partial => Outcome.Fail, LayoutHealth.Legacy => Outcome.Warn, _ => Outcome.Pass }, name, message);
        }
    }

    /// <summary>Reports the unattended-update settings, the last recorded update and the instances sharing this binary.</summary>
    private static void CheckUnattendedUpdates(EffectiveConfig config, Action<Outcome, string, string> report)
    {
        var settings = AutoUpdateSettings.FromConfig(config);
        report(Outcome.Pass, "auto update", settings.AutoInstall
            ? $"on: channel {settings.Channel}, window {settings.Window}, minimum release age {settings.MinReleaseAgeHours} h"
            : $"off (channel {settings.Channel}); opt in with `sbox-ns service install --auto-update`");

        IReadOnlyList<ServerInstance> instances = OperatingSystem.IsLinux() ? ServerInstances.Enumerate() : [];
        if (instances.All(i => i.Config.ConfigDirectory != config.ConfigDirectory))
        {
            instances = [new ServerInstance(ServerInstance.DefaultName, ServerInstances.DefaultUnit, config), .. instances];
        }

        var updateState = UpdateState.ForHost();
        foreach (var instance in instances)
        {
            var record = instance.Config.IsValid ? updateState.Read(instance.Name) : null;
            var label = instances.Count > 1 ? $"update {instance.Name}" : "last update";
            if (!instance.Config.IsValid)
            {
                report(Outcome.Fail, label, $"invalid config in {instance.Config.ConfigDirectory}: {string.Join("; ", instance.Config.Issues)}");
            }
            else if (record is not null)
            {
                report(record.IsFailed ? Outcome.Warn : Outcome.Pass, label, record.IsFailed
                    ? $"{record.FromVersion} -> {record.ToVersion} FAILED at {record.UpdatedAt:u} and was rolled back: {record.Reason}"
                    : $"{record.FromVersion} -> {record.ToVersion} at {record.UpdatedAt:u} ({record.Mode ?? "manual"})");
            }
            else if (instances.Count > 1)
            {
                report(Outcome.Pass, label, $"no update recorded ({instance.Unit}, {instance.Config.ConfigDirectory})");
            }
        }
    }

    /// <summary>Warns when the hosted DNS name does not (yet) resolve to every configured address.</summary>
    private static async Task CheckDnsAsync(EffectiveConfig config, Action<Outcome, string, string> report)
    {
        if (!config.GetBoolean("dns.enabled"))
        {
            report(Outcome.Pass, "dns", "hosted DNS name disabled");
            return;
        }
        var hostname = config.GetString("dns.hostname");
        var expected = new[] { config.GetString("dns.ipv4"), config.GetString("dns.ipv6") }
            .Where(a => a.Length > 0).Select(IPAddress.Parse).ToArray();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resolved = await Dns.GetHostAddressesAsync(hostname, timeout.Token);
            var missing = expected.Where(a => !resolved.Contains(a)).ToArray();
            report(missing.Length == 0 ? Outcome.Pass : Outcome.Warn, "dns", missing.Length == 0
                ? $"{hostname} resolves to {string.Join(", ", expected.Select(a => a.ToString()))}"
                : $"{hostname} resolves to {(resolved.Length == 0 ? "nothing" : string.Join(", ", resolved.Select(a => a.ToString())))}, expected {string.Join(", ", expected.Select(a => a.ToString()))} (DNS changes can take a few minutes)");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            report(Outcome.Warn, "dns", $"{hostname} could not be resolved ({ex.Message}); DNS changes can take a few minutes");
        }
    }

    private static Outcome PortStatus(ListenAddress address)
    {
        try
        {
            using var listener = new TcpListener(address.Address ?? IPAddress.Loopback, address.Port);
            listener.Start();
            listener.Stop();
            return Outcome.Pass;
        }
        catch (SocketException)
        {
            return IsServerResponding(address) ? Outcome.Pass : Outcome.Fail;
        }
    }

    private static string PortDetail(ListenAddress address)
    {
        var host = address.IsLocalhost ? "localhost" : address.Address!.ToString();
        try
        {
            using var listener = new TcpListener(address.Address ?? IPAddress.Loopback, address.Port);
            listener.Start();
            listener.Stop();
            return $"{host}:{address.Port} is free";
        }
        catch (SocketException ex)
        {
            return IsServerResponding(address)
                ? $"{host}:{address.Port} is in use by a running sbox-ns"
                : $"{host}:{address.Port} cannot be bound: {ex.Message}";
        }
    }

    private static bool IsServerResponding(ListenAddress address)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var host = address.Address is null || address.Address.Equals(IPAddress.Any) || address.Address.Equals(IPAddress.IPv6Any)
                ? "localhost"
                : address.Address.ToString();
            var response = http.GetAsync($"http://{host}:{address.Port}/v3/server-info").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
