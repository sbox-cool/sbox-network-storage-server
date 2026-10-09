using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Renders the embedded install/ unit templates.</summary>
public static class SystemdUnits
{
    public const string InstanceTemplatePath = "/etc/systemd/system/sbox-ns@.service";
    public const string UpdateServicePath = "/etc/systemd/system/sbox-ns-update.service";
    public const string UpdateTimerPath = "/etc/systemd/system/sbox-ns-update.timer";
    public const string UpdateTimer = "sbox-ns-update.timer";

    public static string ServiceUnit(string binary, EffectiveConfig config, string user, bool writableConfig = false)
        => Load("sbox-ns.service")
            .Replace("@BINARY@", Quote(binary))
            .Replace("@USER@", user)
            .Replace("@GROUP@", Group(user))
            .Replace("@CONFIG_DIR@", Quote(config.ConfigDirectory))
            .Replace("@DATA_DIR@", Quote(config.DataDirectory))
            .Replace("@READ_WRITE_PATHS@", Quote(config.DataDirectory) + (writableConfig ? " " + Quote(config.ConfigDirectory) : string.Empty));

    public static string InstanceTemplate(string binary, string user, bool writableConfig = false)
        => Load("sbox-ns@.service")
            .Replace("@BINARY@", Quote(binary))
            .Replace("@USER@", user)
            .Replace("@GROUP@", Group(user))
            .Replace("@READ_WRITE_PATHS@", "/var/lib/sbox-ns/%i" + (writableConfig ? " /etc/sbox-ns/%i" : string.Empty));

    public static string UpdateService(string binary, string arguments)
        => Load("sbox-ns-update.service").Replace("@BINARY@", Quote(binary)).Replace("@ARGUMENTS@", arguments);

    public static string UpdateTimerUnit => Load("sbox-ns-update.timer");

    public static bool NeedsBindCapability(EffectiveConfig config)
    {
        static bool LowPort(string address) => ListenAddress.TryParse(address, out var listen) && listen.Port < 1024;
        return LowPort(config.GetString("server.listen"))
            || (config.GetString("tls.mode") != "off" && LowPort(config.GetString("tls.https_listen")));
    }

    /// <summary>Updates only our managed drop-in; operator drop-ins are left alone.</summary>
    public static bool SyncBindDropIn(string unit, EffectiveConfig config, string unitDirectory = "/etc/systemd/system")
    {
        var directory = Path.Combine(unitDirectory, unit + ".service.d");
        var path = Path.Combine(directory, "10-sbox-ns-bind.conf");
        if (!NeedsBindCapability(config))
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        const string text = "[Service]\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\nAmbientCapabilities=CAP_NET_BIND_SERVICE\n";
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        Directory.CreateDirectory(directory);
        ConfigFiles.WriteAtomically(path, text);
        return true;
    }

    private static string Group(string user)
    {
        var result = ServiceCommands.RunCapture("id", ["-gn", user]);
        return result.ExitCode == 0 ? result.Output.Trim() : user;
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%");
        return value.Any(char.IsWhiteSpace) || value.Contains('"') ? "\"" + escaped + "\"" : escaped;
    }

    private static string Load(string name)
    {
        using var stream = typeof(SystemdUnits).Assembly.GetManifestResourceStream("SboxNetworkStorage.Server.Systemd." + name)
            ?? throw new InvalidOperationException($"Missing embedded systemd unit {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
