using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Routing;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.SignedDns;

/// <summary>
/// Ownership proof for the sboxns.com DNS registry: signs the domain-separated
/// <c>sbox-ns-dns-proof-v1</c> payload for a registry-chosen nonce with the existing identity key.
/// </summary>
public static partial class DnsProofEndpoints
{
    public const string Path = "/.well-known/sbox-ns/dns-proof/{nonce}";
    private const string RateLimitPolicy = "dns-proof";

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();

    public static IServiceCollection AddDnsProof(this IServiceCollection services)
        => services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));

    public static IEndpointRouteBuilder MapDnsProof(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (string nonce, EffectiveConfig config, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!NoncePattern().IsMatch(nonce)) return Results.NotFound();
                // Never create a key from an HTTP request: no identity means nothing to prove.
                using var identity = TunnelIdentity.LoadExisting(TunnelManager.IdentityPath(config));
                return identity is null ? Results.NotFound() : Results.Json(new { signature = identity.SignDnsProof(nonce) });
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithRouteOwner(RouteOwner.DotNetNative, "Signed DNS ownership proof for the sboxns.com registry");
        return endpoints;
    }
}
