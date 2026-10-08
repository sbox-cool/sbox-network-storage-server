using System.Globalization;
using System.Net;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>A parsed <c>host:port</c> listen address (<c>0.0.0.0:8080</c>, <c>[::]:8080</c>, <c>localhost:8080</c>).</summary>
public sealed record ListenAddress(IPAddress? Address, bool IsLocalhost, int Port)
{
    public static bool TryParse(string? text, out ListenAddress result)
    {
        result = new ListenAddress(IPAddress.Any, false, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        var host = value[..separator].Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            result = new ListenAddress(null, true, port);
            return true;
        }

        if (host is "*" or "+")
        {
            result = new ListenAddress(IPAddress.Any, false, port);
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        result = new ListenAddress(address, false, port);
        return true;
    }
}
