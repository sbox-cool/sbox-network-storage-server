using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Middleware;

/// <summary>
/// Request-root facts for the dual <c>/api</c> + <c>/v3</c> ingress. The rewrite middleware runs first
/// in the pipeline, so routing, limits, metering, policy and logging all see the canonical
/// <c>/v3</c> path while the original stays available here.
/// </summary>
public static class NetworkStorageRequestRoot
{
    public const string OriginalPathItemKey = "sbox-ns.api-root.original-path";

    /// <summary>The path as the client sent it; the canonical <c>/v3</c> path after the rewrite.</summary>
    public static string OriginalPath(HttpContext context)
        => context.Items.TryGetValue(OriginalPathItemKey, out var value) && value is string original
            ? original
            : context.Request.Path.Value ?? "/";

    /// <summary>The root the caller used (<c>/api</c>), defaulting to <c>/v3</c> for every other path.</summary>
    public static string Root(HttpContext context)
        => NetworkStorageRoots.HasSegmentPrefix(OriginalPath(context), NetworkStorageRoots.Api)
            ? NetworkStorageRoots.Api
            : NetworkStorageRoots.V3;

    /// <summary>
    /// Maps a canonical <c>/v3/...</c> path to the root the caller used, so server-generated URLs never
    /// hand a <c>/v3</c> client an <c>/api</c> URL it might not expect (and vice versa).
    /// </summary>
    public static string ForRequest(HttpContext context, string canonicalPath)
    {
        var root = Root(context);
        if ((root == NetworkStorageRoots.Api || root == NetworkStorageRoots.V3)
            && NetworkStorageRoots.HasSegmentPrefix(canonicalPath, NetworkStorageRoots.V3))
        {
            return root + canonicalPath.Substring(NetworkStorageRoots.V3.Length);
        }

        return canonicalPath;
    }
}

/// <summary>
/// Serves every <c>/v3/{path}</c> route at <c>/api/{path}</c> with the same handler, auth, limits,
/// metering, policy and error capture by rewriting to the canonical path before routing. Reserved
/// <c>/api</c> families (<c>storage</c>, <c>pages</c>, <c>network-storage</c>, <c>v3</c>) keep their
/// own routes and are never rewritten. Must run before <c>UseRouting</c>.
/// </summary>
public sealed class ApiRootRewriteMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (NetworkStorageRoots.TryRewriteApiRoot(context.Request.Path.Value, out var rewritten))
        {
            context.Items[NetworkStorageRequestRoot.OriginalPathItemKey] = context.Request.Path.Value ?? "/";
            context.Request.Path = new PathString(rewritten);
        }

        return next(context);
    }
}
