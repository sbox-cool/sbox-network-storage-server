using SboxNetworkStorage.Server.SignedDns;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Where an instance keeps what the server and CLI write at runtime.</summary>
public enum ConfigLayout
{
    /// <summary>Runtime files live beside the operator files in the config folder (installs before the split).</summary>
    Legacy,

    /// <summary>Runtime files live in <c>&lt;data&gt;/state</c>; the config folder only holds operator files.</summary>
    State
}

/// <summary>What <c>doctor</c> reports about an instance's layout.</summary>
public enum LayoutHealth
{
    /// <summary>The marker exists.</summary>
    Current,

    /// <summary>No marker; runtime files are still in the config folder.</summary>
    Legacy,

    /// <summary>No marker and nothing to migrate.</summary>
    Fresh,

    /// <summary>No marker, and runtime files exist in both the config folder and the state folder.</summary>
    Partial
}

/// <summary>A runtime file (or folder) and the two places it can live.</summary>
/// <param name="LegacyPath">Location in the legacy layout (config folder, or the data folder for the telemetry ID).</param>
/// <param name="StatePath">Location in the state layout.</param>
/// <param name="InConfigFolder">True when <paramref name="LegacyPath"/> is inside the config folder; only these decide the layout.</param>
public sealed record RuntimeFile(string LegacyPath, string StatePath, bool InConfigFolder = true);

/// <summary>
/// The split between operator configuration (config folder, read-only to the service) and
/// runtime-written state (<c>&lt;data&gt;/state</c>). The layout is explicit: the
/// <see cref="MarkerFile"/> in the state folder records a completed migration.
/// </summary>
public static class StateLayout
{
    public const string FolderName = "state";
    public const string MarkerFile = ".layout-version";
    public const string CurrentVersion = "1";
    public const string SecretsFolder = "secrets";
    public const string ExecutablesFolder = "bin";
    public const string LegacyExecutablesFolder = "connectors";
    public const string TunnelLockFile = ".tunnel.lock";
    public const string DnsLockFile = ".dns.lock";
    public const string TunnelIdentityFile = "identity_ecdsa_p256.pem";
    public const string TunnelTokenFile = "tunnel_token";
    public const string TelemetryOverlayFile = "telemetry.toml";

    public const string MigrationNotice =
        "This install still keeps runtime files (secrets, tunnel and DNS state) in the config folder. Run `sudo sbox-ns layout migrate` to move them to the state folder and make the config folder read-only to the service.";

    private static readonly string[] AuthFileKeys = ["auth.session_secret_file", "auth.storage_encryption_key_file", "auth.security_signing_key_file"];

    /// <summary>Overlay keys outside the <c>tunnel.*</c> and <c>dns.*</c> tables that the managed overlays write.</summary>
    private static readonly HashSet<string> AllowedOverlayKeys = new(StringComparer.Ordinal)
    {
        "server.listen", "server.public_url",
        "tls.mode", "tls.acme_domain", "tls.acme_email", "tls.acme_accept_terms",
        "telemetry.enabled"
    };

    public static string StateDirectory(string dataDirectory) => Path.Combine(dataDirectory, FolderName);

    public static string MarkerPath(string dataDirectory) => Path.Combine(StateDirectory(dataDirectory), MarkerFile);

    public static bool HasMarker(string dataDirectory) => File.Exists(MarkerPath(dataDirectory));

    /// <summary>True for the keys a state overlay file may set.</summary>
    public static bool IsAllowedOverlayKey(string key)
        => AllowedOverlayKeys.Contains(key)
            || key.StartsWith("tunnel.", StringComparison.Ordinal)
            || key.StartsWith("dns.", StringComparison.Ordinal);

    /// <summary>The overlay keys in documentation order.</summary>
    public static IReadOnlyList<string> OverlayKeyPatterns { get; } = [.. AllowedOverlayKeys.Order(StringComparer.Ordinal), "tunnel.*", "dns.*"];

    /// <summary>The file names the server and CLI manage in the overlay folder.</summary>
    public static IReadOnlyList<string> ManagedOverlayFiles { get; } = [TunnelManager.ManagedFile, DnsManager.ManagedFile, TelemetryOverlayFile];

