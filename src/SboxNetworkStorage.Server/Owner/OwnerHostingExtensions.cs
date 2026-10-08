using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Owner;

public static class OwnerHostingExtensions
{
    public const string Scheme = "LocalOwner";
    public const string StampClaim = "owner_stamp";
    // Logical names; over HTTPS OwnerHostCookieMiddleware sends them as __Host- cookies. Never set a cookie Domain.
    public const string AuthCookieName = "sbox-ns-owner";
    public const string CsrfCookieName = "sbox-ns-csrf";

    public static IServiceCollection AddOwnerManagement(this IServiceCollection services, EffectiveConfig config)
    {
        services.AddScoped<OwnerAccountService>();
        services.AddScoped<OwnerLoginLinkService>();
        services.AddSingleton<OwnerSetupToken>();
        var keys = Directory.CreateDirectory(Path.Combine(config.DataDirectory, "owner-cookie-keys"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        services.AddDataProtection().SetApplicationName("sbox-ns-owner").PersistKeysToFileSystem(keys);
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = AuthCookieName;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            // Lax, not Strict: owners follow links to the panel from Discord,
            // email alerts and bookmarks, and Strict drops the session on every
            // cross-site arrival. Lax still withholds the cookie from cross-site
            // POSTs, iframes and subresources, every state-changing route is a
            // POST behind the global antiforgery filter, and no GET mutates.
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                var owner = await context.HttpContext.RequestServices.GetRequiredService<OwnerAccountService>()
                    .GetAsync(context.HttpContext.RequestAborted);
                if (owner is null || context.Principal?.FindFirstValue(StampClaim) != owner.SecurityStamp)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(Scheme);
                }
            };
        });
        services.AddAuthorization();
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = CsrfCookieName;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("owner-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        services.AddHostedService<OwnerSetupNotice>();
        return services;
    }

    // Call after UseRouting and before endpoint execution.
    public static WebApplication UseOwnerManagement(this WebApplication app)
    {
        app.UseMiddleware<OwnerHostCookieMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/dashboard") || context.Request.Path.StartsWithSegments("/login") || context.Request.Path == "/setup")
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            }
            await next(context);
        });
        app.MapControllers();
        app.MapGet("/owner-assets/management.css", () => Results.Stream(
            typeof(OwnerHostingExtensions).Assembly.GetManifestResourceStream("SboxNetworkStorage.Server.Owner.management.css")!, "text/css"));
        return app;
    }
}

public sealed class OwnerSetupNotice(IServiceScopeFactory scopes, OwnerSetupToken token, EffectiveConfig config,
    ILogger<OwnerSetupNotice> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<OwnerAccountService>().GetAsync(ct) is not null) return;
        var tls = config.GetString("tls.mode") != "off";
        ListenAddress.TryParse(config.GetString(tls ? "tls.https_listen" : "server.listen"), out var listen);
        var setupUrl = $"{(tls ? "https" : "http")}://localhost:{listen.Port}/setup?token={token.Value}";
        logger.LogWarning("No owner account exists. Open this local one-time setup URL within two hours: {SetupUrl}. For remote servers run `sbox-ns admin login-link` on the server and open the printed link, or use an SSH tunnel. With TLS use a hostname matching the certificate routed to loopback. Restart to rotate an expired token.", setupUrl);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
