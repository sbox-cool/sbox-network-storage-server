using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>
/// Which reverse proxies may set <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>. Loopback is always trusted;
/// <c>server.trusted_proxies</c> adds addresses or networks, and <c>server.trusted_proxy_hops</c> caps how many
/// forwarding entries are consumed, nearest proxy first.
/// </summary>
public static class TrustedProxies
{
    public const int MinHops = 1;
    public const int MaxHops = 10;

    /// <summary>Splits the comma-separated <c>server.trusted_proxies</c> value.</summary>
    public static IEnumerable<string> Entries(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Parses one IP address or CIDR entry. A bare address is a single host. <c>/0</c> is rejected because it would trust every client.</summary>
    public static bool TryParseEntry(string entry, out IPNetwork network)
    {
        var text = entry.Contains('/') ? entry : entry + (entry.Contains(':') ? "/128" : "/32");
        return IPNetwork.TryParse(text, out network) && network.PrefixLength > 0;
    }

    /// <summary>Configures forwarded-header trust from the effective configuration. Call once at host build.</summary>
    public static void Apply(ForwardedHeadersOptions options, EffectiveConfig config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        // Loopback is always trusted: a TLS proxy or tunnel connector on this machine.
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach (var entry in Entries(config.GetString("server.trusted_proxies")))
        {
            if (!TryParseEntry(entry, out var network))
                throw new InvalidOperationException($"Invalid server.trusted_proxies entry '{entry}'.");
            options.KnownNetworks.Add(network);
            // A dual-stack listener can report an IPv4 peer as IPv4-mapped IPv6, so trust that form too.
            if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
                options.KnownNetworks.Add(new IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96));
        }
        options.ForwardLimit = (int)config.GetInteger("server.trusted_proxy_hops");
    }
}
