using System.Diagnostics;
using System.Runtime.InteropServices;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Tunnels;

/// <summary>Root administration preserves the installed config directory's explicit runtime owner.</summary>
internal static class LinuxTunnelOwnership
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    public static async Task ApplyAsync(EffectiveConfig config, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || EffectiveUserId() != 0) return;
        var owner = (await RunAsync("/usr/bin/stat", ["--format=%u:%g", "--", config.ConfigDirectory], ct)).Trim();
        var separator = owner.IndexOf(':');
        if (separator < 1 || !uint.TryParse(owner.AsSpan(0, separator), out _) ||
            !uint.TryParse(owner.AsSpan(separator + 1), out _))
            throw new InvalidOperationException("Cannot determine the tunnel runtime owner from the config directory.");

        var executable = CloudflaredInstaller.ExecutablePath(config.ConfigDirectory);
        var confDirectory = Path.Combine(config.ConfigDirectory, ConfigLoader.ConfDirectory);
        Directory.CreateDirectory(confDirectory);
        var paths = new[]
        {
            Path.Combine(config.ConfigDirectory, ".tunnel.lock"),
            Path.Combine(config.ConfigDirectory, "secrets"),
            TunnelManager.IdentityPath(config), TunnelManager.TokenPath(config),
            Path.Combine(config.ConfigDirectory, "connectors"),
            Path.GetDirectoryName(executable)!, executable, confDirectory
        };
        var arguments = new List<string> { "--no-dereference", "--", owner };
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Tunnel runtime paths must not be symbolic links.");
            arguments.Add(path);
        }
        await RunAsync("/usr/bin/chown", arguments, ct);
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot preserve tunnel runtime ownership.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stderr; // Never log arbitrary child output or private filenames.
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot preserve tunnel runtime ownership; run the command as the installed service user.");
        return await stdout;
    }
}
