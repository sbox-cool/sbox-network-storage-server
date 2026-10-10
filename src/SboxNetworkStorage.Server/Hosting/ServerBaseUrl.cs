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

    /// <summary>First routable IPv4 address of an up interface (a VPS's public address), else the host name.</summary>
    public static string DetectHostAddress()
    {
        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
                    && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (address is not null) return address.ToString();
        }
        catch (NetworkInformationException)
        {
        }
        return Dns.GetHostName();
    }

    private static string FormatHost(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
