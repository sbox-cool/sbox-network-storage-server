using System.Diagnostics;
using System.Security;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Registers and controls sbox-ns as an OS service: systemd (Linux), launchd (macOS), SCM (Windows).</summary>
public static class ServiceCommands
{
    public const string ServiceName = "sbox-ns";
    internal const string SystemdUnitPath = "/etc/systemd/system/sbox-ns.service";
    private const string LaunchdLabel = "cool.sbox.sbox-ns";

    public static async Task<int> RunAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "install|uninstall|start|stop|restart|status");
        return sub switch
        {
            "install" when context.Args.Option("instance") is { } instance => await InstanceServiceCommands.InstallAsync(context, instance),
            "install" => await InstallAsync(context),
            "uninstall" when context.Args.Option("instance") is { } instance => await InstanceServiceCommands.UninstallAsync(instance),
            "uninstall" => await UninstallAsync(),
            "start" => await ControlAsync("start"),
            "stop" => await ControlAsync("stop"),
            "restart" => await RestartAsync(context),
            "status" => await ControlAsync("status"),
            _ => throw new CliException($"unknown service command '{sub}'", CliApp.Usage)
        };
    }

    public static async Task<int> LogsAsync(CliContext context)
    {
        var follow = context.Args.Flag("follow", "f");
        if (OperatingSystem.IsLinux())
        {
            return await RunAsync("journalctl", ["-u", ServiceName, "--no-pager", follow ? "-f" : "-n", follow ? "-n200" : "200"], inherit: true);
        }

        if (OperatingSystem.IsMacOS())
        {
            var config = context.LoadConfig();
            var log = Path.Combine(config.DataDirectory, "logs", "sbox-ns.log");
            return await RunAsync("tail", follow ? ["-n", "200", "-f", log] : ["-n", "200", log], inherit: true);
        }

        Console.WriteLine("On Windows the service logs to the Application event log (source: sbox-ns):");
        Console.WriteLine("  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='sbox-ns'} -MaxEvents 200");
        return CliApp.Ok;
    }

    /// <summary>True when a service registration exists for this host.</summary>
    public static bool IsInstalled()
    {
        if (OperatingSystem.IsLinux()) return File.Exists(SystemdUnitPath);
        if (OperatingSystem.IsMacOS()) return File.Exists(LaunchdPlistPath());
        return OperatingSystem.IsWindows() && RunCapture("sc.exe", ["query", ServiceName]).ExitCode == 0;
    }

    private static async Task<int> InstallAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        var arguments = $"start --config-dir \"{config.ConfigDirectory}\" --data-dir \"{config.DataDirectory}\"";
        var autoUpdate = context.Args.Flag("auto-update");
        if (autoUpdate && !OperatingSystem.IsLinux())
        {
            throw new CliException("--auto-update needs systemd (Linux); run `sbox-ns update` by hand on this platform", CliApp.Usage);
        }

        if (OperatingSystem.IsLinux())
        {
            RequireRoot();
            var user = RunCapture("id", ["-u", ServiceName]).ExitCode == 0 ? ServiceName : Environment.UserName;
            ConfigFiles.WriteAtomically(SystemdUnitPath, SystemdUnit(binary, config, user, writableConfig: config.Layout == ConfigLayout.Legacy));
            SystemdUnits.SyncBindDropIn(ServiceName, config);
            await RunAsync("systemctl", ["daemon-reload"]);
            await RunAsync("systemctl", ["enable", ServiceName]);
            Console.WriteLine($"Installed {SystemdUnitPath} (runs as {user}). Start it with: sbox-ns service start");
            if (autoUpdate)
            {
                var selection = config.ConfigDirectory == ConfigPaths.LinuxServiceConfigDir
                    ? "--all-instances"
                    : $"--config-dir {config.ConfigDirectory} --data-dir {config.DataDirectory}";
                return await InstanceServiceCommands.EnableAutoUpdateAsync(binary, config.ConfigDirectory, selection);
            }

            return CliApp.Ok;
        }

        if (OperatingSystem.IsMacOS())
        {
            var plist = LaunchdPlistPath();
            Directory.CreateDirectory(Path.Combine(config.DataDirectory, "logs"));
            ConfigFiles.WriteAtomically(plist, LaunchdPlist(binary, config));
            Console.WriteLine($"Installed {plist}. Start it with: sbox-ns service start");
            return CliApp.Ok;
        }

        if (OperatingSystem.IsWindows())
        {
            var exit = await RunAsync("sc.exe", ["create", ServiceName, "binPath=", $"\"{binary}\" {arguments}", "start=", "auto", "DisplayName=", "sbox Network Storage Server"]);
            if (exit == 0)
            {
                await RunAsync("sc.exe", ["description", ServiceName, "Self-hosted s&box Network Storage server"]);
                Console.WriteLine("Installed Windows service sbox-ns. Start it with: sbox-ns service start");
            }

            return exit;
        }

        throw new CliException("service installation is not supported on this platform");
    }

    private static async Task<int> UninstallAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            RequireRoot();
            await RunAsync("systemctl", ["disable", "--now", ServiceName]);
            File.Delete(SystemdUnitPath);
            await RunAsync("systemctl", ["daemon-reload"]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await ControlAsync("stop");
            File.Delete(LaunchdPlistPath());
        }
        else if (OperatingSystem.IsWindows())
        {
            await RunAsync("sc.exe", ["stop", ServiceName]);
            await RunAsync("sc.exe", ["delete", ServiceName]);
        }

        Console.WriteLine("Service removed. Config and data folders were left in place.");
        return CliApp.Ok;
    }

    private static async Task<int> RestartAsync(CliContext context)
    {
        // Never stop a running server for a config that will not start.
        var config = context.LoadConfig();
        if (!config.IsValid)
        {
            ConfigCommands.PrintIssues(config);
            Console.Error.WriteLine("The running server was left untouched. Fix the problems above, then restart again.");
            return CliApp.Usage;
        }

        if (OperatingSystem.IsLinux())
        {
            RequireRoot();
            if (SystemdUnits.SyncBindDropIn(ServiceName, config))
                await RunAsync("systemctl", ["daemon-reload"]);
        }

        return await ControlAsync("restart");
    }

    public static async Task<int> ControlAsync(string action)
    {
        if (OperatingSystem.IsLinux())
        {
            return await RunAsync("systemctl", action == "status" ? ["status", ServiceName, "--no-pager"] : [action, ServiceName], inherit: true);
        }

        if (OperatingSystem.IsMacOS())
        {
            var domain = IsRoot() ? "system" : $"gui/{RunCapture("id", ["-u"]).Output.Trim()}";
            return action switch
            {
                "start" => await RunAsync("launchctl", ["bootstrap", domain, LaunchdPlistPath()], inherit: true),
                "stop" => await RunAsync("launchctl", ["bootout", $"{domain}/{LaunchdLabel}"], inherit: true),
                "restart" => await RunAsync("launchctl", ["kickstart", "-k", $"{domain}/{LaunchdLabel}"], inherit: true),
                _ => await RunAsync("launchctl", ["print", $"{domain}/{LaunchdLabel}"], inherit: true)
            };
        }

        if (OperatingSystem.IsWindows())
        {
            if (action == "restart")
            {
                await RunAsync("sc.exe", ["stop", ServiceName], inherit: true);
                await Task.Delay(TimeSpan.FromSeconds(3));
                return await RunAsync("sc.exe", ["start", ServiceName], inherit: true);
            }

            return await RunAsync("sc.exe", [action == "status" ? "query" : action, ServiceName], inherit: true);
        }

        throw new CliException("service control is not supported on this platform");
    }

    /// <param name="writableConfig">Only for the legacy layout: older binaries write secrets and tunnel state into the config folder.</param>
    internal static string SystemdUnit(string binary, EffectiveConfig config, string user, bool writableConfig = false)
        => SystemdUnits.ServiceUnit(binary, config, user, writableConfig);

    private static string LaunchdPlist(string binary, EffectiveConfig config)
    {
        static string X(string value) => SecurityElement.Escape(value);
        var log = Path.Combine(config.DataDirectory, "logs", "sbox-ns.log");
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{LaunchdLabel}</string>
              <key>ProgramArguments</key>
              <array>
                <string>{X(binary)}</string>
                <string>start</string>
                <string>--config-dir</string><string>{X(config.ConfigDirectory)}</string>
                <string>--data-dir</string><string>{X(config.DataDirectory)}</string>
              </array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
              <key>WorkingDirectory</key><string>{X(config.DataDirectory)}</string>
              <key>StandardOutPath</key><string>{X(log)}</string>
              <key>StandardErrorPath</key><string>{X(log)}</string>
            </dict>
            </plist>

            """;
    }

    private static string LaunchdPlistPath()
        => IsRoot()
            ? $"/Library/LaunchDaemons/{LaunchdLabel}.plist"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{LaunchdLabel}.plist");

    internal static bool IsRoot() => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    internal static void RequireRoot()
    {
        if (!IsRoot())
        {
            throw new CliException("this command must run as root (try: sudo sbox-ns ...)");
        }
    }

    internal static async Task<int> RunAsync(string file, IReadOnlyList<string> arguments, bool inherit = false)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new CliException($"could not start {file}");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliException($"{file} was not found on this system");
        }
    }

    internal static (int ExitCode, string Output) RunCapture(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }
    }
}
