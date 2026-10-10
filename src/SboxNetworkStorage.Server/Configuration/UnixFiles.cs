using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Unix ownership calls that .NET does not expose: who owns a path, who an account is, and changing an owner without following symbolic links.</summary>
public static class UnixFiles
{
    /// <summary>Owner of an existing path (a symbolic link is not followed), or null when it does not exist.</summary>
    public static UnixOwner? OwnerOf(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
        {
            return null;
        }

        var (exit, output) = Run("stat", OperatingSystem.IsMacOS() ? ["-f", "%u:%g", path] : ["--format=%u:%g", "--", path]);
        return exit == 0 ? Parse(output.Trim().Split(':')) : null;
    }

    /// <summary>The account named <paramref name="name"/>, or null when it does not exist.</summary>
    public static UnixOwner? Lookup(string name)
    {
        var user = Run("id", ["-u", name]);
        var group = Run("id", ["-g", name]);
        return user.ExitCode == 0 && group.ExitCode == 0 ? Parse([user.Output.Trim(), group.Output.Trim()]) : null;
    }

    /// <summary>chown without following a symbolic link at <paramref name="path"/>.</summary>
    public static void Chown(string path, UnixOwner owner)
    {
        if (LChown(path, owner.User, owner.Group) != 0)
        {
            throw new IOException($"cannot change the owner of {path} to {owner.User}:{owner.Group} (error {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>
    /// Gives <paramref name="path"/> the group of <paramref name="reference"/> and group read access, so a service
    /// running in that group can read a file root wrote into the operator config folder. Does nothing (and keeps
    /// the owner-only mode) when the group is already shared or this process may not change it.
    /// </summary>
    public static void ShareGroup(string path, string reference)
    {
        if (OperatingSystem.IsWindows() || OwnerOf(reference) is not { } folder || OwnerOf(path) is not { } file || folder.Group == file.Group)
        {
            return;
        }

        try
        {
            Chown(path, file with { Group = folder.Group });
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The folder counterpart of <see cref="ShareGroup"/>: group read and traverse (0750).</summary>
    public static void ShareFolderGroup(string path, string reference)
    {
        if (OperatingSystem.IsWindows() || OwnerOf(reference) is not { } parent || OwnerOf(path) is not { } folder || parent.Group == folder.Group)
        {
            return;
        }

        try
        {
            Chown(path, folder with { Group = parent.Group });
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        catch (IOException)
        {
        }
    }

    private static UnixOwner? Parse(string[] parts)
        => parts.Length == 2 && uint.TryParse(parts[0], out var user) && uint.TryParse(parts[1], out var group) ? new UnixOwner(user, group) : null;

    private static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments)
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

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    private static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint user, uint group);
}
