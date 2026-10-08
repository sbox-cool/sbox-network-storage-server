using SboxNetworkStorage.Application.Diagnostics;
using SboxNetworkStorage.Contracts.Diagnostics;

namespace SboxNetworkStorage.Server.Routing;

public static class RouteOwnershipBootstrap
{
    public static void Seed(IRouteOwnershipRegistry registry)
    {
        registry.Add(new RouteOwnershipRecord("/_internal/health", RouteOwner.DotNetNative, "Lightweight ASP.NET Core health endpoint"));
        registry.Add(new RouteOwnershipRecord("/_internal/ready", RouteOwner.DotNetNative, "ASP.NET Core readiness endpoint with required dependency checks"));
        registry.Add(new RouteOwnershipRecord("/_internal/stats", RouteOwner.DotNetNative, "ASP.NET Core runtime stats and route ownership diagnostics"));
        registry.Add(new RouteOwnershipRecord("/css/*, /js/*, /images/*", RouteOwner.DotNetNative, "Existing repository wwwroot static assets served by ASP.NET Core"));
        registry.Add(new RouteOwnershipRecord("public/auth MVC pages", RouteOwner.DotNetNative, "MVC controllers and Razor views served live by ASP.NET Core public ingress"));
        registry.Add(new RouteOwnershipRecord("/tools, /tools/model-importer, /tools/material-importer, /tools/terrain-generator", RouteOwner.DotNetNative, "MVC tool pages served live by ASP.NET Core public ingress"));
        registry.Add(new RouteOwnershipRecord("/settings*, /admin*, /tools/network-storage* and /workspace/*/network-storage*", RouteOwner.DotNetNative, "ASP.NET Core-owned native settings/admin/network-storage ingress routes"));
        registry.Add(new RouteOwnershipRecord("/wiki/* and /api/wiki/search", RouteOwner.DotNetNative, "Public wiki pages, guides, search, and live usage limits rendered by ASP.NET Core"));
        registry.Add(new RouteOwnershipRecord("/blog/* and /api/blog/reactions/*", RouteOwner.DotNetNative, "Public blog pages and reactions served live by ASP.NET Core"));
        registry.Add(new RouteOwnershipRecord("/api/tools/*, /api/model-importer/convert-animation", RouteOwner.DotNetNative, ".NET-owned public compatibility endpoints"));
        registry.Add(new RouteOwnershipRecord("/api/status and /api/ops/*", RouteOwner.DotNetNative, "ASP.NET Core native public status and ops probes"));
        registry.Add(new RouteOwnershipRecord("/tools/network-storage", RouteOwner.DotNetNative, "ASP.NET Core Razor Network Storage landing page"));
        registry.Add(new RouteOwnershipRecord("/tools/network-storage/new, /tools/network-storage/{projectId} and /tools/network-storage/{projectId}/* section pages", RouteOwner.DotNetNative, "ASP.NET Core Razor Network Storage project pages"));
        registry.Add(new RouteOwnershipRecord("/workspace/{slug}/network-storage/{projectId}", RouteOwner.DotNetNative, "Canonical redirect for workspace-scoped Network Storage project URLs"));
        registry.Add(new RouteOwnershipRecord("/v3/*, /api/v3/* and existing Network Storage aliases", RouteOwner.DotNetNative, "ASP.NET Core Network Storage route gateway to the non-Bun storage service"));
        registry.Add(new RouteOwnershipRecord("/_shadow/network-storage", RouteOwner.DotNetShadow, "Non-mutating .NET Network Storage classifier and comparison diagnostics", LiveProductionResponseOwner: false));
        registry.Add(new RouteOwnershipRecord("tool job workers", RouteOwner.PrivateWorker, "Approved JS/CLI-heavy jobs behind .NET-owned public ingress", LiveProductionResponseOwner: false));
    }
}
