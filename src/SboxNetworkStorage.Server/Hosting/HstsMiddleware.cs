using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>
/// Sends Strict-Transport-Security on HTTPS responses when the server
/// terminates TLS itself. Plain-HTTP installs and disabled <c>tls.mode</c>
/// never send it, so HTTP fallback cannot be bricked. No includeSubDomains:
/// tunnel names and bare IPs must not force HTTPS on sibling names.
/// Registered right after forwarded-header processing so proxied TLS counts.
/// </summary>
public sealed class HstsMiddleware(RequestDelegate next, EffectiveConfig config)
{
    public const string HeaderValue = "max-age=15552000";
    public async Task InvokeAsync(HttpContext context)
    {
        // Runs before routing, so the response has never started here.
        if (!context.Response.HasStarted && config.GetBoolean("tls.hsts")
            && config.GetString("tls.mode") != "off" && context.Request.IsHttps)
            context.Response.Headers.StrictTransportSecurity = HeaderValue;
        await next(context);
    }
}
