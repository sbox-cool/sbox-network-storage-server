using System.Diagnostics;
using System.Runtime.InteropServices;
using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// Decides whether a folder may hold files that privileged code reads or replaces. A folder is
/// trusted when it is not a symbolic link, is owned by root (or by the current user when not
/// root), and is not writable by group or others. Anything the service account can write into
/// is not trusted by root.
/// </summary>
public sealed class FolderTrust(uint effectiveUser, Func<string, uint> ownerOf)
{
    public static FolderTrust Host { get; } = new(OperatingSystem.IsWindows() ? 0u : EffectiveUserId(), StatOwner);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    public void Require(string folder, string description)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new DirectoryInfo(folder);
        if (!info.Exists)
        {
            throw new CliException($"{description} {folder} does not exist");
        }

        if (info.LinkTarget is not null)
        {
            throw new CliException($"{description} {folder} is a symbolic link; refusing to use it");
        }

        var owner = ownerOf(folder);
        if (owner != 0 && owner != effectiveUser)
        {
            throw new CliException($"{description} {folder} is owned by uid {owner}; it must be owned by root (or by the user running this command)");
        }

        if (effectiveUser == 0 && owner != 0)
        {
            throw new CliException($"{description} {folder} is owned by uid {owner}; it must be owned by root");
        }

        if ((File.GetUnixFileMode(folder) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new CliException($"{description} {folder} is writable by group or others; refusing to use it");
        }
    }

    /// <summary>Rejects <paramref name="file"/> when it exists as a symbolic link.</summary>
    public static void RejectSymbolicLink(string file, string description)
    {
        if (!OperatingSystem.IsWindows() && new FileInfo(file).LinkTarget is not null)
        {
            throw new CliException($"{description} {file} is a symbolic link; refusing to use it");
        }
    }

    private static uint StatOwner(string path)
    {
        var arguments = OperatingSystem.IsMacOS() ? new[] { "-f", "%u", path } : ["-c", "%u", "--", path];
        var start = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new CliException("cannot run stat to check folder ownership");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || !uint.TryParse(output, out var owner))
        {
            throw new CliException($"cannot determine the owner of {path}");
        }

        return owner;
    }
}
