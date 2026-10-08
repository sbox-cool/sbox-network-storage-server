namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Resolves the config folder and install directory for this process.</summary>
public static class ConfigPaths
{
    public const string LinuxServiceConfigDir = "/etc/sbox-ns";
    public const string LinuxServiceDataDir = "/var/lib/sbox-ns";
    public const string ConfigDirEnvironmentVariable = "NS_CONFIG_DIR";
    public const string DataDirEnvironmentVariable = "NS_DATA_DIR";

    /// <summary>Directory containing the running executable.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Config folder precedence: <c>--config-dir</c> flag, <c>NS_CONFIG_DIR</c>,
    /// <c>/etc/sbox-ns</c> when it holds a server.toml (Linux service installs),
    /// then <c>&lt;install dir&gt;/config</c>.
    /// </summary>
    public static string ResolveConfigDirectory(string? flagValue)
    {
        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            return Path.GetFullPath(flagValue);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConfigDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (OperatingSystem.IsLinux() && File.Exists(Path.Combine(LinuxServiceConfigDir, SettingDefinitions.ServerFile)))
        {
            return LinuxServiceConfigDir;
        }

        return Path.Combine(InstallDirectory, "config");
    }

    /// <summary>
    /// Data folder precedence: <c>--data-dir</c> flag, <c>NS_DATA_DIR</c>,
    /// <c>server.data_dir</c>, <c>/var/lib/sbox-ns</c> for the Linux service config folder,
    /// then <c>&lt;install dir&gt;/data</c>.
    /// </summary>
    public static string ResolveDataDirectory(string? flagValue, string configuredValue, string configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            return Path.GetFullPath(flagValue);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return Path.GetFullPath(configuredValue, configDirectory);
        }

        return string.Equals(configDirectory, LinuxServiceConfigDir, StringComparison.Ordinal)
            ? LinuxServiceDataDir
            : Path.Combine(InstallDirectory, "data");
    }
}
