using System.Diagnostics;
using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// Side effects of an update that tests replace: service control, the migration run and the database
/// backup/restore. Database work runs as the service user, never as root.
/// </summary>
public interface IUpdateHost
{
    Task<bool> IsActiveAsync(string unit, CancellationToken ct);

    Task<int> StartAsync(string unit, CancellationToken ct);

    Task<int> StopAsync(string unit, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db migrate</c> for one instance and returns its exit code.</summary>
    Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db backup --output &lt;path&gt;</c> for one instance and returns its exit code.</summary>
    Task<int> BackupAsync(string binary, ServerInstance instance, string outputPath, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db restore &lt;path&gt;</c> for one instance and returns its exit code.</summary>
    Task<int> RestoreAsync(string binary, ServerInstance instance, string backupPath, CancellationToken ct);

    /// <summary>
    /// Moves the runtime files of every migrated instance back into its config folder (<c>layout migrate --revert</c>)
    /// so an older binary can start. The units are already stopped and the update lock is held. Returns 0 on success.
    /// </summary>
    Task<int> RevertLayoutAsync(string binary, IReadOnlyList<ServerInstance> instances, CancellationToken ct);
}

/// <summary>systemd on Linux; elsewhere only the default service via the platform service manager.</summary>
public sealed class SystemUpdateHost : IUpdateHost
{
    public async Task<bool> IsActiveAsync(string unit, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            return await RunAsync("systemctl", ["is-active", "--quiet", unit], ct) == 0;
        }

        return unit == ServerInstances.DefaultUnit && ServiceCommands.IsInstalled();
    }

    public Task<int> StartAsync(string unit, CancellationToken ct) => ControlAsync("start", unit, ct);

    public Task<int> StopAsync(string unit, CancellationToken ct) => ControlAsync("stop", unit, ct);

    public Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct)
        => RunAsServiceUserAsync(binary, instance, ["db", "migrate"], ct);

    public Task<int> BackupAsync(string binary, ServerInstance instance, string outputPath, CancellationToken ct)
        => RunAsServiceUserAsync(binary, instance, ["db", "backup", "--output", outputPath], ct);

    public Task<int> RestoreAsync(string binary, ServerInstance instance, string backupPath, CancellationToken ct)
        => RunAsServiceUserAsync(binary, instance, ["db", "restore", backupPath], ct);

    public async Task<int> RevertLayoutAsync(string binary, IReadOnlyList<ServerInstance> instances, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        var migrator = new LayoutMigrator(HostLayoutSystem.Instance, this, UpdateState.ForHost(), binary, Console.WriteLine)
        {
            ManageUnits = false,
            UpdateLockHeld = true
        };
        try
        {
            await migrator.RevertAsync(instances, ct);
            return 0;
        }
        catch (LayoutMigrationException ex)
        {
            foreach (var step in ex.RecoverySteps)
            {
                Console.Error.WriteLine($"  {step}");
            }

            throw;
        }
    }

    /// <summary>
    /// Runs a database command of <paramref name="binary"/> for one instance as the unit's service user:
    /// root-owned database files would stop the service, and root must not write into a folder the
    /// service account controls.
    /// </summary>
    private static async Task<int> RunAsServiceUserAsync(string binary, ServerInstance instance, IReadOnlyList<string> command, CancellationToken ct)
    {
        var arguments = new List<string>(command) { "--config-dir", instance.Config.ConfigDirectory, "--data-dir", instance.Config.DataDirectory };
        if (OperatingSystem.IsLinux())
        {
            var user = ServiceCommands.RunCapture("systemctl", ["show", instance.Unit, "--property=User", "--value"]);
            if (user.ExitCode != 0)
            {
                throw new CliException($"cannot determine the service user for {instance.Unit}");
            }

            if (!string.IsNullOrWhiteSpace(user.Output) && user.Output.Trim() != "root")
            {
                var extractDirectory = "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + Path.Combine(instance.Config.DataDirectory, ".net");
                return await RunAsync("runuser", ["-u", user.Output.Trim(), "--", "env", extractDirectory, binary, .. arguments], ct, TimeSpan.FromMinutes(20));
            }
        }

        return await RunAsync(binary, arguments, ct, TimeSpan.FromMinutes(20));
    }

    private static async Task<int> ControlAsync(string action, string unit, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            return await RunAsync("systemctl", [action, unit], ct);
        }

        if (unit != ServerInstances.DefaultUnit)
        {
            throw new CliException($"named instances ({unit}) need systemd");
        }

        return await ServiceCommands.ControlAsync(action);
    }

    private static async Task<int> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new CliException($"could not start {file}");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw new CliException($"{file} was cancelled or exceeded its deadline");
            }
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliException($"{file} was not found on this system");
        }
    }
}

/// <summary>Atomic binary replacement shared by manual and unattended updates.</summary>
public static class BinarySwap
{
    /// <summary>
    /// Atomically swaps <paramref name="source"/> into <paramref name="target"/>; works while the target is running.
    /// Refuses a target folder the service account could write, and symbolic links at the target or the staging file.
    /// </summary>
    public static void Replace(string source, string target, FolderTrust? trust = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        (trust ?? FolderTrust.Host).Require(folder, "binary folder");
        var staged = target + ".new";
        FolderTrust.RejectSymbolicLink(target, "binary");
        FolderTrust.RejectSymbolicLink(staged, "staging file");
        File.Copy(source, staged, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.Move(staged, target, overwrite: true);
            return;
        }

        // Windows cannot overwrite a running executable but can rename it.
        var old = target + ".old";
        File.Delete(old);
        File.Move(target, old);
        File.Move(staged, target);
    }
}
