using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for <c>GET /v3/endpoints/:projectId/:endpointSlug</c>
/// and <c>GET /v1/endpoints/:projectId/:endpointSlug</c>. Reads endpoints from
/// the store; falls back to the workspace client on a store miss or connection
/// error. Legacy-compatible response shape.
/// Read-only; never mutates.
/// </summary>
public sealed class EndpointSlugReadHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly INetworkStorageStore _networkStore;
    private readonly ILogger<EndpointSlugReadHandler> _logger;

    public EndpointSlugReadHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageStore networkStore,
        ILogger<EndpointSlugReadHandler> logger)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _networkStore = networkStore;
        _logger = logger;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Endpoint;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Endpoint
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var endpointSlug = request.RouteParameter("endpointSlug") ?? string.Empty;

        // ── Auth ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult();
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult();
        }

        if (auth is null || !auth.Enabled)
        {
            return UnauthorizedResult();
        }

        try
        {
            return await ReadFromStoreAsync(auth, projectId, endpointSlug, request.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store endpoint read failed for project={ProjectId} slug={Slug}; falling back to workspace objects", projectId, endpointSlug);
        }

        return await ReadFromWorkspaceAsync(auth, projectId, endpointSlug, request.CancellationToken);
    }

    private async Task<NetworkStorageResult> ReadFromStoreAsync(
        StorageApiKeyAuthResult auth, string projectId, string endpointSlug, CancellationToken ct)
    {
        var endpoints = await _networkStore.ListEndpointsAsync(projectId, ct);

        JsonElement? match = null;
        foreach (var ep in endpoints)
        {
            if (ep.ValueKind != JsonValueKind.Object) continue;
            var slug = ep.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            if (string.Equals(slug, endpointSlug, StringComparison.OrdinalIgnoreCase))
            {
                match = ep;
                break;
            }
        }

        if (match is not { } found)
        {
            return NetworkStorageResult.Error(
                404,
                "ENDPOINT_NOT_FOUND",
                new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = "Endpoint not found." } },
                storagePathsRead: new[] { $"store://{projectId}/endpoints" },
                auth.KeyType);
        }

        // Extract the definition_json field (the published endpoint definition)
        JsonElement endpointDef = found;
        if (found.TryGetProperty("definition_json", out var def) && def.ValueKind == JsonValueKind.String)
        {
            try { endpointDef = JsonSerializer.Deserialize<JsonElement>(def.GetString()!); }
            catch { /* keep the raw row if definition is malformed */ }
        }

        return NetworkStorageResult.Ok(
            new { ok = true, source = "store", endpoint = endpointDef },
            storagePathsRead: new[] { $"store://{projectId}/endpoints" },
            auth.KeyType);
    }

    private async Task<NetworkStorageResult> ReadFromWorkspaceAsync(
        StorageApiKeyAuthResult auth, string projectId, string endpointSlug, CancellationToken ct)
    {
        var ownerUserId = auth.UserId;
        var storagePathsRead = new[] { $"network-storage/users/{ownerUserId}/{projectId}/endpoints.json" };
        JsonElement[] endpoints;
        try
        {
            var raw = await _workspaceClient.GetProjectResourceAsync<JsonElement>(
                ownerUserId, projectId, "endpoints.json", ct);
            endpoints = raw is { } json && json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().ToArray()
                : [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return NetworkStorageResult.Error(
                500,
                "ENDPOINT_READ_FAILED",
                new { ok = false, error = new { code = "ENDPOINT_READ_FAILED", message = "Failed to read endpoints list." } },
                storagePathsRead,
                auth.KeyType);
        }

        // ── Find by slug ──
        JsonElement? match = null;
        foreach (var ep in endpoints)
        {
            var slug = ep.TryGetProperty("slug", out var s) ? s.GetString() : null;
            if (string.Equals(slug, endpointSlug, StringComparison.OrdinalIgnoreCase))
            {
                match = ep;
                break;
            }
        }

        if (match is not { } found)
        {
            return NetworkStorageResult.Error(
                404,
                "ENDPOINT_NOT_FOUND",
                new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = "Endpoint not found." } },
                storagePathsRead,
                auth.KeyType);
        }

        return NetworkStorageResult.Ok(
            new { ok = true, source = "cdn", endpoint = found },
            storagePathsRead,
            auth.KeyType);
    }

    private static NetworkStorageResult UnauthorizedResult() =>
        NetworkStorageResult.Error(
            401, "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: null);
}