using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// <c>sbox-ns layout migrate [--revert]</c>: moves the runtime files (generated secrets, tunnel and DNS state, the
/// connector binary, the telemetry ID) out of the config folder into the state folder, so the config folder can be
/// read-only to the service. <c>--revert</c> puts them back for older binaries.
/// </summary>
public static class LayoutCommands
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "migrate");
        if (sub != "migrate")
        {
            throw new CliException($"unknown layout command '{sub}'. Use: layout migrate [--revert]", CliApp.Usage);
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new CliException("layout migrate runs on Linux only; other platforms already keep runtime state under the data folder", CliApp.Usage);
        }

        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        var migrator = new LayoutMigrator(HostLayoutSystem.Instance, new SystemUpdateHost(), UpdateState.ForHost(), binary, Console.WriteLine);
        return await RunAsync(migrator, ResolveInstances(context), context.Args.Flag("revert"));
    }

    /// <summary>Runs the migration (or revert) and prints the outcome; the exit code is non-zero when a step failed.</summary>
    public static async Task<int> RunAsync(LayoutMigrator migrator, IReadOnlyList<ServerInstance> instances, bool revert)
    {
        try
        {
            var outcome = revert
                ? await migrator.RevertAsync(instances, CancellationToken.None)
                : await migrator.MigrateAsync(instances, CancellationToken.None);
            Console.WriteLine(outcome switch
            {
                LayoutOutcome.Migrated => $"Layout migrated for {string.Join(", ", instances.Select(i => i.Name))}: runtime files are under each instance's state folder and the config folders are read-only to the service.",
                LayoutOutcome.Reverted => $"Layout reverted for {string.Join(", ", instances.Select(i => i.Name))}: runtime files are back in the config folders.",
                _ when revert => "The layout is not migrated (no marker); nothing to revert and nothing was changed.",
                _ => "The layout is already current (marker present); nothing was changed."
            });
            return CliApp.Ok;
        }
        catch (LayoutMigrationException ex)
        {
            Console.Error.WriteLine($"error: layout {(revert ? "revert" : "migrate")} failed: {ex.Message}");
            foreach (var step in ex.RecoverySteps)
            {
                Console.Error.WriteLine($"  {step}");
            }

            return CliApp.Failure;
        }
    }

    /// <summary>The first step of <c>update --auto</c>: migrate every instance that has no marker yet. Returns the exit code to stop with, or null to carry on.</summary>
    public static async Task<int?> MigrateWhenNeededAsync(IReadOnlyList<ServerInstance> instances, LayoutMigrator migrator)
    {
        if (instances.All(i => StateLayout.HasMarker(i.Config.DataDirectory)))
        {
            return null;
        }

        Console.WriteLine("Migrating the install layout (runtime files out of the config folder) before updating.");
        var exit = await RunAsync(migrator, instances, revert: false);
        return exit == CliApp.Ok ? null : exit;
    }

    /// <summary>Every instance on this host, or only the one selected by <c>--config-dir</c>/<c>--data-dir</c>.</summary>
    private static IReadOnlyList<ServerInstance> ResolveInstances(CliContext context)
    {
        if (context.Args.Option("config-dir") is not null || context.Args.Option("data-dir") is not null
            || Environment.GetEnvironmentVariable(ConfigPaths.ConfigDirEnvironmentVariable) is { Length: > 0 })
        {
            return [ServerInstances.ForConfig(context.LoadConfig())];
        }

        var instances = ServerInstances.Enumerate();
        return instances.Count > 0
            ? instances
            : throw new CliException($"no instances found ({ConfigPaths.LinuxServiceConfigDir}/server.toml or {ConfigPaths.LinuxServiceConfigDir}/<name>/server.toml)", CliApp.Usage);
    }
}

/// <summary>Linux with systemd: the real account lookups, ownership changes and unit files.</summary>
internal sealed class HostLayoutSystem : ILayoutSystem
{
    public static HostLayoutSystem Instance { get; } = new();

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool IsRoot => IsLinux && RuntimeIdentity.Host.EffectiveUser == 0;

    public LayoutAccount ServiceAccount(ServerInstance instance)
    {
        var shown = ServiceCommands.RunCapture("systemctl", ["show", instance.Unit, "--property=User", "--value"]);
        var user = shown.ExitCode == 0 ? shown.Output.Trim() : string.Empty;
        if (user is "" or "root")
        {
            user = RuntimeIdentity.ServiceAccount;
        }

        return UnixFiles.Lookup(user) is { } owner
            ? new LayoutAccount(user, owner)
            : throw new CliException($"cannot determine the service user of {instance.Unit} (no account named {user})");
    }

    public IReadOnlyList<ServerInstance> HostInstances() => ServerInstances.Enumerate();

    public UnixOwner? OwnerOf(string path) => UnixFiles.OwnerOf(path);

    public void Chown(string path, UnixOwner owner) => UnixFiles.Chown(path, owner);

    public string UnitPathFor(ServerInstance instance)
        => instance.Name == ServerInstance.DefaultName ? ServiceCommands.SystemdUnitPath : SystemdUnits.InstanceTemplatePath;

    public string? ReadUnit(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteUnit(string path, string text) => ConfigFiles.WriteAtomically(path, text);

    public async Task ReloadUnitsAsync()
    {
        if (await ServiceCommands.RunAsync("systemctl", ["daemon-reload"]) != 0)
        {
            throw new CliException("systemctl daemon-reload failed");
        }
    }
}
