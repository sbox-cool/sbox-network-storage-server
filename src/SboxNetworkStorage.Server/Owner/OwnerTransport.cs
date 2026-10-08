using System.Net;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Transport facts about an owner-panel request (after forwarded-header processing).</summary>
public static class OwnerTransport
{
    public const string HttpsDocsUrl = "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/admin-panel.md#http-vs-https";

    public static bool IsLoopback(HttpContext context)
        => context.Connection.RemoteIpAddress is { } address
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>True when credentials and session cookies would cross the network unencrypted.</summary>
    public static bool IsInsecureRemote(HttpContext context) => !context.Request.IsHttps && !IsLoopback(context);
}
