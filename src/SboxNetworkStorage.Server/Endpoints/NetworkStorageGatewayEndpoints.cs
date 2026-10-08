using System.Text.Json;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Routing;
using System.Threading;
namespace SboxNetworkStorage.Server.Endpoints;

public static class NetworkStorageGatewayEndpoints
{
    private static readonly string[] Methods = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] ExcludedRequestHeaders = ["Host", "Connection"];
    private static readonly SemaphoreSlim GatewayConcurrency = new(100, 100);

    public static IEndpointRouteBuilder MapNetworkStorageGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/v3/storage/deploy-canary/{**path}", Methods, DeployCanaryProbeAsync)
            .WithDisplayName("Network Storage deploy canary probe")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core deploy canary probe that short-circuits before storage-api");

        // Native storage API roots. `/v3/storage` and `/api/storage` without a
        // project id are not valid Network Storage surfaces, but they are hit by
        // crawlers/probes and would otherwise fall through to the Bun storage-api
        // proxy, producing a noisy NetworkStorageGatewayUnavailable exception
        // when the deprecated Bun worker is not running. Return a native 404 instead.
        endpoints.MapGet("/v3/storage", ServeStorageRootAsync)
            .WithDisplayName("Network Storage v3 root")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage v3 root 404");
        endpoints.MapGet("/api/storage", ServeStorageRootAsync)
            .WithDisplayName("Network Storage api root")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage api root 404");

        // Native security-config read. This endpoint was still falling through to
        // the Bun storage-api proxy, so whenever that proxy was down
        // `/v3/security-config/:projectId` failed with NetworkStorageGatewayUnavailable
        // even though a native read-only candidate already existed over Bunny.
        endpoints.MapGet("/v3/security-config/{projectId}", ServeSecurityConfigAsync)
            .WithDisplayName("Network Storage security config")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage security-config read");

        // Native values read. Like security-config, a native candidate already
        // exists, so short-circuit before the Bun proxy for both the v3 route
        // and the v1 compatibility alias.
        endpoints.MapGet("/v3/values/{projectId}", ServeValuesAsync)
            .WithDisplayName("Network Storage values")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage values read");
        endpoints.MapGet("/v1/values/{projectId}", ServeValuesAsync)
            .WithDisplayName("Network Storage values (v1 alias)")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage values read via v1 compatibility alias");

        // Native rate-limits read. A native candidate already exists, so avoid
        // proxying these exact control-plane reads through Bun.
        endpoints.MapGet("/v3/storage/{projectId}/rate-limits", ServeRateLimitsAsync)
            .WithDisplayName("Network Storage rate limits")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage rate-limits read");
        endpoints.MapGet("/v1/storage/{projectId}/rate-limits", ServeRateLimitsAsync)
            .WithDisplayName("Network Storage rate limits (v1 alias)")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage rate-limits read via v1 compatibility alias");

        // Native published-page reads. A native candidate already exists, so
        // avoid proxying `/pages/*` and `/api/pages/*` through Bun for the
        // published JSON surfaces.
        endpoints.MapGet("/pages/{projectId}/{pageSlug}", ServePagesAsync)
            .WithDisplayName("Network Storage page")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage page read");
        endpoints.MapGet("/api/pages/{projectId}/{pageSlug}", ServePagesAsync)
            .WithDisplayName("Network Storage page (api)")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage page read via API alias");

        // Native read-only storage surfaces backed by existing candidates.
        endpoints.MapGet("/v3/storage/{projectId}/stats/{steamId}", ServeStatsReadAsync)
            .WithDisplayName("Network Storage stats read")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage stats read");

        // Native heartbeat write. Writes player-stats directly to Bunny CDN,
        // bypassing Bun/SpacetimeDB entirely. Critical for game clients.
        endpoints.MapPost("/v3/storage/{projectId}/stats/heartbeat", ServeHeartbeatAsync)
            .WithDisplayName("Network Storage stats heartbeat")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage heartbeat write");
        endpoints.MapPost("/api/storage/{projectId}/stats/heartbeat", ServeHeartbeatAsync)
            .WithDisplayName("Network Storage stats heartbeat (api alias)")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage heartbeat write via API alias");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{key}/probe", ServeProbeAsync)
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage record existence probe (lightweight read check)");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{steamId}/ledger", ServeLedgerReadAsync)
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage ledger read");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/list", ServeGlobalReadAsync)
            .WithDisplayName("Network Storage global list read")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage global list read");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/record/{recordId}", ServeGlobalReadAsync)
            .WithDisplayName("Network Storage global record read")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage global record read");

        // Native endpoint slug read. A native candidate handler reads the
        // endpoints list from Bunny and returns the matching slug, avoiding
        // the Bun proxy timeout for the most common endpoint read pattern.
        endpoints.MapGet("/v3/endpoints/{projectId}/{endpointSlug}", ServeEndpointSlugReadAsync)
            .WithDisplayName("Network Storage endpoint read by slug")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage endpoint slug read");
        endpoints.MapGet("/v1/endpoints/{projectId}/{endpointSlug}", ServeEndpointSlugReadAsync)
            .WithDisplayName("Network Storage endpoint read by slug (v1 alias)")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage endpoint slug read via v1 alias");

        // Native management GET family. The existing ManagementReadCandidateHandler
        // already mirrors the Bun GET surfaces under /v3/manage/:projectId/*, so
        // short-circuit the whole read family before the broad /v3/** proxy.
        endpoints.MapGet("/v3/manage/{projectId}/{**path}", ServeManagementReadAsync)
            .WithDisplayName("Network Storage management read")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage management GET family");

        // Native management mutations (ScyllaDB-backed). Exact routes must be
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/endpoints");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/endpoints");
        MapManagementMutation(endpoints, [HttpMethods.Patch], "/v3/manage/{projectId}/endpoints");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/endpoints/{endpointId}");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/collections");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/collections");
        MapManagementMutation(endpoints, [HttpMethods.Patch], "/v3/manage/{projectId}/collections");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/collections/{collectionId}");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/workflows");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/workflows");
        MapManagementMutation(endpoints, [HttpMethods.Patch], "/v3/manage/{projectId}/workflows");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/workflows/{workflowId}");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/game-values");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/game-values");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/game-values");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/rate-limit-rules");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/rate-limit-rules");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/rate-limit-rules");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/queries");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/queries/{queryId}");
        // Ported management mutations. The handlers mirror the retired Bun
        // manage-api surfaces (settings, saved tests, key destruction, source
        // upgrade, test runners); logic not yet ported answers as a described
        // dry-run via ManagementMutationCandidateHandler.
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/settings");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/tests");
        MapManagementMutation(endpoints, [HttpMethods.Delete], "/v3/manage/{projectId}/keys");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/source-upgrade");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/run-tests");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/test-endpoint");
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/suggest-tests");

        // Native package-sync mutation. The s&box editor and direct API clients
        // depend on this route to publish the game package; it was previously
        // handled by the now-decommissioned Bun storage-api.
        endpoints.MapPost("/v3/manage/{projectId}/package-sync", (Func<HttpContext, Task<IResult>>)ServePackageSyncAsync)
            .WithDisplayName("Network Storage package sync")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage package-sync mutation");

        // Native sync preflight (read-only validation) and batch push. The s&box
        // library's "Push All" flow calls POST sync/preflight then PUT sync; both
        // previously fell through to the decommissioned Bun storage-api proxy
        // (STORAGE_API_DECOMMISSIONED) / native 404. Preflight validates the
        // payload without writing; PUT sync batch-writes endpoints/collections/
        // workflows to ScyllaDB via the same native upserts as the per-resource routes.
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/sync/preflight");
        MapManagementMutation(endpoints, [HttpMethods.Put], "/v3/manage/{projectId}/sync");
        // Older Sync Tool releases call auto-test immediately after preflight.
        // Route it to the existing native management handler instead of the
        // decommissioned-management catch-all.
        MapManagementMutation(endpoints, [HttpMethods.Post], "/v3/manage/{projectId}/auto-test");

        // Optional fallback for remaining management mutation POST routes that are
        // not yet ported to native ScyllaDB writes. Returns a deliberate 501 instead
        // of proxying to the decommissioned Bun storage-api.
        endpoints.MapPost("/v3/manage/{projectId}/{**path}", ServeUnimplementedManagementMutationAsync)
            .WithDisplayName("Network Storage management mutation unimplemented")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core Network Storage management mutation not yet implemented");

        // ── Cutover task 1.6 (cutover-network-storage-to-dotnet-scylla) ──
        // When ScyllaDB is the authoritative store, serve the v3 single-record
        // CRUD family natively through the record data plane instead of proxying
        // to Bun → SpacetimeDB (the source of the production write/read timeouts).
        // Registration is gated on Scylla:Primary so that when the flag is off the
        // routes are NOT registered and these paths fall through to the Bun proxy
        // exactly as before — a reversible, zero-change default. The literal
        // sub-resources `append` and `analytics/events` are mapped to the proxy so
        // the `{key}` parameter route can never capture them.
        var scyllaPrimary = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ScyllaDbOptions>>().Value.Primary;
        if (scyllaPrimary)
        {
            // Native ScyllaDB-backed global append + analytics event ingestion.
            endpoints.MapPost("/v3/storage/{projectId}/{collectionId}/append", StorageApiEndpoints.AppendRecordAsync)
                .WithDisplayName("Network Storage v3 global append (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native v3 global append via the ScyllaDB data plane");
            endpoints.MapPost("/api/storage/{projectId}/{collectionId}/append", StorageApiEndpoints.AppendRecordAsync)
                .WithDisplayName("Network Storage api global append (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native /api/storage global append via the ScyllaDB data plane");
            endpoints.MapPost("/v3/storage/{projectId}/analytics/events", StorageApiEndpoints.PostAnalyticsEventAsync)
                .WithDisplayName("Network Storage v3 analytics events (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native v3 analytics event ingestion");
            endpoints.MapPost("/api/storage/{projectId}/analytics/events", StorageApiEndpoints.PostAnalyticsEventAsync)
                .WithDisplayName("Network Storage api analytics events (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native /api/storage analytics event ingestion");

            endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.GetRecordAsync)
                .WithDisplayName("Network Storage v3 record read (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native v3 record read via the ScyllaDB data plane");
            endpoints.MapPost("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.PostRecordAsync)
                .WithDisplayName("Network Storage v3 record write (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native v3 record write via the ScyllaDB data plane");
            endpoints.MapDelete("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.DeleteRecordAsync)
                .WithDisplayName("Network Storage v3 record delete (ScyllaDB data plane)")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native v3 record delete via the ScyllaDB data plane");
        }
        endpoints.MapEndpointExecution();

        // All Network Storage /v3/, /v1/, /api/storage/, /api/v3/, /pages/ routes
        // are now served natively by ASP.NET Core + ScyllaDB. The previous
        // catch-all proxy to the Bun storage-api is removed — any unmatched
        // /v3/ or /v1/ path returns a native 404 instead of silently proxying
        // to the decommissioned Bun/SpacetimeDB data plane.
        foreach (var pattern in new[]
        {
            "/api/v3/network-storage",
            "/api/v3/{**path}",
            "/v3/{**path}",
            "/v1/{**path}",
            "/api/storage/{**path}",
            "/api/pages/{**path}",
            "/pages/{**path}"
        })
        {
            endpoints.MapMethods(pattern, Methods, ServeNotFoundAsync)
                .WithDisplayName($"Network Storage unmatched {pattern}")
                .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native 404 for unmatched Network Storage paths (no Bun proxy)");
        }

        return endpoints;
    }

    private static async Task DeployCanaryProbeAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = "ASP.NET Core deploy canary probe that short-circuits before storage-api";
        await context.Response.WriteAsJsonAsync(new
        {
            ok = true,
            probe = "deploy-canary",
            owner = ".NET native"
        }, context.RequestAborted);
    }

    private static async Task ServeStorageRootAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = "ASP.NET Core native Network Storage v3 root 404";
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "NOT_FOUND",
            detail = "Network Storage v3 endpoints require a project id."
        }, context.RequestAborted);
    }

    private static async Task ServeNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = "ASP.NET Core native 404 for unmatched Network Storage paths (no Bun proxy)";
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "NOT_FOUND",
            detail = "This Network Storage route is not recognized. All /v3/ and /v1/ traffic is served by ASP.NET Core + ScyllaDB."
        }, context.RequestAborted);
    }

    private static async Task ServeSecurityConfigAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var executor = context.RequestServices.GetRequiredService<INetworkStorageReadCandidateExecutor>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var authSignals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasApiKey"] = false,
            ["hasSteamId"] = false,
            ["hasAuthSessionToken"] = false,
            ["hasSessionToken"] = false,
            ["hasEncryptedRequestId"] = false,
        };
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            NetworkStorageCredentials.None,
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await executor.TryExecuteAsync(request);
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = new { code = "DOTNET_ROUTE_NOT_IMPLEMENTED", message = "No native security-config handler is registered." }
            }, context.RequestAborted);
            return;
        }

        // Mirror Bun cache semantics closely enough for clients: bypass when the
        // caller asks for refresh/no-cache, otherwise short public caching.
        var cacheBypass = string.Equals(context.Request.Query["refresh"], "1", StringComparison.Ordinal)
            || string.Equals(context.Request.Query["cache"], "bypass", StringComparison.OrdinalIgnoreCase)
            || (context.Request.Headers.CacheControl.ToString()?.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ?? false);
        context.Response.Headers["Cache-Control"] = cacheBypass
            ? "no-store, max-age=0"
            : "public, max-age=15, s-maxage=60";
        context.Response.Headers["X-Security-Config-Cache"] = cacheBypass ? "bypass" : "public";
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();

        // Candidate bodies are anonymous-object payloads. Extract source/version
        // headers if present so existing clients keep seeing them.
        var bodyJson = JsonSerializer.SerializeToElement(result.Body ?? new { });
        if (bodyJson.ValueKind == JsonValueKind.Object)
        {
            if (bodyJson.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
                context.Response.Headers["X-Security-Config-Source"] = source.GetString() ?? "";
            if (bodyJson.TryGetProperty("config", out var config)
                && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("configVersion", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                context.Response.Headers["X-Security-Config-Version"] = version.GetString() ?? "";
            }
        }

        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeValuesAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var executor = context.RequestServices.GetRequiredService<INetworkStorageReadCandidateExecutor>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (query.TryGetValue("apiKey", out var queryKey) ? queryKey : null);
        var authSignals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasApiKey"] = !string.IsNullOrEmpty(apiKey),
            ["hasSteamId"] = false,
            ["hasAuthSessionToken"] = false,
            ["hasSessionToken"] = false,
            ["hasEncryptedRequestId"] = false,
        };
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await executor.TryExecuteAsync(request);
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "DOTNET_ROUTE_NOT_IMPLEMENTED", message = "No native values handler is registered." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeRateLimitsAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var executor = context.RequestServices.GetRequiredService<INetworkStorageReadCandidateExecutor>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (query.TryGetValue("apiKey", out var queryKey) ? queryKey : null);
        var authSignals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasApiKey"] = !string.IsNullOrEmpty(apiKey),
            ["hasSteamId"] = false,
            ["hasAuthSessionToken"] = false,
            ["hasSessionToken"] = false,
            ["hasEncryptedRequestId"] = false,
        };
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await executor.TryExecuteAsync(request);
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "DOTNET_ROUTE_NOT_IMPLEMENTED", message = "No native rate-limits handler is registered." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServePagesAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var executor = context.RequestServices.GetRequiredService<INetworkStorageReadCandidateExecutor>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["hasApiKey"] = false,
                ["hasSteamId"] = false,
                ["hasAuthSessionToken"] = false,
                ["hasSessionToken"] = false,
                ["hasEncryptedRequestId"] = false,
            },
            NetworkStorageCredentials.None,
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await executor.TryExecuteAsync(request);
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "DOTNET_ROUTE_NOT_IMPLEMENTED", message = "No native pages handler is registered." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeStatsReadAsync(HttpContext context)
        => await ServeApiKeyReadCandidateAsync(context);

    private static async Task ServeLedgerReadAsync(HttpContext context)
        => await ServeApiKeyReadCandidateAsync(context);

    private static async Task ServeGlobalReadAsync(HttpContext context)
        => await ServeApiKeyReadCandidateAsync(context);

    private static async Task ServeManagementReadAsync(HttpContext context)
        => await ServeApiKeyReadCandidateAsync(context);

    private static async Task<IResult> ServePackageSyncAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var handler = context.RequestServices.GetRequiredService<PackageSyncHandler>();
        return await handler.HandleAsync(context, projectId, context.RequestAborted);
    }

    private static async Task ServeEndpointSlugReadAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<EndpointSlugReadCandidateHandler>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (query.TryGetValue("apiKey", out var queryKey) ? queryKey : null);
        // GET /v3/endpoints/{projectId}/{endpointSlug} with an API key is public
        // endpoint-execution traffic. Serve it through the same native path as POST
        // execution (full apiKey/project auth, identity + input resolution, native
        // ScyllaDB execution). There is no Bun proxy in the request path: an endpoint
        // the native executor cannot run returns a reported 501. The legacy Bun
        // storage-api is decommissioned, so the previous "shadow native + serve Bun"
        // default failed every authenticated GET with a 502 (connection refused on
        // 127.0.0.1:4547). Unauthenticated probes keep the fast native 401 below.
        if (!string.IsNullOrEmpty(apiKey))
        {
            await EndpointExecutionEndpoints.ExecuteEndpointAsync(context);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            context.RequestServices.GetRequiredService<IOptions<SboxcoolBackendOptions>>().Value.StorageApiGatewayTimeoutSeconds,
            1,
            300)));
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["hasApiKey"] = !string.IsNullOrEmpty(apiKey),
                ["hasSteamId"] = false,
                ["hasAuthSessionToken"] = false,
                ["hasSessionToken"] = false,
                ["hasEncryptedRequestId"] = false,
            },
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            timeout.Token);

        NetworkStorageCandidateResult result;
        try
        {
            result = await handler.ExecuteAsync(request);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = new { code = "ENDPOINT_READ_TIMEOUT", message = "Endpoint metadata read timed out." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeHeartbeatAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (context.Request.Query.TryGetValue("apiKey", out var queryKey) ? queryKey.ToString() : null);
        string? steamId = context.GetRouteValue("steamId") as string;
        if (string.IsNullOrEmpty(steamId) && context.Request.Query.TryGetValue("steamId", out var qsid))
            steamId = qsid.ToString();

        JsonElement? body = null;
        if (context.Request.ContentLength > 0)
        {
            try { body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted); }
            catch { /* ignore bad body */ }
        }

        var handler = context.RequestServices.GetRequiredService<NativeStatsHeartbeatHandler>();
        var result = await handler.ExecuteAsync(projectId, apiKey, steamId, body, context.RequestAborted);

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    /// <summary>
    /// Lightweight record existence probe. Reads a single record from ScyllaDB
    /// and returns <c>{ok:true, exists:true/false}</c> without exposing the
    /// payload. Used by game clients to verify connectivity and data-plane
    /// health before attempting full reads.
    /// </summary>
    private static async Task ServeProbeAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";
        var recordKey = (string?)context.GetRouteValue("key") ?? "";

        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (context.Request.Query.TryGetValue("apiKey", out var queryKey) ? queryKey.ToString() : null);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "UNAUTHORIZED", detail = "Missing apiKey" });
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "UNAUTHORIZED" });
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "KEY_DISABLED" });
            return;
        }

        var dataPlane = context.RequestServices.GetRequiredService<INetworkStorageDataPlane>();
        bool exists;
        try
        {
            var read = await dataPlane.ReadRecordAsync(auth.UserId, projectId, collectionId, recordKey, context.RequestAborted);
            exists = read.Found;
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProbeEndpoint");
            logger.LogError(ex, "Probe read failed for {ProjectId}/{CollectionId}/{Key}", projectId, collectionId, recordKey);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "PROBE_READ_FAILED" });
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { ok = true, exists });
    }


    private static async Task ServeApiKeyReadCandidateAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var executor = context.RequestServices.GetRequiredService<INetworkStorageReadCandidateExecutor>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (query.TryGetValue("apiKey", out var queryKey) ? queryKey : null);
        var authSignals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasApiKey"] = !string.IsNullOrEmpty(apiKey),
            ["hasSteamId"] = false,
            ["hasAuthSessionToken"] = false,
            ["hasSessionToken"] = false,
            ["hasEncryptedRequestId"] = false,
        };
        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await executor.TryExecuteAsync(request);
        if (result is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "DOTNET_ROUTE_NOT_IMPLEMENTED", message = "No native read handler is registered." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    internal static async Task ProxyAsync(HttpContext context)
    {
        if (!await GatewayConcurrency.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "NETWORK_STORAGE_GATEWAY_BUSY" }, context.RequestAborted);
            return;
        }
        try
        {
            await InnerProxyAsync(context);
        }
        finally
        {
            GatewayConcurrency.Release();
        }
    }

    private static async Task InnerProxyAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<SboxcoolBackendOptions>>().Value;

        // The legacy Bun storage-api backend has been decommissioned. When no
        // gateway URL is configured, return a clear 501 instead of crashing on
        // an empty Uri or retrying a dead port.
        if (string.IsNullOrWhiteSpace(options.StorageApiGatewayBaseUrl))
        {
            await ServeDecommissionedAsync(context, "storage-api");
            return;
        }

        var gatewayBaseUri = new Uri(EnsureTrailingSlash(options.StorageApiGatewayBaseUrl), UriKind.Absolute);
        await ProxyToBaseUrlAsync(context, gatewayBaseUri, options.StorageApiGatewayTimeoutSeconds, "storage-api");
    }

    private static void MapManagementMutation(IEndpointRouteBuilder endpoints, string[] methods, string pattern)
    {
        endpoints.MapMethods(pattern, methods, ServeNativeManagementMutationAsync)
            .WithDisplayName($"Network Storage management mutation {pattern}")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core native Network Storage management mutation");
    }

    private static async Task ServeNativeManagementMutationAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);

        var route = NetworkStorageRouteClassifier.Classify(context.Request.Method, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<ManagementMutationCandidateHandler>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        string? body = null;
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContentLength == 0)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;
        }

        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (query.TryGetValue("apiKey", out var queryKey) ? queryKey : null);

        var authSignals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["hasApiKey"] = !string.IsNullOrEmpty(apiKey),
            ["hasSteamId"] = false,
            ["hasAuthSessionToken"] = false,
            ["hasSessionToken"] = false,
            ["hasEncryptedRequestId"] = false,
        };

        var request = new NetworkStorageCandidateRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            body,
            ResolvedOwnerUserId: null,
            context.RequestAborted)
        {
            SuppressSideEffects = false,
        };

        NetworkStorageCandidateResult result;
        try
        {
            result = await handler.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ManagementMutation");
            logger.LogError(ex, "Native management mutation failed for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = new { code = "MANAGEMENT_MUTATION_FAILED", message = "Native mutation handler failed." }
            }, context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeUnimplementedManagementMutationAsync(HttpContext context)
    {
        StampDispatchDiagnostics(context);
        await ServeDecommissionedAsync(context, "legacy-management-api");
    }

    private static async Task ServeDecommissionedAsync(HttpContext context, string source)
    {
        StampDispatchDiagnostics(context);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "STORAGE_API_DECOMMISSIONED",
            message = "The legacy storage-api backend has been decommissioned. This route has not been migrated to the native .NET executor yet.",
            source,
        }, context.RequestAborted);
    }

    private static async Task ProxyToBaseUrlAsync(HttpContext context, Uri gatewayBaseUri, int timeoutSeconds, string upstreamLabel)
    {
        var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        var proxyErrorReporter = context.RequestServices.GetRequiredService<IProxyErrorReporter>();

        StampDispatchDiagnostics(context);

        using var proxyRequest = CreateProxyRequest(context, gatewayBaseUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));

        // Retry transient connection failures (e.g. storage-api restarting during deploy).
        // Up to 3 attempts with 500ms, 1000ms backoff. Only retries HttpRequestException
        // with SocketException (connection refused/reset), not timeouts or cancellations.
        HttpResponseMessage? upstreamResponse = null;
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Clone the request body for each attempt (streams are consumed after SendAsync).
            var attemptRequest = attempt == 1 ? proxyRequest : await CloneRequestAsync(proxyRequest);
            try
            {
                upstreamResponse = await httpClientFactory
                    .CreateClient("storage-api-gateway")
                    .SendAsync(attemptRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                break; // success
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException } && attempt < maxAttempts)
            {
                // Connection refused/reset — retry after backoff. If the gateway
                // timeout fires during the backoff, the delay throws; treat that as
                // upstream-unavailable (502) instead of letting it escape as a 500.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), timeout.Token);
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
            {
                await proxyErrorReporter.CaptureAsync(
                    context,
                    StatusCodes.Status502BadGateway,
                    "NetworkStorageGatewayUnavailable",
                    "Network Storage gateway unavailable.",
                    ex);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
                context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = "ASP.NET Core Network Storage route gateway to the non-Bun storage service";
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = false,
                    error = "NETWORK_STORAGE_GATEWAY_UNAVAILABLE"
                }, context.RequestAborted);
                return;
            }
        }
        if (upstreamResponse is null)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "NETWORK_STORAGE_GATEWAY_UNAVAILABLE" }, context.RequestAborted);
            return;
        }


        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(context.Response, upstreamResponse);
            context.Response.Headers["X-Sboxcool-Route-Owner"] = RouteOwner.DotNetNative.ToDisplayName();
            context.Response.Headers["X-Sboxcool-Route-Owner-Description"] = "ASP.NET Core Network Storage route gateway to the non-Bun storage service";
            if (!await ProxyResponseForwarder.TryForwardServerErrorAsync(
                    context, upstreamResponse, proxyErrorReporter, "NetworkStorageGatewayUpstream5xx", timeout.Token))
            {
                await upstreamResponse.Content.CopyToAsync(context.Response.Body, timeout.Token);
            }
        }
    }

    private static HttpRequestMessage CreateProxyRequest(HttpContext context, Uri gatewayBaseUri)
    {
        var targetUri = new Uri(gatewayBaseUri, $"{context.Request.Path}{context.Request.QueryString}");
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (ExcludedRequestHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.Host = gatewayBaseUri.Authority;
        request.Headers.Remove("X-Forwarded-Host");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        if (context.Connection.RemoteIpAddress is not null)
        {
            request.Headers.Remove("X-Forwarded-For");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress.ToString());
        }

        return request;
    }

    private static void CopyResponseHeaders(HttpResponse response, HttpResponseMessage upstreamResponse)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (string.Equals(header.Key, "transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (string.Equals(header.Key, "transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static void StampDispatchDiagnostics(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<INetworkStorageModeResolver>();
        var classification = NetworkStorageRouteClassifier.Classify(context.Request.Method, context.Request.Path.Value);
        var decision = resolver.Resolve(classification);
        context.Response.Headers["X-Sboxcool-Network-Storage-Mode"] = decision.EffectiveMode.ToString();
        context.Response.Headers["X-Sboxcool-Network-Storage-Served-By"] =
            decision.Target == NetworkStorageDispatchTarget.Bun ? "bun-proxy" : ".net-candidate";
        context.Response.Headers["X-Sboxcool-Network-Storage-Dispatch-Reason"] = decision.Reason;
    }

    private static string EnsureTrailingSlash(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (original.Content is not null)
        {
            var body = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(body);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

}
