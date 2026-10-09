using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native candidate for <c>GET /v1|v3/storage/:projectId/rate-limits</c>.
/// When <c>Scylla:Primary</c> is true, reads from ScyllaDB (rules from
/// <c>rate_limit_rules</c>, endpoint limits from <c>endpoints</c> definitions).
/// Falls back to the Bunny project service on ScyllaDB miss or connection error.
/// </summary>
public sealed class RateLimitsHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly INetworkStorageProjectService _projectService;
    private readonly INetworkStorageStore _scyllaStore;
    private readonly ILogger<RateLimitsHandler> _logger;

    public RateLimitsHandler(
        IStorageApiKeyResolver apiKeyResolver,
        INetworkStorageProjectService projectService,
        INetworkStorageStore scyllaStore,
        ILogger<RateLimitsHandler> logger)
    {
        _apiKeyResolver = apiKeyResolver;
        _projectService = projectService;
        _scyllaStore = scyllaStore;
        _logger = logger;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageRateLimits;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageRateLimits
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;

        var apiKey = request.Credentials.ApiKey ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            return Unauthorized(projectId);

        var auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        if (auth is null || !auth.Enabled)
            return Unauthorized(projectId);

        try
        {
            return await ReadFromStoreAsync(auth, projectId, request.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Store rate-limits read failed for project={ProjectId}; falling back to workspace objects", projectId);
        }

        return await ReadFromWorkspaceAsync(auth, projectId, request.CancellationToken);
    }

    private async Task<NetworkStorageResult> ReadFromStoreAsync(
        StorageApiKeyAuthResult auth, string projectId, CancellationToken ct)
    {
        // BuildRateLimitRulesRow returns { project_id, rules_json (parsed JsonElement), version, ... }
        var rulesRow = await _scyllaStore.ReadRateLimitRulesAsync(projectId, ct);
        JsonElement rulesElement;
        if (rulesRow is { } row && row.TryGetProperty("rules_json", out var rulesJson)
            && rulesJson.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            rulesElement = rulesJson;
        }
        else
        {
            rulesElement = JsonSerializer.SerializeToElement(Array.Empty<RateLimitRule>());
        }

        // Extract endpoint-level rate limits from ScyllaDB endpoint definitions
        var endpoints = await _scyllaStore.ListEndpointsAsync(projectId, ct);
        var endpointRateLimits = new Dictionary<string, object>();
        foreach (var ep in endpoints)
        {
            if (ep.ValueKind != JsonValueKind.Object) continue;
            var epId = ep.TryGetProperty("endpoint_id", out var eid) && eid.ValueKind == JsonValueKind.String
                ? eid.GetString() : null;
            if (epId is null) continue;
            if (ep.TryGetProperty("definition_json", out var def)
                && def.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                try
                {
                    var defObj = def.ValueKind == JsonValueKind.String
                        ? JsonSerializer.Deserialize<JsonElement>(def.GetString()!)
                        : def;
                    if (defObj.TryGetProperty("rateLimit", out var rl) && rl.ValueKind == JsonValueKind.Object)
                        endpointRateLimits[epId] = JsonSerializer.Deserialize<object>(rl.GetRawText()!)!;
                }
                catch { /* malformed definition — skip */ }
            }
        }

        return NetworkStorageResult.Ok(
            new { ok = true, projectId, source = "store", endpointRateLimits, rules = rulesElement },
            storagePathsRead: new[] { $"store://{projectId}/rate_limit_rules" },
            authDecision: auth.KeyType);
    }

    private async Task<NetworkStorageResult> ReadFromWorkspaceAsync(
        StorageApiKeyAuthResult auth, string projectId, CancellationToken ct)
    {
        var rateLimits = await _projectService.GetProjectRateLimitsAsync(auth.UserId, projectId, ct);
        var ser = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var rulesElement = rateLimits.Rules is not null
            ? JsonSerializer.SerializeToElement(rateLimits.Rules, ser)
            : JsonSerializer.SerializeToElement(Array.Empty<RateLimitRule>(), ser);

        return NetworkStorageResult.Ok(
            new { ok = true, projectId, source = "configuration", endpointRateLimits = rateLimits.EndpointRateLimits, rules = rulesElement },
            storagePathsRead: new[] { $"network-storage/users/{auth.UserId}/{projectId}/rate-limit-rules.json" },
            authDecision: auth.KeyType);
    }

    private static NetworkStorageResult Unauthorized(string projectId) =>
        NetworkStorageResult.Error(401, "UNAUTHORIZED",
            new { ok = false, error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." }, projectId, source = "auth" },
            storagePathsRead: Array.Empty<string>(), authDecision: "anonymous");
}
