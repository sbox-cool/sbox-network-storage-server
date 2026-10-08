using System.Net;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>One sbox-ns server on this host: its systemd unit and its resolved configuration.</summary>
public sealed record ServerInstance(string Name, string Unit, EffectiveConfig Config)
{
    public const string DefaultName = "default";

    /// <summary>Loopback URL of <c>/health</c> on the instance's HTTP listener (<c>server.listen</c>).</summary>
    public string HealthUrl
    {
        get
        {
            ListenAddress.TryParse(Config.GetString("server.listen"), out var listen);
            var host = listen.IsLocalhost || listen.Address is null ? "localhost"
                : listen.Address.Equals(IPAddress.Any) ? "127.0.0.1"
                : listen.Address.Equals(IPAddress.IPv6Any) ? "[::1]"
                : listen.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{listen.Address}]"
                : listen.Address.ToString();
            return $"http://{host}:{listen.Port}/health";
        }
    }
}

/// <summary>
/// Finds the instances sharing this binary: the default service (<c>/etc/sbox-ns/server.toml</c>,
/// unit <c>sbox-ns</c>) and every named instance (<c>/etc/sbox-ns/&lt;name&gt;/server.toml</c>,
/// data in <c>/var/lib/sbox-ns/&lt;name&gt;</c>, unit <c>sbox-ns@&lt;name&gt;</c>).
/// </summary>
public static partial class ServerInstances
{
    public const string DefaultUnit = "sbox-ns";

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name) && name != ServerInstance.DefaultName;

    public static string UnitFor(string name) => $"{DefaultUnit}@{name}";

    public static string ConfigDirectory(string configRoot, string name) => Path.Combine(configRoot, name);

    public static string DataDirectory(string dataRoot, string name) => Path.Combine(dataRoot, name);

    /// <summary>Enumerates instances under the given roots, sorted with the default instance first.</summary>
    public static IReadOnlyList<ServerInstance> Enumerate(string configRoot = ConfigPaths.LinuxServiceConfigDir,
        string dataRoot = ConfigPaths.LinuxServiceDataDir, Func<string, string?>? environment = null)
    {
        // Instance directories are explicit; NS_CONFIG_DIR/NS_DATA_DIR must not redirect them.
        environment ??= key => key is ConfigPaths.ConfigDirEnvironmentVariable or ConfigPaths.DataDirEnvironmentVariable
            ? null
            : Environment.GetEnvironmentVariable(key);
        var instances = new List<ServerInstance>();
        if (File.Exists(Path.Combine(configRoot, SettingDefinitions.ServerFile)))
        {
            instances.Add(new ServerInstance(ServerInstance.DefaultName, DefaultUnit, ConfigLoader.Load(configRoot, dataRoot, environment: environment)));
        }

        if (!Directory.Exists(configRoot))
        {
            return instances;
        }

        foreach (var directory in Directory.EnumerateDirectories(configRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!IsValidName(name) || !File.Exists(Path.Combine(directory, SettingDefinitions.ServerFile)))
            {
                continue;
            }

            instances.Add(new ServerInstance(name, UnitFor(name),
                ConfigLoader.Load(directory, DataDirectory(dataRoot, name), environment: environment)));
        }

        return instances;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}
