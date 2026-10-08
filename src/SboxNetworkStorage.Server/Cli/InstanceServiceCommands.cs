using System.Globalization;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// <c>sbox-ns service install --instance &lt;name&gt; --port &lt;p&gt; [--auto-update]</c>: one more
/// server on this host sharing the binary, as the systemd unit <c>sbox-ns@&lt;name&gt;</c>.
/// </summary>
public static class InstanceServiceCommands
{
    public static async Task<int> InstallAsync(CliContext context, string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new CliException("named instances need systemd (Linux)", CliApp.Usage);
        }

        if (!ServerInstances.IsValidName(name))
        {
            throw new CliException($"instance name '{name}' must match ^[a-z0-9][a-z0-9-]{{0,31}}$ and not be '{ServerInstance.DefaultName}'", CliApp.Usage);
        }

        var portText = context.Args.Option("port") ?? throw new CliException("missing --port <port>", CliApp.Usage);
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new CliException($"--port must be between 1 and 65535 (got '{portText}')", CliApp.Usage);
        }

        ServiceCommands.RequireRoot();
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        var autoUpdate = context.Args.Flag("auto-update");
        var config = PrepareFolders(ConfigPaths.LinuxServiceConfigDir, ConfigPaths.LinuxServiceDataDir, name, port, autoUpdate);
        if (!config.IsValid)
        {
            ConfigCommands.PrintIssues(config);
            throw new CliException($"configuration in {config.ConfigDirectory} is invalid", CliApp.Usage);
        }

        var user = ServiceCommands.RunCapture("id", ["-u", ServiceCommands.ServiceName]).ExitCode == 0 ? ServiceCommands.ServiceName : Environment.UserName;
        foreach (var directory in new[] { config.ConfigDirectory, config.DataDirectory })
        {
            await ServiceCommands.RunAsync("chown", ["-R", $"{user}:{user}", directory]);
            await ServiceCommands.RunAsync("chmod", ["0750", directory]);
        }

        ConfigFiles.WriteAtomically(SystemdUnits.InstanceTemplatePath, SystemdUnits.InstanceTemplate(binary, user));
        await ServiceCommands.RunAsync("systemctl", ["daemon-reload"]);
        var unit = ServerInstances.UnitFor(name);
        var enabled = await ServiceCommands.RunAsync("systemctl", ["enable", unit]);
        if (enabled != 0)
        {
            throw new CliException($"systemctl enable {unit} failed (exit {enabled})");
        }

        Console.WriteLine($"Installed instance {name}: config {config.ConfigDirectory}, data {config.DataDirectory}, listening on {config.GetString("server.listen")} (runs as {user}).");
        Console.WriteLine($"Start it with: systemctl start {unit}");
        return autoUpdate ? await EnableAutoUpdateAsync(binary, config.ConfigDirectory, "--all-instances") : CliApp.Ok;
    }

    public static async Task<int> UninstallAsync(string name)
    {
        if (!OperatingSystem.IsLinux() || !ServerInstances.IsValidName(name))
        {
            throw new CliException("usage: sbox-ns service uninstall --instance <name> (Linux)", CliApp.Usage);
        }

        ServiceCommands.RequireRoot();
        await ServiceCommands.RunAsync("systemctl", ["disable", "--now", ServerInstances.UnitFor(name)]);
        Console.WriteLine($"Instance {name} stopped and disabled. Its config ({ServerInstances.ConfigDirectory(ConfigPaths.LinuxServiceConfigDir, name)}) and data were left in place;");
        Console.WriteLine("remove the config folder too, or `update --auto --all-instances` keeps treating it as an instance.");
        return CliApp.Ok;
    }

    /// <summary>
    /// Creates <c>&lt;configRoot&gt;/&lt;name&gt;</c> (missing files only) and <c>&lt;dataRoot&gt;/&lt;name&gt;</c>,
    /// points <c>server.listen</c> at <c>127.0.0.1:&lt;port&gt;</c> and, with <paramref name="autoUpdate"/>,
    /// sets <c>updates.auto_install = true</c>. Returns the instance's loaded configuration.
    /// </summary>
    internal static EffectiveConfig PrepareFolders(string configRoot, string dataRoot, string name, int port, bool autoUpdate)
    {
        var configDirectory = ServerInstances.ConfigDirectory(configRoot, name);
        var dataDirectory = ServerInstances.DataDirectory(dataRoot, name);
        var listen = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        ConfigFiles.WriteMissing(configDirectory, new Dictionary<string, object> { ["server.listen"] = listen });
        ConfigFiles.SetValue(configDirectory, SettingDefinitions.Find("server.listen")!, listen);
        if (autoUpdate)
        {
            ConfigFiles.SetValue(configDirectory, SettingDefinitions.Find("updates.auto_install")!, true);
        }

        Directory.CreateDirectory(dataDirectory);
        return ServerInstances.Enumerate(configRoot, dataRoot).Single(i => i.Name == name).Config;
    }

    /// <summary>Opts the config folder in (<c>updates.auto_install = true</c>) and installs/enables the update timer.</summary>
    internal static async Task<int> EnableAutoUpdateAsync(string binary, string configDirectory, string selection)
    {
        ConfigFiles.SetValue(configDirectory, SettingDefinitions.Find("updates.auto_install")!, true);
        ConfigFiles.WriteAtomically(SystemdUnits.UpdateServicePath, SystemdUnits.UpdateService(binary, selection));
        ConfigFiles.WriteAtomically(SystemdUnits.UpdateTimerPath, SystemdUnits.UpdateTimerUnit);
        await ServiceCommands.RunAsync("systemctl", ["daemon-reload"]);
        var enabled = await ServiceCommands.RunAsync("systemctl", ["enable", "--now", SystemdUnits.UpdateTimer]);
        if (enabled != 0)
        {
            throw new CliException($"systemctl enable --now {SystemdUnits.UpdateTimer} failed (exit {enabled})");
        }

        Console.WriteLine($"Unattended updates enabled: {SystemdUnits.UpdateTimer} runs `sbox-ns update --auto {selection}` every 15 minutes");
        Console.WriteLine("(installs only inside updates.window, on updates.channel; see `sbox-ns config show`).");
        return CliApp.Ok;
    }
}
