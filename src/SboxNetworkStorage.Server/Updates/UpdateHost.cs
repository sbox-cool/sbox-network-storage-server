using System.Diagnostics;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Storage.Relational;

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

    /// <summary>Runs the target binary's non-mutating <c>compatibility --json</c> preflight for one instance and returns its exit code.</summary>
    Task<int> CheckCompatibilityAsync(string targetBinary, string installedBinary, ServerInstance instance, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db backup --output &lt;path&gt;</c> for one instance and returns its exit code.</summary>
    Task<int> BackupAsync(string binary, ServerInstance instance, string outputPath, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; db restore &lt;path&gt;</c> for one instance and returns its exit code.</summary>
    Task<int> RestoreAsync(string binary, ServerInstance instance, string backupPath, CancellationToken ct);

    /// <summary>
    /// Moves the runtime files of every migrated instance back into its config folder (<c>layout migrate --revert</c>)
    /// so an older binary can start. The units are already stopped and the update lock is held. Returns 0 on success.
    /// </summary>
    Task<int> RevertLayoutAsync(string binary, IReadOnlyList<ServerInstance> instances, CancellationToken ct);

    /// <summary>
    /// Runs <c>&lt;binary&gt; db status --json</c> for one instance and parses the schema version and compatibility floor;
    /// null when the command fails or prints something else.
    /// </summary>
    Task<SchemaState?> ReadSchemaStateAsync(string binary, ServerInstance instance, CancellationToken ct);

    /// <summary>
    /// Runs <c>&lt;binary&gt; schema-info --json</c>. Null when the binary does not know the command, i.e. it predates
    /// rollback-safe migrations and only runs on the exact schema it shipped with.
    /// </summary>
    Task<BinarySchemaInfo?> ProbeSchemaInfoAsync(string binary, CancellationToken ct);

    /// <summary>
    /// Runs <c>&lt;binary&gt; update verify --json</c> for one instance as the service user and parses the verdict.
    /// Null when the command fails, prints something else, or the binary predates the census (exit 5 is a regression, still a verdict).
    /// </summary>
    Task<PostUpdateVerdict?> ReadUpdateVerdictAsync(string binary, ServerInstance instance, int hours, CancellationToken ct);

    /// <summary>Runs <c>&lt;binary&gt; update observed-libraries --json</c> for one instance as the service user. Null when it fails.</summary>
    Task<IReadOnlyList<ObservedLibrary>?> ReadObservedLibrariesAsync(string binary, ServerInstance instance, CancellationToken ct);
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

    public async Task<int> CheckCompatibilityAsync(string targetBinary, string installedBinary, ServerInstance instance, CancellationToken ct)
    {
        // The root-only update state and download folders are not traversable by the service user.
        // Execute a read-only copy in the trusted installed-binary directory instead.
        var folder = Path.GetDirectoryName(Path.GetFullPath(installedBinary))!;
        FolderTrust.Host.Require(folder, "compatibility probe directory");
        FolderTrust.RejectSymbolicLink(targetBinary, "compatibility target");
        var probe = Path.Combine(folder, $".sbox-ns-compat-{Guid.NewGuid():N}");
        try
        {
            File.Copy(targetBinary, probe, overwrite: false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return await RunAsServiceUserAsync(probe, instance, ["compatibility", "--json"], ct);
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

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

    public Task<SchemaState?> ReadSchemaStateAsync(string binary, ServerInstance instance, CancellationToken ct)
    {
        var (file, arguments) = ServiceUserInvocation(binary, instance, ["db", "status", "--json"]);
        var result = ServiceCommands.RunCapture(file, arguments);
        return Task.FromResult(result.ExitCode == 0 ? SchemaRollback.TryParseState(result.Output) : null);
    }

    public Task<BinarySchemaInfo?> ProbeSchemaInfoAsync(string binary, CancellationToken ct)
    {
        var result = ServiceCommands.RunCapture(binary, ["schema-info", "--json"]);
        return Task.FromResult(result.ExitCode == 0 ? BinarySchemaInfo.TryParse(result.Output) : null);
    }

    public Task<PostUpdateVerdict?> ReadUpdateVerdictAsync(string binary, ServerInstance instance, int hours, CancellationToken ct)
    {
        var (file, arguments) = ServiceUserInvocation(binary, instance, ["update", "verify", "--json", "--hours", hours.ToString()]);
        var result = ServiceCommands.RunCapture(file, arguments);
        var passed = result.ExitCode is 0 or UpdateVerifyCommand.RegressionExitCode;
        return Task.FromResult(passed ? PostUpdateVerdict.TryParse(result.Output) : null);
    }

    public Task<IReadOnlyList<ObservedLibrary>?> ReadObservedLibrariesAsync(string binary, ServerInstance instance, CancellationToken ct)
    {
        var (file, arguments) = ServiceUserInvocation(binary, instance, ["update", "observed-libraries", "--json"]);
        var result = ServiceCommands.RunCapture(file, arguments);
        if (result.ExitCode != 0)
        {
            return Task.FromResult<IReadOnlyList<ObservedLibrary>?>(null);
        }

        try
        {
            return Task.FromResult<IReadOnlyList<ObservedLibrary>?>(System.Text.Json.JsonSerializer.Deserialize<List<ObservedLibrary>>(result.Output, ReleaseCompatibility.Json));
        }
        catch (System.Text.Json.JsonException)
        {
            return Task.FromResult<IReadOnlyList<ObservedLibrary>?>(null);
        }
    }

    /// <summary>
    /// Runs a database command of <paramref name="binary"/> for one instance as the unit's service user:
    /// root-owned database files would stop the service, and root must not write into a folder the
    /// service account controls.
    /// </summary>
    private static Task<int> RunAsServiceUserAsync(string binary, ServerInstance instance, IReadOnlyList<string> command, CancellationToken ct)
    {
        var (file, arguments) = ServiceUserInvocation(binary, instance, command);
        return RunAsync(file, arguments, ct, TimeSpan.FromMinutes(20));
    }

    private static (string File, IReadOnlyList<string> Arguments) ServiceUserInvocation(string binary, ServerInstance instance, IReadOnlyList<string> command)
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
                return ("runuser", ["-u", user.Output.Trim(), "--", "env", extractDirectory, binary, .. arguments]);
            }
        }

        return (binary, arguments);
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
        if (IsRunningExecutable(target)) LoadEveryReferencedAssembly();
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

    private static bool IsRunningExecutable(string path)
        => Environment.ProcessPath is { } running
            && string.Equals(Path.GetFullPath(running), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// A single-file build reads its assemblies from its own file the first time each is used. Once that file is
    /// replaced, the next first use (System.IO.Pipelines for the update record, for example) would read the new
    /// binary and fail, turning a finished update into a rollback. Load the whole reference graph while the file
    /// is still this binary. Returns how many references were visited.
    /// </summary>
    internal static int LoadEveryReferencedAssembly()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<System.Reflection.Assembly>([typeof(BinarySwap).Assembly]);
        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!seen.Add(reference.FullName)) continue;
                try
                {
                    pending.Push(System.Reflection.Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Reference-only facades and optional platform assemblies are not in every build.
                }
            }
        }

        return seen.Count;
    }
}
