using System.Globalization;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed class TunnelManager(TunnelRegistryClient registry, CloudflaredInstaller installer)
{
    public const string ManagedFile = "zzzz-tunnel.toml";
    public static string IdentityPath(EffectiveConfig config) => Path.Combine(config.SecretsDirectory, StateLayout.TunnelIdentityFile);
    public static string TokenPath(EffectiveConfig config) => Path.Combine(config.SecretsDirectory, StateLayout.TunnelTokenFile);

    public async Task EnableAsync(EffectiveConfig config, CancellationToken ct)
    {
        using var identityScope = RuntimeIdentity.Enter(config);
        using var lease = Acquire(config);
        foreach (var value in config.Values.Values)
            if (value.Definition.Key is "server.listen" or "server.public_url" or "tls.mode"
                && value.Source == SettingSource.Flag)
                throw new InvalidOperationException("Remove listener/public URL flags before changing tunnel state.");
        config = Reload(config);
        if (config.GetBoolean("dns.enabled"))
            throw new InvalidOperationException("A hosted DNS name is enabled. Run `sbox-ns dns disable` first; tunnel and DNS names cannot be used together.");
        EnsureWritable(config);
        var enabled = config.GetBoolean("tunnel.enabled");
        if (enabled && !File.Exists(IdentityPath(config)))
            throw new InvalidOperationException("The original tunnel identity key is missing; restore it before re-enabling.");
        using var identity = TunnelIdentity.LoadOrCreate(IdentityPath(config));
        if (enabled && identity.Name != config.GetString("tunnel.name"))
            throw new InvalidOperationException("The identity key does not match the configured tunnel.");
        var port = enabled ? checked((int)config.GetInteger("tunnel.local_port")) : ListenPort(config);
        var registration = await registry.RegisterAsync(config.GetString("tunnel.registry"), identity.Sign("register", port), ct);
        var url = TunnelRegistryClient.PublicUrl(registration.Hostname, identity.Name);
        // Download and checksum complete before either token or effective configuration is changed.
        await installer.InstallAsync(config.ExecutablesDirectory, ct);
        TunnelFiles.WriteSecret(TokenPath(config), registration.TunnelToken);
        var values = new Dictionary<string, object>
        {
            ["server.listen"] = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            ["server.public_url"] = url,
            ["tls.mode"] = "off",
            ["tunnel.enabled"] = true,
            ["tunnel.name"] = identity.Name,
            ["tunnel.hostname"] = new Uri(url).Host,
            ["tunnel.registry"] = config.GetString("tunnel.registry"),
            ["tunnel.local_port"] = (long)port,
            ["tunnel.previous_listen"] = enabled ? config.GetString("tunnel.previous_listen") : config.GetString("server.listen"),
            ["tunnel.previous_public_url"] = enabled ? config.GetString("tunnel.previous_public_url") : config.GetString("server.public_url"),
            ["tunnel.previous_tls_mode"] = enabled ? config.GetString("tunnel.previous_tls_mode") : config.GetString("tls.mode")
        };
        WriteOverlay(config, values);
    }

    public async Task DisableAsync(EffectiveConfig config, CancellationToken ct)
    {
        using var identityScope = RuntimeIdentity.Enter(config);
        using var lease = Acquire(config);
        config = Reload(config);
        if (!config.GetBoolean("tunnel.enabled"))
        {
            File.Delete(TokenPath(config));
            return;
        }
        EnsureWritable(config);
        if (!File.Exists(IdentityPath(config)))
            throw new InvalidOperationException("The original tunnel identity key is missing; restore it before disabling.");
        using var identity = TunnelIdentity.LoadOrCreate(IdentityPath(config));
        if (identity.Name != config.GetString("tunnel.name"))
            throw new InvalidOperationException("The identity key does not match the configured tunnel.");
        await registry.DeleteAsync(config.GetString("tunnel.registry"),
            identity.Sign("delete", checked((int)config.GetInteger("tunnel.local_port"))), ct);
        WriteOverlay(config, new Dictionary<string, object>
        {
            ["server.listen"] = config.GetString("tunnel.previous_listen"),
            ["server.public_url"] = config.GetString("tunnel.previous_public_url"),
            ["tls.mode"] = config.GetString("tunnel.previous_tls_mode"),
            ["tunnel.enabled"] = false,
            ["tunnel.name"] = "",
            ["tunnel.hostname"] = "",
            ["tunnel.registry"] = config.GetString("tunnel.registry"),
            ["tunnel.local_port"] = 8080L,
            ["tunnel.previous_listen"] = "",
            ["tunnel.previous_public_url"] = "",
            ["tunnel.previous_tls_mode"] = "off"
        });
        File.Delete(TokenPath(config));
    }

    private static EffectiveConfig Reload(EffectiveConfig config)
    {
        var fresh = ConfigLoader.Load(config.ConfigDirectory, config.DataDirectory);
        if (!fresh.IsValid) throw new InvalidOperationException("Tunnel configuration is invalid: " + string.Join("; ", fresh.Issues));
        return fresh;
    }

    private static int ListenPort(EffectiveConfig config)
        => ListenAddress.TryParse(config.GetString("server.listen"), out var listen) ? listen.Port
            : throw new InvalidOperationException("Invalid server.listen.");

    private static void EnsureWritable(EffectiveConfig config)
    {
        foreach (var key in new[] { "server.listen", "server.public_url", "tls.mode" }.Concat(
                     SettingDefinitions.All.Where(d => d.Key.StartsWith("tunnel.", StringComparison.Ordinal) && d.Key != "tunnel.registry").Select(d => d.Key)))
        {
            var value = config.Values[key];
            if (value.Source is SettingSource.Environment or SettingSource.Flag
                || value.Source == SettingSource.ConfD && value.Origin is { } origin
                && string.CompareOrdinal(Path.GetFileName(origin[..origin.LastIndexOf(':')]), ManagedFile) > 0)
                throw new InvalidOperationException($"Remove the overriding {key} setting before changing tunnel state.");
        }
    }

    private static FileStream Acquire(EffectiveConfig config)
    {
        config.EnsureRuntimeDirectory();
        try { return new FileStream(Path.Combine(config.RuntimeDirectory, StateLayout.TunnelLockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another tunnel operation is in progress."); }
    }

    private static void WriteOverlay(EffectiveConfig config, IReadOnlyDictionary<string, object> values)
        => ManagedOverlay.Write(config, ManagedFile, "Managed atomically by sbox-ns tunnel. Use tunnel enable/disable to change lifecycle.", values);
}