    /// <summary>
    /// Every runtime file of an instance with its legacy and state location. <paramref name="authFileValues"/> are the
    /// three <c>auth.*_file</c> settings; only the ones that resolve inside the config folder move.
    /// </summary>
    public static IReadOnlyList<RuntimeFile> RuntimeFiles(string configDirectory, string dataDirectory, IEnumerable<string> authFileValues)
    {
        var state = StateDirectory(dataDirectory);
        var files = new List<RuntimeFile>();
        foreach (var value in authFileValues.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var full = Path.GetFullPath(value, configDirectory);
            if (IsInside(configDirectory, full))
            {
                files.Add(new RuntimeFile(full, Path.Combine(state, Path.GetRelativePath(configDirectory, full))));
            }
        }

        foreach (var name in new[] { TunnelIdentityFile, TunnelTokenFile })
        {
            files.Add(new RuntimeFile(Path.Combine(configDirectory, SecretsFolder, name), Path.Combine(state, SecretsFolder, name)));
        }

        files.Add(new RuntimeFile(Path.Combine(configDirectory, LegacyExecutablesFolder), Path.Combine(state, ExecutablesFolder)));
        files.Add(new RuntimeFile(Path.Combine(configDirectory, TunnelLockFile), Path.Combine(state, TunnelLockFile)));
        files.Add(new RuntimeFile(Path.Combine(configDirectory, DnsLockFile), Path.Combine(state, DnsLockFile)));
        foreach (var name in new[] { TunnelManager.ManagedFile, DnsManager.ManagedFile })
        {
            files.Add(new RuntimeFile(Path.Combine(configDirectory, ConfigLoader.ConfDirectory, name), Path.Combine(state, ConfigLoader.ConfDirectory, name)));
        }

        files.Add(new RuntimeFile(Path.Combine(dataDirectory, UsageTelemetry.IdFileName), Path.Combine(state, UsageTelemetry.IdFileName), InConfigFolder: false));
        return files.DistinctBy(f => f.LegacyPath, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<RuntimeFile> RuntimeFiles(EffectiveConfig config)
        => RuntimeFiles(config.ConfigDirectory, config.DataDirectory, AuthFileKeys.Select(config.GetString));

    /// <summary>
    /// Marker present: <see cref="ConfigLayout.State"/>. No marker and runtime files in the config folder:
    /// <see cref="ConfigLayout.Legacy"/>. No marker and none (a fresh install): <see cref="ConfigLayout.State"/>.
    /// </summary>
    public static ConfigLayout Resolve(string configDirectory, string dataDirectory, IEnumerable<string> authFileValues)
        => HasMarker(dataDirectory) || !RuntimeFiles(configDirectory, dataDirectory, authFileValues).Any(f => f.InConfigFolder && Exists(f.LegacyPath))
            ? ConfigLayout.State
            : ConfigLayout.Legacy;

    public static LayoutHealth Health(EffectiveConfig config)
    {
        if (HasMarker(config.DataDirectory))
        {
            return LayoutHealth.Current;
        }

        var files = RuntimeFiles(config);
        if (files.Any(f => Exists(f.LegacyPath) && Exists(f.StatePath)))
        {
            return LayoutHealth.Partial;
        }

        return files.Any(f => f.InConfigFolder && Exists(f.LegacyPath)) ? LayoutHealth.Legacy : LayoutHealth.Fresh;
    }

    /// <summary>One line for <c>doctor</c>: the health and what to do about it.</summary>
    public static (LayoutHealth Health, string Message) Describe(EffectiveConfig config)
    {
        var health = Health(config);
        return (health, health switch
        {
            LayoutHealth.Current => $"state folder {config.StateDirectory}; config folder holds operator files only",
            LayoutHealth.Legacy => MigrationNotice,
            LayoutHealth.Partial => $"partial: runtime files exist in both {config.ConfigDirectory} and {config.StateDirectory} and there is no layout marker (an interrupted `layout migrate`). "
                + "Keep the current copy of each file, remove the other, then run `sudo sbox-ns layout migrate` again; do not start the service until then.",
            _ => $"state folder {config.StateDirectory} (nothing to migrate)"
        });
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static bool IsInside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
