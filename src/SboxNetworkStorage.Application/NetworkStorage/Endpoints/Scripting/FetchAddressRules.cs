using System.Net;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Destination rules for <c>ns.fetch</c>, applied to every resolved address on every hop. Names are never trusted: a
/// host whose DNS answer includes any blocked address is refused.
/// </summary>
/// <remarks>
/// Three tiers. Cloud metadata addresses are always blocked. Reserved and special-purpose ranges are always blocked.
/// Private, loopback, link-local and carrier-grade NAT ranges are blocked unless the operator sets
/// <c>endpoints.fetch_private_networks</c>. IPv4 embedded in IPv4-mapped, NAT64 and 6to4 addresses is checked as IPv4.
/// </remarks>
public static class FetchAddressRules
{
    /// <summary>Reason code for a cloud metadata address. Always blocked.</summary>
    public const string MetadataAddress = "metadata_address";

    /// <summary>Reason code for a reserved or special-purpose address. Always blocked.</summary>
    public const string ReservedAddress = "reserved_address";

    /// <summary>Reason code for a private, loopback or link-local address. Blocked unless the operator allows private networks.</summary>
    public const string PrivateAddress = "private_address";

    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("169.254.169.254"),
        IPAddress.Parse("169.254.170.2"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    private static readonly IPNetwork[] ReservedNetworks = ParseAll(
        "0.0.0.0/8",
        "192.0.0.0/24",
        "192.0.2.0/24",
        "198.18.0.0/15",
        "198.51.100.0/24",
        "203.0.113.0/24",
        "224.0.0.0/4",
        "240.0.0.0/4",
        "::/96",
        "64:ff9b:1::/48",
        "100::/64",
        "2001:db8::/32",
        "ff00::/8");

    private static readonly IPNetwork[] PrivateNetworks = ParseAll(
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "::1/128",
        "fc00::/7",
        "fe80::/10",
        "fec0::/10");

    private static readonly IPNetwork Nat64Prefix = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFourPrefix = IPNetwork.Parse("2002::/16");

    /// <summary>
    /// The reason an address may not be contacted, or null when it may be. Private ranges are allowed only when
    /// <paramref name="allowPrivateNetworks"/> is true. Metadata and reserved ranges are never allowed.
    /// </summary>
    public static string? BlockedReason(IPAddress address, bool allowPrivateNetworks)
    {
        if (address.IsIPv4MappedToIPv6)
            return BlockedReason(address.MapToIPv4(), allowPrivateNetworks);

        if (MetadataAddresses.Any(metadata => metadata.Equals(address)))
            return MetadataAddress;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (Nat64Prefix.Contains(address))
                return BlockedReason(EmbeddedIpv4(address, 12), allowPrivateNetworks);
            if (SixToFourPrefix.Contains(address))
                return BlockedReason(EmbeddedIpv4(address, 2), allowPrivateNetworks);
        }

        if (PrivateNetworks.Any(network => network.Contains(address)))
            return allowPrivateNetworks ? null : PrivateAddress;

        if (ReservedNetworks.Any(network => network.Contains(address)))
            return ReservedAddress;

        return null;
    }

    private static IPAddress EmbeddedIpv4(IPAddress address, int offset)
    {
        var bytes = address.GetAddressBytes();
        return new IPAddress(bytes[offset..(offset + 4)]);
    }

    private static IPNetwork[] ParseAll(params string[] cidrs) => cidrs.Select(cidr => IPNetwork.Parse(cidr)).ToArray();
}
