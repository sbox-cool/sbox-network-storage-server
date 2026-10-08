using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native candidate for <c>GET /v3/endpoints/:projectId/:endpointSlug</c>
/// and <c>GET /v1/endpoints/:projectId/:endpointSlug</c>. When <c>Scylla:Primary</c>
/// is true, reads endpoints from ScyllaDB; falls back to the Bunny workspace
/// client on ScyllaDB miss or connection error. Bun-compatible response shape.
/// Read-only; never mutates.
/// </summary>
public sealed class EndpointSlugReadCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IBunnyWorkspaceClient _workspaceClient;
    private readonly INetworkStorageStore _scyllaStore;
    private readonly ScyllaDbOptions _scyllaOptions;
    private readonly ILogger<EndpointSlugReadCandidateHandler> _logger;

    public EndpointSlugReadCandidateHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IBunnyWorkspaceClient workspaceClient,
        INetworkStorageStore scyllaStore,
        IOptions<ScyllaDbOptions> scyllaOptions,
        ILogger<EndpointSlugReadCandidateHandler> logger)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _scyllaStore = scyllaStore;
        _scyllaOptions = scyllaOptions.Value;
        _logger = logger;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Endpoint;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Endpoint
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
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

        // ── Try ScyllaDB first when primary ──
        if (_scyllaOptions.Primary)
        {
            try
            {
                return await ReadFromScyllaAsync(auth, projectId, endpointSlug, request.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ScyllaDB endpoint read failed for project={ProjectId} slug={Slug}; falling back to Bunny", projectId, endpointSlug);
            }
        }

        return await ReadFromBunnyAsync(auth, projectId, endpointSlug, request.CancellationToken);
    }

    private async Task<NetworkStorageCandidateResult> ReadFromScyllaAsync(
        StorageApiKeyAuthResult auth, string projectId, string endpointSlug, CancellationToken ct)
    {
        var endpoints = await _scyllaStore.ListEndpointsAsync(projectId, ct);

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
            return NetworkStorageCandidateResult.Error(
                404,
                "ENDPOINT_NOT_FOUND",
                new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = "Endpoint not found." } },
                storagePathsRead: new[] { $"scylladb://{projectId}/endpoints" },
                auth.KeyType);
        }

        // Extract the definition_json field (the published endpoint definition)
        JsonElement endpointDef = found;
        if (found.TryGetProperty("definition_json", out var def) && def.ValueKind == JsonValueKind.String)
        {
            try { endpointDef = JsonSerializer.Deserialize<JsonElement>(def.GetString()!); }
            catch { /* keep the raw row if definition is malformed */ }
        }

        return NetworkStorageCandidateResult.Ok(
            new { ok = true, source = "scylladb", endpoint = endpointDef },
            storagePathsRead: new[] { $"scylladb://{projectId}/endpoints" },
            auth.KeyType);
    }

    private async Task<NetworkStorageCandidateResult> ReadFromBunnyAsync(
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
            return NetworkStorageCandidateResult.Error(
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
            return NetworkStorageCandidateResult.Error(
                404,
                "ENDPOINT_NOT_FOUND",
                new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = "Endpoint not found." } },
                storagePathsRead,
                auth.KeyType);
        }

        return NetworkStorageCandidateResult.Ok(
            new { ok = true, source = "cdn", endpoint = found },
            storagePathsRead,
            auth.KeyType);
    }

    private static NetworkStorageCandidateResult UnauthorizedResult() =>
        NetworkStorageCandidateResult.Error(
            401, "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: null);
}