using System.Diagnostics;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed record TunnelStatus(bool Enabled, string Name, string Hostname, string Registry, string ConnectorState,
    string ConnectorVersion, int? ProcessId, DateTimeOffset UpdatedAt);

public sealed class TunnelConnectorState(EffectiveConfig config)
{
    private TunnelStatus _current = Describe(config, "stopped", null);
    public TunnelStatus Current => Volatile.Read(ref _current);
    public static string StatePath(EffectiveConfig config) => Path.Combine(config.DataDirectory, "tunnel-connector.json");
    public static TunnelStatus Describe(EffectiveConfig config, string state, int? pid)
        => new(config.GetBoolean("tunnel.enabled"), config.GetString("tunnel.name"), config.GetString("tunnel.hostname"),
            config.GetString("tunnel.registry"), state, CloudflaredInstaller.Version, pid, DateTimeOffset.UtcNow);

    public void Set(EffectiveConfig config, string state, int? pid)
    {
        var status = Describe(config, state, pid);
        Volatile.Write(ref _current, status);
        ConfigFiles.WriteAtomically(StatePath(config), JsonSerializer.Serialize(status));
    }

    public static TunnelStatus Read(EffectiveConfig config)
    {
        var fallback = Describe(config, config.GetBoolean("tunnel.enabled") ? "stopped (restart required)" : "disabled", null);
        try
        {
            var saved = JsonSerializer.Deserialize<TunnelStatus>(File.ReadAllText(StatePath(config)));
            if (saved is null || saved.Name != fallback.Name || saved.Enabled != fallback.Enabled
                || DateTimeOffset.UtcNow - saved.UpdatedAt > TimeSpan.FromSeconds(15)) return fallback;
            if (saved.ProcessId is { } pid)
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return fallback;
            }
            return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
        { return fallback; }
    }
}
