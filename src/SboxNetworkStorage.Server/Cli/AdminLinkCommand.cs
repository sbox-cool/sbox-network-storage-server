using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns admin login-link</c>: mint a single-use owner login link on the server.</summary>
public static class AdminLinkCommand
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var minutes = ParseMinutes(context.Args.Option("minutes"));
        var config = context.LoadValidConfig();
        await using var services = CliServices.Build(config);
        await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(CancellationToken.None);
        var store = services.GetRequiredService<INetworkStorageStore>();
        var owner = await new OwnerAccountService(store, config).GetAsync(CancellationToken.None);
        var (token, expiresAt) = await new OwnerLoginLinkService(store).CreateAsync(minutes, CancellationToken.None);
        var baseUrl = BaseUrl(config, DetectHostAddress);
        Console.WriteLine($"{baseUrl}/login/link?token={token}");
        Console.WriteLine(owner is null
            ? "Opens owner creation (no owner exists yet)."
            : $"Signs in as owner '{owner.Username}'.");
        Console.WriteLine($"Single use; expires {expiresAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC ({minutes} min). Opening the page does not use it up; confirming does.");
        if (baseUrl.StartsWith("http://", StringComparison.Ordinal) && !IsLoopbackUrl(baseUrl))
            Console.Error.WriteLine("warning: this link uses plain HTTP, so it and your session can be intercepted on the network. Prefer HTTPS (server.public_url / tls.mode) or an SSH tunnel; see docs/admin-panel.md.");
        return CliApp.Ok;
    }

    public static int ParseMinutes(string? value)
    {
        if (value is null) return OwnerLoginLinkService.DefaultMinutes;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 1 or > OwnerLoginLinkService.MaxMinutes)
            throw new CliException($"--minutes must be a whole number from 1 to {OwnerLoginLinkService.MaxMinutes}.", CliApp.Usage);
        return minutes;
    }

    /// <summary>
    /// server.public_url when set; otherwise the scheme/port of the active listener with the ACME domain,
    /// the bound address, or (for wildcard binds) <paramref name="detectHost"/>'s best guess at this machine's address.
    /// </summary>
    public static string BaseUrl(EffectiveConfig config, Func<string> detectHost)
    {
        var publicUrl = config.GetString("server.public_url").Trim();
        if (publicUrl.Length > 0) return publicUrl.TrimEnd('/');
        var tlsMode = config.GetString("tls.mode");
        var tls = tlsMode != "off";
        ListenAddress.TryParse(config.GetString(tls ? "tls.https_listen" : "server.listen"), out var listen);
        var host = tlsMode == "acme" && config.GetString("tls.acme_domain") is { Length: > 0 } domain ? domain
            : listen.IsLocalhost ? "localhost"
            : listen.Address is { } address && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) ? FormatHost(address)
            : detectHost();
        var scheme = tls ? "https" : "http";
        var defaultPort = tls ? 443 : 80;
        return listen.Port == defaultPort || listen.Port == 0 ? $"{scheme}://{host}" : $"{scheme}://{host}:{listen.Port}";
    }

    private static string FormatHost(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static bool IsLoopbackUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    /// <summary>First routable IPv4 address of an up interface (a VPS's public address), else the host name.</summary>
    private static string DetectHostAddress()
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
}
