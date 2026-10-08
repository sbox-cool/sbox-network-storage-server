using System.Diagnostics;
using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>Side effects of an unattended update that tests replace: service control and the migration run.</summary>
public interface IUpdateHost
{
    Task<bool> IsActiveAsync(string unit, CancellationToken ct);

    Task<int> StartAsync(string unit, CancellationToken ct);

    Task<int> StopAsync(string unit, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db migrate</c> for one instance and returns its exit code.</summary>
    Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct);
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

    public async Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct)
    {
        var arguments = new[] { "db", "migrate", "--config-dir", instance.Config.ConfigDirectory, "--data-dir", instance.Config.DataDirectory };
        if (OperatingSystem.IsLinux())
        {
            // A root-owned newly created database/WAL would prevent the service from starting.
            var user = ServiceCommands.RunCapture("systemctl", ["show", instance.Unit, "--property=User", "--value"]);
            if (user.ExitCode != 0)
            {
                throw new CliException($"cannot determine the service user for {instance.Unit}");
            }

            if (!string.IsNullOrWhiteSpace(user.Output) && user.Output.Trim() != "root")
            {
                return await RunAsync("runuser", ["-u", user.Output.Trim(), "--", binary, .. arguments], ct, TimeSpan.FromMinutes(20));
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
    /// <summary>Atomically swaps <paramref name="source"/> into <paramref name="target"/>; works while the target is running.</summary>
    public static void Replace(string source, string target)
    {
        var staged = target + ".new";
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
