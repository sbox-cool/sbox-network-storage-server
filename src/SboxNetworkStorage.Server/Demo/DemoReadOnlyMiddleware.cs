using Microsoft.AspNetCore.Mvc.Controllers;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;

namespace SboxNetworkStorage.Server.Demo;

/// <summary>Demo access is a server-enforced read-only allowlist, never a login bypass on normal installs.</summary>
public sealed class DemoReadOnlyMiddleware(RequestDelegate next, EffectiveConfig config)
{
    public const string ContextKey = "sbox-ns-demo-read-only";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!config.GetBoolean("adminpanel.demo_read_only")) { await next(context); return; }
        var path = context.Request.Path;
        var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo;
        var dashboard = path.StartsWithSegments("/dashboard") && (controller == typeof(OwnerDashboardController)
            || controller == typeof(OwnerDataController) || controller == typeof(OwnerResourcesController)
            || controller == typeof(OwnerActivityController));
        var allowed = dashboard || path.StartsWithSegments("/owner-assets") || path == "/health" || path == "/v3/server-info";
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            (path == "/" || path == "/login"))
        {
            context.Response.Redirect("/dashboard");
            return;
        }
        if (!allowed || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("This demo is read-only. Installation, credentials and write operations are unavailable.", context.RequestAborted);
            return;
        }
        context.Items[ContextKey] = true;
        context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }
}
