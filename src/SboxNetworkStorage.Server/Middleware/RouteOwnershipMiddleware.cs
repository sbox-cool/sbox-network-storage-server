using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Routing;

namespace SboxNetworkStorage.Server.Middleware;

public static class RouteOwnershipResolver
{
    public static RouteOwner ResolveOwner(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/_shadow/network-storage", StringComparison.OrdinalIgnoreCase)) return RouteOwner.DotNetShadow;
        return RouteOwner.DotNetNative;
    }
}

public sealed class RouteOwnershipMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var metadata = context.GetEndpoint()?.Metadata.GetMetadata<RouteOwnershipMetadata>();
            var owner = metadata?.Owner ?? RouteOwnershipResolver.ResolveOwner(context.Request.Path);
            context.Response.Headers["X-Sboxcool-Route-Owner"] = owner.ToDisplayName();
            if (metadata is not null)
            {
                context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = metadata.Description;
            }
            return Task.CompletedTask;
        });

        await next(context);
    }
}
