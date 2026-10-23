using System.Net;
using System.Net.Sockets;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// SSRF guard for owner-saved webhook URLs (Discord and generic webhooks).
/// Saving refuses URLs that point at this server itself: loopback, link-local
/// (including the 169.254.169.254 cloud metadata address), unspecified
/// addresses and <c>localhost</c>. RFC 1918 private addresses stay allowed for
/// self-hosters reaching their own network; public DNS names are allowed (they
/// cannot be verified without resolving, and sends never follow redirects).
/// </summary>
public static class AlertWebhookUrlPolicy
{
    /// <summary>Why <paramref name="uri"/> must not be saved, or null when it may.</summary>
    public static string? UnsafeReason(Uri uri)
    {
        var host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return "The webhook host must not be localhost.";
        if (!IPAddress.TryParse(host, out var address))
            return null;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return "The webhook host must not be an unspecified address.";
        if (IPAddress.IsLoopback(address))
            return "The webhook host must not be a loopback address.";
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
                return "The webhook host must not be a link-local address (including cloud metadata).";
            return null;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
            return "The webhook host must not be a link-local address.";
        return null;
    }
}
