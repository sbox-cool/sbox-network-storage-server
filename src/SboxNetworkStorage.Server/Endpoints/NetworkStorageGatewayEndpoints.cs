using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

using System.Threading;
namespace SboxNetworkStorage.Server.Endpoints;

public static class NetworkStorageGatewayEndpoints
{
    private static readonly string[] Methods = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];

    public static IEndpointRouteBuilder MapNetworkStorageGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/v3/storage/deploy-canary/{**path}", Methods, DeployCanaryProbeAsync)
            .WithDisplayName("Network Storage deploy canary probe");

        // Native storage API roots. `/v3/storage` and `/api/storage` without a
        // project id are not valid Network Storage surfaces, but they are hit by
        // crawlers/probes and would otherwise fall through to the Bun storage-api
        // proxy, producing a noisy NetworkStorageGatewayUnavailable exception
        // when the deprecated Bun worker is not running. Return a native 404 instead.
        endpoints.MapGet("/v3/storage", ServeStorageRootAsync)
            .WithDisplayName("Network Storage v3 root");
        endpoints.MapGet("/api/storage", ServeStorageRootAsync)
            .WithDisplayName("Network Storage api root");

        // Native security-config read. This endpoint was still falling through to
        // the Bun storage-api proxy, so whenever that proxy was down
        // `/v3/security-config/:projectId` failed with NetworkStorageGatewayUnavailable
        // even though a native read-only candidate already existed over Bunny.
        endpoints.MapGet("/v3/security-config/{projectId}", ServeSecurityConfigAsync)
            .WithDisplayName("Network Storage security config");

        // Native values read. Like security-config, a native candidate already
        // exists, so short-circuit before the Bun proxy for both the v3 route
        // and the v1 compatibility alias.
        endpoints.MapGet("/v3/values/{projectId}", ServeValuesAsync)
            .WithDisplayName("Network Storage values");
        endpoints.MapGet("/v1/values/{projectId}", ServeValuesAsync)
            .WithDisplayName("Network Storage values (v1 alias)");

        // Native rate-limits read. A native candidate already exists, so avoid
        // proxying these exact control-plane reads through Bun.
        endpoints.MapGet("/v3/storage/{projectId}/rate-limits", ServeRateLimitsAsync)
            .WithDisplayName("Network Storage rate limits");
        endpoints.MapGet("/v1/storage/{projectId}/rate-limits", ServeRateLimitsAsync)
            .WithDisplayName("Network Storage rate limits (v1 alias)");

        // Native published-page reads. A native candidate already exists, so
        // avoid proxying `/pages/*` and `/api/pages/*` through Bun for the
        // published JSON surfaces.
        endpoints.MapGet("/pages/{projectId}/{pageSlug}", ServePagesAsync)
            .WithDisplayName("Network Storage page");
        endpoints.MapGet("/api/pages/{projectId}/{pageSlug}", ServePagesAsync)
            .WithDisplayName("Network Storage page (api)");

        // Native read-only storage surfaces backed by existing candidates.
        endpoints.MapGet("/v3/storage/{projectId}/stats/{steamId}", ServeStatsReadAsync)
            .WithDisplayName("Network Storage stats read");

        // Native heartbeat write. Writes player-stats directly to Bunny CDN,
        // bypassing Bun/SpacetimeDB entirely. Critical for game clients.
        endpoints.MapPost("/v3/storage/{projectId}/stats/heartbeat", ServeHeartbeatAsync)
            .WithDisplayName("Network Storage stats heartbeat");
        endpoints.MapPost("/api/storage/{projectId}/stats/heartbeat", ServeHeartbeatAsync)
            .WithDisplayName("Network Storage stats heartbeat (api alias)");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{key}/probe", ServeProbeAsync);
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{steamId}/ledger", ServeLedgerReadAsync);
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/list", ServeGlobalReadAsync)
            .WithDisplayName("Network Storage global list read");
        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/record/{recordId}", ServeGlobalReadAsync)
            .WithDisplayName("Network Storage global record read");

        // Native endpoint slug read. A native candidate handler reads the
        // endpoints list from Bunny and returns the matching slug, avoiding
        // the Bun proxy timeout for the most common endpoint read pattern.
        endpoints.MapGet("/v3/endpoints/{projectId}/{endpointSlug}", ServeEndpointSlugReadAsync)
            .WithDisplayName("Network Storage endpoint read by slug");
        endpoints.MapGet("/v1/endpoints/{projectId}/{endpointSlug}", ServeEndpointSlugReadAsync)
            .WithDisplayName("Network Storage endpoint read by slug (v1 alias)");

        // Native management GET family. The existing ManagementReadCandidateHandler
        // already mirrors the Bun GET surfaces under /v3/manage/:projectId/*, so
        // short-circuit the whole read family before the broad /v3/** proxy.
        endpoints.MapGet("/v3/manage/{projectId}/{**path}", ServeManagementReadAsync)
            .WithDisplayName("Network Storage management read");

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
            .WithDisplayName("Network Storage package sync");

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
        // Game-client revision handshake (sbox-cool/sbox-network-storage
        // NetworkStorageRevisionInit). Public-key route: the game reports its
        // running revision and learns whether it is outdated. Read-only.
        endpoints.MapPost("/v3/manage/{projectId}/revision-init", (Func<HttpContext, Task<IResult>>)ServeRevisionInitAsync)
            .WithDisplayName("Network Storage revision init");

        // Fallback for unknown management mutation POST routes. These match no
        // reference surface (Bun or .NET); answer 404, never a 5xx.
        endpoints.MapPost("/v3/manage/{projectId}/{**path}", ServeUnknownManagementMutationAsync)
            .WithDisplayName("Network Storage management mutation unknown");

        // Literal subresources precede record CRUD so they cannot be captured as record keys.
        endpoints.MapPost("/v3/storage/{projectId}/{collectionId}/append", StorageApiEndpoints.AppendRecordAsync)
            .WithDisplayName("Network Storage v3 global append");
        endpoints.MapPost("/api/storage/{projectId}/{collectionId}/append", StorageApiEndpoints.AppendRecordAsync)
            .WithDisplayName("Network Storage api global append");
        endpoints.MapPost("/v3/storage/{projectId}/analytics/events", StorageApiEndpoints.PostAnalyticsEventAsync)
            .WithDisplayName("Network Storage v3 analytics events");
        endpoints.MapPost("/api/storage/{projectId}/analytics/events", StorageApiEndpoints.PostAnalyticsEventAsync)
            .WithDisplayName("Network Storage api analytics events");

        endpoints.MapGet("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.GetRecordAsync)
            .WithDisplayName("Network Storage v3 record read");
        endpoints.MapPost("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.PostRecordAsync)
            .WithDisplayName("Network Storage v3 record write");
        endpoints.MapDelete("/v3/storage/{projectId}/{collectionId}/{key}", StorageApiEndpoints.DeleteRecordAsync)
            .WithDisplayName("Network Storage v3 record delete");
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
                .WithDisplayName($"Network Storage unmatched {pattern}");
        }

        return endpoints;
    }

    /// <summary>
    /// Annotates the request for usage metering when the handler authenticated
    /// a key for the route's project. Denied and anonymous results are not metered.
    /// </summary>
    private static void MeterAuthenticated(HttpContext context, NetworkStorageResult result)
    {
        if (result.AuthDecision is null or "denied" or "anonymous") return;
        if (context.GetRouteValue("projectId") is not string { Length: > 0 } projectId) return;
        NetworkStorageUsageContext.SetAuthenticated(context, projectId);
    }

    private static async Task DeployCanaryProbeAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
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
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "NOT_FOUND",
            detail = "This Network Storage route is not recognized. All /v3/ and /v1/ traffic is served by ASP.NET Core + Store."
        }, context.RequestAborted);
    }

    private static async Task ServeSecurityConfigAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<SecurityConfigHandler>();
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
        var request = new NetworkStorageRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            NetworkStorageCredentials.None,
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await handler.ExecuteAsync(request);

        // Mirror Bun cache semantics closely enough for clients: bypass when the
        // caller asks for refresh/no-cache, otherwise short public caching.
        var cacheBypass = string.Equals(context.Request.Query["refresh"], "1", StringComparison.Ordinal)
            || string.Equals(context.Request.Query["cache"], "bypass", StringComparison.OrdinalIgnoreCase)
            || (context.Request.Headers.CacheControl.ToString()?.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ?? false);
        context.Response.Headers["Cache-Control"] = cacheBypass
            ? "no-store, max-age=0"
            : "public, max-age=15, s-maxage=60";
        context.Response.Headers["X-Security-Config-Cache"] = cacheBypass ? "bypass" : "public";

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

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeValuesAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<GameValuesHandler>();
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
        var request = new NetworkStorageRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await handler.ExecuteAsync(request);

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeRateLimitsAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<RateLimitsHandler>();
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
        var request = new NetworkStorageRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await handler.ExecuteAsync(request);

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServePagesAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<PagesHandler>();
        var query = context.Request.Query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var request = new NetworkStorageRequest(
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

        var result = await handler.ExecuteAsync(request);

        // Published pages are public; a served page proves the project exists, so its transfer is metered.
        if (result.StatusCode == StatusCodes.Status200OK
            && context.GetRouteValue("projectId") is string { Length: > 0 } pageProjectId)
        {
            NetworkStorageUsageContext.SetAuthenticated(context, pageProjectId);
        }

        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeStatsReadAsync(HttpContext context)
        => await ServeApiKeyReadAsync(context, context.RequestServices.GetRequiredService<StatsReadHandler>());

    private static async Task ServeLedgerReadAsync(HttpContext context)
        => await ServeApiKeyReadAsync(context, context.RequestServices.GetRequiredService<StorageLedgerReadHandler>());

    private static async Task ServeGlobalReadAsync(HttpContext context)
        => await ServeApiKeyReadAsync(context, context.RequestServices.GetRequiredService<StorageGlobalReadHandler>());

    private static async Task ServeManagementReadAsync(HttpContext context)
        => await ServeApiKeyReadAsync(context, context.RequestServices.GetRequiredService<ManagementReadHandler>());

    private static async Task<IResult> ServePackageSyncAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var handler = context.RequestServices.GetRequiredService<PackageSyncHandler>();
        return await handler.HandleAsync(context, projectId, context.RequestAborted);
    }

    private static async Task<IResult> ServeRevisionInitAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var handler = context.RequestServices.GetRequiredService<RevisionInitHandler>();
        return await handler.HandleAsync(context, projectId, context.RequestAborted);
    }

    private static async Task ServeEndpointSlugReadAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<EndpointSlugReadHandler>();
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
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var request = new NetworkStorageRequest(
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

        NetworkStorageResult result;
        try
        {
            result = await handler.ExecuteAsync(request);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = new { code = "ENDPOINT_READ_TIMEOUT", message = "Endpoint metadata read timed out." }
            }, context.RequestAborted);
            return;
        }

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeHeartbeatAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey)
            ? headerKey.ToString()
            : (context.Request.Query.TryGetValue("apiKey", out var queryKey) ? queryKey.ToString() : null);
        string? steamId = context.GetRouteValue("steamId") as string;
        if (string.IsNullOrEmpty(steamId) && context.Request.Query.TryGetValue("steamId", out var qsid))
            steamId = qsid.ToString();

        var handler = context.RequestServices.GetRequiredService<NativeStatsHeartbeatHandler>();

        // Authenticate from headers before reading any request body.
        var authentication = await handler.AuthenticateAsync(projectId, apiKey, ClientAddress.Resolve(context), context.RequestAborted);
        if (authentication.Auth is not { } auth)
        {
            var rejection = authentication.Rejection!;
            context.Response.StatusCode = rejection.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (rejection.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                context.Response.Headers.RetryAfter = "60";
            }

            await context.Response.WriteAsJsonAsync(rejection.Body, context.RequestAborted);
            return;
        }

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

        JsonElement? body = null;
        if (context.Request.ContentLength > 0)
        {
            try { body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* ignore bad body */ }
        }

        var result = await handler.ExecuteAsync(projectId, auth, steamId, body, context.RequestAborted);

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

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

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

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { ok = true, exists });
    }


    private static async Task ServeApiKeyReadAsync(HttpContext context, INetworkStorageHandler handler)
    {
        var route = NetworkStorageRouteClassifier.Classify(HttpMethods.Get, context.Request.Path);
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
        var request = new NetworkStorageRequest(
            route,
            query,
            context.Request.ContentType,
            authSignals,
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            Body: null,
            ResolvedOwnerUserId: null,
            context.RequestAborted);

        var result = await handler.ExecuteAsync(request);

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }


    private static void MapManagementMutation(IEndpointRouteBuilder endpoints, string[] methods, string pattern)
    {
        endpoints.MapMethods(pattern, methods, ServeNativeManagementMutationAsync)
            .WithDisplayName($"Network Storage management mutation {pattern}");
    }

    private static async Task ServeNativeManagementMutationAsync(HttpContext context)
    {
        var route = NetworkStorageRouteClassifier.Classify(context.Request.Method, context.Request.Path);
        var handler = context.RequestServices.GetRequiredService<ManagementMutationHandler>();
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

        var request = new NetworkStorageRequest(route,
        query,
        context.Request.ContentType,
        authSignals,
        new NetworkStorageCredentials(apiKey, null, null, null, null),
        Body: null,
        ResolvedOwnerUserId: null,
        context.RequestAborted);

        // Authenticate from headers before reading any request body.
        var rejection = await handler.RejectUnauthenticatedAsync(request);
        if (rejection is not null)
        {
            context.Response.StatusCode = rejection.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(rejection.Body, context.RequestAborted);
            return;
        }

        string? body = null;
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContentLength == 0)
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        request = request with { Body = body };

        NetworkStorageResult result;
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

        MeterAuthenticated(context, result);
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(result.Body, context.RequestAborted);
    }

    private static async Task ServeUnknownManagementMutationAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "NOT_FOUND",
            detail = "This Network Storage route is not recognized.",
        }, context.RequestAborted);
    }


}

