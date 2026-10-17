using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>
/// The base URL games and owners use to reach this server, shared by the CLI (setup, quickstart,
/// admin login-link) and the owner dashboard so they never disagree.
/// </summary>
public static class ServerBaseUrl
{
    /// <summary>Host shown when the server listens on every address and the CLI should not guess.</summary>
    public const string HostPlaceholder = "<this-host>";

    /// <summary><c>server.public_url</c> without a trailing slash, or null when it is not set.</summary>
    public static string? PublicUrl(EffectiveConfig config)
        => config.GetString("server.public_url").Trim() is { Length: > 0 } url ? url.TrimEnd('/') : null;

    /// <summary>
    /// server.public_url when set; otherwise the scheme/port of the active listener with the ACME domain,
    /// the bound address, or (for wildcard binds) <paramref name="wildcardHost"/>.
    /// </summary>
    public static string FromConfig(EffectiveConfig config, Func<string> wildcardHost)
    {
        if (PublicUrl(config) is { } publicUrl) return publicUrl;
        var tlsMode = config.GetString("tls.mode");
        var tls = tlsMode != "off";
        ListenAddress.TryParse(config.GetString(tls ? "tls.https_listen" : "server.listen"), out var listen);
        var host = tlsMode == "acme" && config.GetString("tls.acme_domain") is { Length: > 0 } domain ? domain
            : listen.IsLocalhost ? "localhost"
            : listen.Address is { } address && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) ? FormatHost(address)
            : wildcardHost();
        var scheme = tls ? "https" : "http";
        var defaultPort = tls ? 443 : 80;
        return listen.Port == defaultPort || listen.Port == 0 ? $"{scheme}://{host}" : $"{scheme}://{host}:{listen.Port}";
    }

    /// <summary>
    /// server.public_url when set; otherwise the origin the browser used to open the dashboard, which is
    /// also how the game reaches the server unless a proxy or tunnel sits in between.
    /// </summary>
    public static string ForRequest(EffectiveConfig config, HttpRequest request)
        => PublicUrl(config) ?? $"{request.Scheme}://{request.Host.Value}";

    public static bool IsLoopback(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    private static readonly int[] GameLoopbackPorts = [80, 443, 8080, 8443];

    /// <summary>
    /// Why s&amp;box game code would refuse requests to this base URL, or null. Matches <c>Http.IsAllowed</c> in the
    /// s&amp;box 26.10.13 editor: hosts that are IP addresses are refused, and loopback is allowed only on ports 80,
    /// 443, 8080 and 8443. The editor Sync Tool is not restricted, so a refused URL still syncs.
    /// </summary>
    public static string? GameClientBlockReason(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (IsLoopback(url))
        {
            return GameLoopbackPorts.Contains(uri.Port)
                ? null
                : $"s&box game code reaches this machine only on ports 80, 443, 8080 or 8443, so requests to port {uri.Port} fail with \"Access to '...' is not allowed\" (the editor Sync Tool still works). Listen on one of those ports.";
        }

        return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? "s&box game code refuses IP-address hosts. Give the server a domain name (DNS record, sbox-ns tunnel enable or sbox-ns dns enable) and use it in server.public_url."
            : null;
    }

    /// <summary>First routable address of an up interface (a VPS's public address), else the host name.
    /// IPv4 first; on IPv6-only machines the first global IPv6 address, URL-ready in brackets.
    /// Without brackets a URL like <c>http://2001:db8::1:8080</c> does not parse, so callers must
    /// never strip them.</summary>
    public static string DetectHostAddress()
    {
        try
        {
            var unicasts = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .ToList();
            var v4 = unicasts.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
                && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (v4 is not null) return v4.ToString();
            var v6 = unicasts.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(ip)
                && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast).ToList();
            var global = v6.FirstOrDefault(ip => ip.GetAddressBytes()[0] is >= 0x20 and <= 0x3F);
            if ((global ?? v6.FirstOrDefault()) is { } v6pick) return $"[{v6pick}]";
        }
        catch (NetworkInformationException)
        {
        }
        return Dns.GetHostName();
    }

    private static string FormatHost(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
