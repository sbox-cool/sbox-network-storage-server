using System.Diagnostics;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Database work belongs to the service account, not the root operator process.</summary>
internal static class RuntimeCommand
{
    public static async Task<int?> TryRunAsync(EffectiveConfig config, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux() || !ServiceCommands.IsRoot()
            || UnixFiles.Lookup(RuntimeIdentity.ServiceAccount) is null)
            return null;

        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        UpdateCommands.RequireStandaloneExecutable(binary);
        var start = new ProcessStartInfo("runuser") { UseShellExecute = false };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(RuntimeIdentity.ServiceAccount);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(binary);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var name = argument.Split('=', 2)[0];
            if (name is "--admin-password-file" or "--password-file")
            {
                var separator = argument.IndexOf('=');
                var path = separator >= 0 ? argument[(separator + 1)..] : arguments[++index];
                start.Environment["NS_ADMIN_PASSWORD"] = File.ReadAllText(path).TrimEnd('\r', '\n');
                continue;
            }
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--config-dir");
        start.ArgumentList.Add(config.ConfigDirectory);
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(config.DataDirectory);
        using var process = Process.Start(start) ?? throw new CliException("cannot start database command as service user");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
