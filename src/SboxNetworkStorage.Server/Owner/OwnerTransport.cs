using System.Net;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Transport facts about an owner-panel request (after forwarded-header processing).</summary>
public static class OwnerTransport
{
    public const string HttpsDocsUrl = OwnerDocs.Base + "admin-panel.md#http-vs-https";

    public static bool IsLoopback(HttpContext context)
        => context.Connection.RemoteIpAddress is { } address
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>True when credentials and session cookies would cross the network unencrypted.</summary>
    public static bool IsInsecureRemote(HttpContext context) => !context.Request.IsHttps && !IsLoopback(context);

    /// <summary>
    /// True when owner credentials (password, login link) must not be accepted on this request: plain HTTP from another
    /// machine, unless the operator opted in with <c>adminpanel.allow_insecure_http</c>. A TLS proxy on loopback that
    /// sends <c>X-Forwarded-Proto: https</c> counts as HTTPS.
    /// </summary>
    public static bool RefusesCredentials(HttpContext context)
        => IsInsecureRemote(context)
            && !context.RequestServices.GetRequiredService<EffectiveConfig>().GetBoolean("adminpanel.allow_insecure_http");
}
