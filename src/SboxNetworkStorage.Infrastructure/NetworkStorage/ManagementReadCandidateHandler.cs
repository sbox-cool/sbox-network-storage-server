using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native candidate for <c>GET /v3/manage/:projectId/*</c> routes. Mirrors Bun's
/// management GET handlers: resolves auth via secret API key, reads CDN-backed resource JSON
/// files (endpoints, collections, workflows, queries, game-values, tests, game-package, rate-limit-rules,
/// settings/config, validate), and produces Bun-compatible response shapes.
///
/// Two routes are explicitly unsupported:
///  <list type="bullet">
///    <item><c>sync-jobs/:jobId</c> — sync jobs are ephemeral in-memory state (Map in Bun) with no CDN persistence.</item>
///    <item><c>agent-manifest</c> — complex aggregate combining endpoints, game-package, and manifest
///          metadata with URL construction; deferred.</item>
///  </list>
/// Unsupported routes return a 200 <c>MANAGEMENT_ROUTE_NOT_IMPLEMENTED</c> result.
/// </summary>
public sealed class ManagementReadCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IBunnyWorkspaceClient _workspaceClient;
    private readonly INetworkStorageProjectService _projectService;

    public ManagementReadCandidateHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IBunnyWorkspaceClient workspaceClient,
        INetworkStorageProjectService projectService)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _projectService = projectService;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Management;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Management
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var resourceName = ExtractResourceName(request.Route);
        if (resourceName is null)
        {
            return NotImplementedResult(resourceName ?? "(unknown)");
        }

        // ── Auth for all management routes ──

        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return ManagementAuthError();
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return ManagementAuthError();
        }

        if (auth is null || !auth.Enabled)
        {
            return ManagementAuthError();
        }

        // Management routes require a SECRET key, not a public key
        if (!string.Equals(auth.KeyType, "secret", StringComparison.Ordinal))
        {
            return ManagementAuthError();
        }

        foreach (var scope in RequiredReadScopes(resourceName))
        {
            if (!ApiKeyPermissionPolicy.HasPermission(auth, scope, "r"))
            {
                return NetworkStorageCandidateResult.Error(
                    403, "FORBIDDEN",
                    new { ok = false, error = new { code = "FORBIDDEN", message = "This key does not have permission for this operation." } },
                    storagePathsRead: Array.Empty<string>(), authDecision: "denied");
            }
        }

        var ownerUserId = auth.UserId;

        // ── Verify project exists and is enabled ──

        IReadOnlyList<BunnyProject> projects;
        try
        {
            projects = await _workspaceClient.GetUserProjectsAsync(ownerUserId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return ManagementAuthError();
        }

        var project = projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null)
        {
            return ManagementAuthError();
        }

        if (!project.Enabled)
        {
            return NetworkStorageCandidateResult.Error(
                403,
                "PROJECT_DISABLED",
                new { ok = false, error = new { code = "PROJECT_DISABLED", message = "This project is currently disabled." } },
                storagePathsRead: Array.Empty<string>(),
                authDecision: auth.KeyType);
        }

        // ── Dispatch by resource name ──

        return resourceName switch
        {
            "game-values"      => await ReadResourceJsonAsync(ownerUserId, projectId, "game-values.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "endpoints"        => await ReadResourceJsonAsync(ownerUserId, projectId, "endpoints.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "collections"      => await ReadResourceJsonAsync(ownerUserId, projectId, "collections.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "workflows"        => await ReadResourceJsonAsync(ownerUserId, projectId, "workflows.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "queries"          => await ReadResourceJsonAsync(ownerUserId, projectId, "queries.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "tests"            => await ReadResourceJsonAsync(ownerUserId, projectId, "tests.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "game-package"     => await HandleGamePackageAsync(ownerUserId, projectId, auth.KeyType, request.CancellationToken),
            "rate-limit-rules" => await HandleRateLimitRulesAsync(ownerUserId, projectId, auth.KeyType, request.CancellationToken),
            "settings" or "config" => await HandleSettingsAsync(project, ownerUserId, projectId, auth.KeyType),
            "validate"         => await HandleValidateAsync(apiKey, auth, projectId, ownerUserId),
            "sync-jobs"        => NotImplementedResult("sync-jobs/:jobId"),
            "agent-manifest"   => NotImplementedResult("agent-manifest"),
            _                  => NotImplementedResult(resourceName),
        };
    }

    // ── Generic resource JSON reader ──

    private async Task<NetworkStorageCandidateResult> ReadResourceJsonAsync(
        long userId, string projectId, string fileName, Func<JsonElement, object> shapeResponse, string authDecision, CancellationToken ct)
    {
        var resourcePath = $"network-storage/users/{userId}/{projectId}/{fileName}";

        JsonElement? result;
        try
        {
            result = await _workspaceClient.GetProjectResourceAsync<JsonElement>(
                userId, projectId, fileName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return NetworkStorageCandidateResult.Error(
                500,
                "MANAGEMENT_READ_ERROR",
                new { ok = false, error = new { code = "MANAGEMENT_READ_ERROR", message = $"Failed to read {fileName}." } },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind == JsonValueKind.Undefined)
        {
            var emptyData = shapeResponse(JsonSerializer.SerializeToElement(Array.Empty<object>()));
            return NetworkStorageCandidateResult.Ok(
                emptyData,
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        var body = shapeResponse(result.Value);
        return NetworkStorageCandidateResult.Ok(
            body,
            storagePathsRead: new[] { resourcePath },
            authDecision: authDecision);
    }

    // ── Game Package ──

    private async Task<NetworkStorageCandidateResult> HandleGamePackageAsync(
        long userId, string projectId, string authDecision, CancellationToken ct)
    {
        const string fileName = "game-package.json";
        var resourcePath = $"network-storage/users/{userId}/{projectId}/{fileName}";

        JsonElement? result;
        try
        {
            result = await _workspaceClient.GetProjectResourceAsync<JsonElement>(
                userId, projectId, fileName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return NetworkStorageCandidateResult.Ok(
                new { ok = true, gamePackage = (object?)null },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind == JsonValueKind.Undefined)
        {
            return NetworkStorageCandidateResult.Ok(
                new { ok = true, gamePackage = (object?)null },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        // Add Unix timestamp convenience fields matching Bun's withUnixTimestamps()
        var obj = new Dictionary<string, object?>();
        foreach (var prop in result.Value.EnumerateObject())
        {
            obj[prop.Name] = JsonValueToObject(prop.Value);
        }

        obj["revisionFirstSyncedAtUnix"] = TryParseUnixTimestamp(result.Value, "revisionPublishedAt");
        obj["lastSyncedAtUnix"] = TryParseUnixTimestamp(result.Value, "lastSyncedAt");

        return NetworkStorageCandidateResult.Ok(
            new { ok = true, gamePackage = (object)obj },
            storagePathsRead: new[] { resourcePath },
            authDecision: authDecision);
    }

    private static long? TryParseUnixTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var isoString = el.GetString();
        if (string.IsNullOrEmpty(isoString))
            return null;

        if (DateTimeOffset.TryParse(isoString, out var dto))
            return dto.ToUnixTimeSeconds();

        return null;
    }

    // ── Rate Limit Rules ──

    private async Task<NetworkStorageCandidateResult> HandleRateLimitRulesAsync(
        long userId, string projectId, string authDecision, CancellationToken ct)
    {
        ProjectRateLimits rateLimits;
        try
        {
            rateLimits = await _projectService.GetProjectRateLimitsAsync(userId, projectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return NetworkStorageCandidateResult.Error(
                500,
                "MANAGEMENT_READ_ERROR",
                new { ok = false, error = new { code = "MANAGEMENT_READ_ERROR", message = "Failed to read rate limit rules." } },
                storagePathsRead: new[] { $"network-storage/users/{userId}/{projectId}/rate-limit-rules.json" },
                authDecision: authDecision);
        }

        var serializationOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var rulesElement = rateLimits.Rules is not null
            ? JsonSerializer.SerializeToElement(rateLimits.Rules, serializationOptions)
            : JsonSerializer.SerializeToElement(Array.Empty<RateLimitRule>(), serializationOptions);

        return NetworkStorageCandidateResult.Ok(
            new
            {
                ok = true,
                projectId,
                source = "configuration",
                endpointRateLimits = rateLimits.EndpointRateLimits,
                rules = rulesElement
            },
            storagePathsRead: new[] { $"network-storage/users/{userId}/{projectId}/rate-limit-rules.json" },
            authDecision: authDecision);
    }

    // ── Settings / Config ──

    private Task<NetworkStorageCandidateResult> HandleSettingsAsync(
        BunnyProject project, long userId, string projectId, string authDecision)
    {
        // Mirrors Bun's projectSettingsForManage() — derives settings from the BunnyProject model
        var settings = new Dictionary<string, object?>
        {
            ["enabled"] = project.Enabled,
            ["requireSboxAuth"] = project.RequireSboxAuth == true,
            ["enableAuthSessions"] = project.EnableAuthSessions == true,
            ["authSessionTtlSeconds"] = project.AuthSessionTtlSeconds ?? 3600,
            ["enableEncryptedRequests"] = project.EnableEncryptedRequests == true,
            ["encryptedRequestWindowSeconds"] = project.EncryptedRequestWindowSeconds ?? 300,
            ["playerKeyMode"] = project.PlayerKeyMode ?? "player",
            ["revisionEnforcementEnabled"] = project.RevisionEnforcementEnabled == true,
            ["revisionEnforcementMode"] = project.RevisionEnforcementMode ?? "allow_continue",
            ["revisionGracePeriodMinutes"] = (object?)project.RevisionGracePeriodMinutes,
            ["revisionPostGraceAction"] = project.RevisionPostGraceAction ?? "block_writes",
            ["revisionForceEndpointUpgrade"] = project.RevisionForceEndpointUpgrade == true,
            ["revisionNotifyMessage"] = project.RevisionNotifyMessage ?? "",
            ["revisionShowDefaultMessage"] = project.RevisionShowDefaultMessage == true,
            ["revisionShowNewVersionBanner"] = project.RevisionShowNewVersionBanner != false,
            ["revisionShowUpdateOptions"] = project.RevisionShowUpdateOptions != false,
            ["revisionEscalateAfterMinutes"] = (object?)project.RevisionEscalateAfterMinutes,
            ["revisionShowPopupOnce"] = project.RevisionShowPopupOnce != false,
            ["revisionTestOutdatedEditor"] = project.RevisionTestOutdatedEditor == true,
            ["revisionTestOutdatedLive"] = project.RevisionTestOutdatedLive == true,
        };

        // Bun structures for backward compatibility — settings and config carry the same data
        var enableAuthSessions = project.EnableAuthSessions == true;
        var enableEncryptedRequests = project.EnableEncryptedRequests == true;

        var response = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["projectId"] = projectId,
            ["settings"] = settings,
            ["config"] = settings,
            ["enableAuthSessions"] = enableAuthSessions,
            ["enableEncryptedRequests"] = enableEncryptedRequests,
        };

        return Task.FromResult(NetworkStorageCandidateResult.Ok(
            response,
            storagePathsRead: new[] { $"network-storage/users/{userId}/{projectId}/projects.json" },
            authDecision: authDecision));
    }

    // ── Validate ──

    private Task<NetworkStorageCandidateResult> HandleValidateAsync(
        string apiKey, StorageApiKeyAuthResult auth, string projectId, long ownerUserId)
    {
        // Bun's routeManageValidate builds a checks result: projectId check + secretKey check
        // Since we already resolved the key to reach here, both checks succeed.
        var checks = new Dictionary<string, object>
        {
            ["projectId"] = new { ok = true, message = "Found" },
            ["secretKey"] = new { ok = true, message = "Valid" },
            ["publicKey"] = new { ok = false, message = "Not provided" },
        };

        var projectInfo = new Dictionary<string, object?>
        {
            ["id"] = projectId,
            ["title"] = projectId,
            ["settings"] = new Dictionary<string, object>
            {
                ["enabled"] = auth.Enabled,
            },
        };

        var response = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["project"] = projectInfo,
            ["checks"] = checks,
        };

        return Task.FromResult(NetworkStorageCandidateResult.Ok(
            response,
            storagePathsRead: Array.Empty<string>(),
            authDecision: auth.KeyType));
    }

    // ── Helpers ──

    // There are no separate package/test scopes. Those resources can contain
    // definitions from every category, so require every documented read scope.
    private static string[] RequiredReadScopes(string resourceName) => resourceName switch
    {
        "endpoints" or "collections" or "workflows" or "queries" => [resourceName],
        "game-values" => ["game_values"],
        "rate-limit-rules" => ["rate_limits"],
        "settings" or "config" or "sync-jobs" => ["settings"],
        "tests" or "game-package" or "agent-manifest" =>
            ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"],
        // Validation reports credential validity, not resource definitions.
        _ => [],
    };

    /// <summary>Extracts the resource name segment from a management route template.</summary>
    /// <example>
    /// <c>/v3/manage/:projectId/endpoints</c> → <c>"endpoints"</c>
    /// <c>/v3/manage/:projectId/sync-jobs/:jobId</c> → <c>"sync-jobs"</c>
    /// </example>
    private static string? ExtractResourceName(NetworkStorageRouteClassification route)
    {
        var entry = route.Entry;
        if (entry is null) return null;

        var template = entry.Template;
        if (string.IsNullOrEmpty(template)) return null;

        // Template is like "v3/manage/:projectId/endpoints"
        var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments[0]=v3, segments[1]=manage, segments[2]=:projectId, segments[3]=resource
        if (segments.Length < 4) return null;

        var resource = segments[3];
        // Strip leading ':' in case it's a parameter segment
        if (resource.Length > 0 && resource[0] == ':')
            return null;

        return resource;
    }

    private static NetworkStorageCandidateResult NotImplementedResult(string routeName)
    {
        return NetworkStorageCandidateResult.Ok(
            new
            {
                ok = false,
                error = "MANAGEMENT_ROUTE_NOT_IMPLEMENTED",
                message = $"The route '{routeName}' is not implemented in the native .NET candidate. Bun handles it.",
            },
            storagePathsRead: Array.Empty<string>(),
            authDecision: null);
    }

    private static NetworkStorageCandidateResult ManagementAuthError()
    {
        return NetworkStorageCandidateResult.Error(
            401,
            "UNAUTHORIZED",
            new { ok = false, error = new { code = "UNAUTHORIZED", message = "Invalid or missing management API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: "denied");
    }

    private static object? JsonValueToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => JsonValueToObject(p.Value)),
            JsonValueKind.Array => value.EnumerateArray().Select(JsonValueToObject).ToList(),
            _ => value.GetRawText(),
        };
    }
}
