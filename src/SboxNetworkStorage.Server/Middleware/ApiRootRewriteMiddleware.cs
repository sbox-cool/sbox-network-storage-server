using Microsoft.AspNetCore.Routing.Patterns;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Compat;

namespace SboxNetworkStorage.Server.Middleware;

public static class NetworkStorageRequestRoot
{
    public const string OriginalPathItemKey = "sbox-ns.api-root.original-path";

    public static string OriginalPath(HttpContext context)
        => context.Items.TryGetValue(OriginalPathItemKey, out var value) && value is string original
            ? original : context.Request.Path.Value ?? "/";
}

internal sealed record CanonicalApiEndpoint(RouteEndpoint Endpoint);

public static class ApiRootEndpoints
{
    /// <summary>
    /// Copy existing v3 endpoint delegates and metadata at startup, not future api endpoints.
    /// Reserved legacy families keep their original registrations. No request-time route discovery.
    /// </summary>
    public static void MapApiRootAliases(this IEndpointRouteBuilder routes)
    {
        var aliases = new List<Endpoint>();
        foreach (var endpoint in routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var path = endpoint.RoutePattern.RawText;
            if (path is null || !NetworkStorageRoots.HasSegmentPrefix(path, NetworkStorageRoots.V3)) continue;
            var alias = NetworkStorageRoots.Api + path[NetworkStorageRoots.V3.Length..];
            if (!NetworkStorageRoots.TryRewriteApiRoot(alias, out _)) continue;
            var builder = new RouteEndpointBuilder(endpoint.RequestDelegate!, RoutePatternFactory.Parse(alias), endpoint.Order)
            {
                DisplayName = endpoint.DisplayName + " (api alias)"
            };
            foreach (var metadata in endpoint.Metadata)
            {
                // Names identify the original endpoint; duplicating them breaks link generation.
                if (metadata is not IEndpointNameMetadata && metadata is not IRouteNameMetadata)
                    builder.Metadata.Add(metadata);
            }
            builder.Metadata.Add(new CanonicalApiEndpoint(endpoint));
            aliases.Add(builder.Build());
        }
        routes.DataSources.Add(new DefaultEndpointDataSource(aliases));
    }
}

/// <summary>
/// /api/v3 explicitly selects the existing v3 API. /api/storage selects it only for identified
/// libraries from 1.2.0 onward; older and unidentified clients retain legacy document semantics.
/// Version metadata selects syntax, never authentication or authorization.
/// </summary>
public sealed class ApiRootRewriteMiddleware(RequestDelegate next)
{
    public const string UniformApiLibrarySince = "1.2.0";
    private static readonly Updates.SemanticVersion UniformApiLibrary = new(1, 2, 0, null);

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        var explicitV3 = NetworkStorageRoots.HasSegmentPrefix(path, NetworkStorageRoots.ApiV3);
        var modernStorage = NetworkStorageRoots.HasSegmentPrefix(path, NetworkStorageRoots.Api + "/storage")
            && ClientIdentityContext.Get(context).Library is { } version && version.CompareTo(UniformApiLibrary) >= 0;
        if (explicitV3 || modernStorage)
        {
            context.Items[NetworkStorageRequestRoot.OriginalPathItemKey] = path ?? "/";
            context.Request.Path = NetworkStorageRoots.V3 + path![(explicitV3 ? NetworkStorageRoots.ApiV3.Length : NetworkStorageRoots.Api.Length)..];
        }
        return next(context);
    }
}
