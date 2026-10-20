using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for <c>GET /v3/manage/:projectId/*</c> routes. Mirrors legacy server's
/// management GET handlers: resolves auth via secret API key, reads CDN-backed resource JSON
/// files (endpoints, collections, workflows, queries, game-values, tests, game-package, rate-limit-rules,
/// settings/config, validate), and produces legacy-compatible response shapes.
///
/// <c>sync-jobs/:jobId</c> reads the in-memory <see cref="ManagementSyncJobStore"/> that
/// <c>PUT endpoints?async=true</c> writes. Routes without an implementation return a typed 501
/// <c>MANAGEMENT_ROUTE_NOT_IMPLEMENTED</c>; they never return a 200 for work that was not done.
/// </summary>
public sealed class ManagementReadHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly INetworkStorageProjectService _projectService;
    private readonly ManagementSyncJobStore _syncJobs;

    public ManagementReadHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageProjectService projectService,
        ManagementSyncJobStore? syncJobs = null)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _projectService = projectService;
        _syncJobs = syncJobs ?? new ManagementSyncJobStore();
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Management;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Management
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
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
                return NetworkStorageResult.Error(
                    403, "FORBIDDEN",
                    new { ok = false, error = new { code = "FORBIDDEN", message = "This key does not have permission for this operation." } },
                    storagePathsRead: Array.Empty<string>(), authDecision: "denied");
            }
        }

        var ownerUserId = auth.UserId;

        // ── Verify project exists and is enabled ──

        IReadOnlyList<WorkspaceProject> projects;
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
            return NetworkStorageResult.Error(
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
            "endpoints"        => await ReadDefinitionsAsync(request, ownerUserId, projectId, "endpoints.json",
                                   RevisionOverrides.EndpointsSection, auth.KeyType),
            "collections"      => await ReadDefinitionsAsync(request, ownerUserId, projectId, "collections.json",
                                   RevisionOverrides.CollectionsSection, auth.KeyType),
            "workflows"        => await ReadResourceJsonAsync(ownerUserId, projectId, "workflows.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "queries"          => await ReadResourceJsonAsync(ownerUserId, projectId, "queries.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "tests"            => await ReadResourceJsonAsync(ownerUserId, projectId, "tests.json",
                                   data => new { ok = true, data }, auth.KeyType, request.CancellationToken),
            "game-package"     => await HandleGamePackageAsync(ownerUserId, projectId, auth.KeyType, request.CancellationToken),
            "rate-limit-rules" => await HandleRateLimitRulesAsync(ownerUserId, projectId, auth.KeyType, request.CancellationToken),
            "settings" or "config" => await HandleSettingsAsync(project, ownerUserId, projectId, auth.KeyType),
            "validate"         => await HandleValidateAsync(request, apiKey, auth, projectId, ownerUserId),
            "sync-jobs"        => HandleSyncJob(request, ownerUserId, projectId, auth.KeyType),
            "agent-manifest"   => await HandleAgentManifestAsync(request, ownerUserId, project, auth.KeyType),
            _                  => NotImplementedResult(resourceName),
        };
    }

    // ── Generic resource JSON reader ──

    private async Task<NetworkStorageResult> ReadResourceJsonAsync(
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
            return NetworkStorageResult.Error(
                500,
                "MANAGEMENT_READ_ERROR",
                new { ok = false, error = new { code = "MANAGEMENT_READ_ERROR", message = $"Failed to read {fileName}." } },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind == JsonValueKind.Undefined)
        {
            var emptyData = shapeResponse(JsonSerializer.SerializeToElement(Array.Empty<object>()));
            return NetworkStorageResult.Ok(
                emptyData,
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        var body = shapeResponse(result.Value);
        return NetworkStorageResult.Ok(
            body,
            storagePathsRead: new[] { resourcePath },
            authDecision: authDecision);
    }

    // ── Endpoints / collections with the staged revision ──

    /// <summary>
    /// Live definitions, plus the staged ("next") items marked <c>revisionTarget: "next"</c> when the
    /// Sync Tool asks for <c>includeStaged=true</c> without pinning <c>revisionTarget=live</c>.
    /// Live items stay unmarked, matching the reference response.
    /// </summary>
    private async Task<NetworkStorageResult> ReadDefinitionsAsync(
        NetworkStorageRequest request, long userId, string projectId, string fileName, string stagedSection, string authDecision)
    {
        var ct = request.CancellationToken;
        IReadOnlyList<KeyValuePair<string, JsonObject>> staged = [];
        if (IncludesStaged(request))
        {
            try
            {
                staged = RevisionOverrides.Items(
                    await RevisionOverrides.ReadAsync(_workspaceClient, userId, projectId, ct), stagedSection);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return NetworkStorageResult.Error(
                    500,
                    "MANAGEMENT_READ_ERROR",
                    new { ok = false, error = new { code = "MANAGEMENT_READ_ERROR", message = $"Failed to read {RevisionOverrides.ResourcePath}." } },
                    storagePathsRead: new[] { $"network-storage/users/{userId}/{projectId}/{RevisionOverrides.ResourcePath}" },
                    authDecision: authDecision);
            }
        }

        Func<JsonElement, object> shape = staged.Count == 0
            ? data => new { ok = true, data }
            : data => new { ok = true, data = WithStaged(data, staged) };
        return await ReadResourceJsonAsync(userId, projectId, fileName, shape, authDecision, ct);
    }

    private static bool IncludesStaged(NetworkStorageRequest request)
        => request.QueryValue("includeStaged")?.Trim().ToLowerInvariant() is "true" or "1"
            && !string.Equals(request.QueryValue(NetworkStoragePublishTarget.QueryName)?.Trim(),
                NetworkStoragePublishTarget.Live, StringComparison.OrdinalIgnoreCase);

    private static JsonArray WithStaged(JsonElement live, IReadOnlyList<KeyValuePair<string, JsonObject>> staged)
    {
        var data = live.ValueKind == JsonValueKind.Array ? JsonNode.Parse(live.GetRawText())!.AsArray() : new JsonArray();
        foreach (var (_, item) in staged)
        {
            var copy = item.DeepClone().AsObject();
            copy["revisionTarget"] = NetworkStoragePublishTarget.Next;
            data.Add(copy);
        }
        return data;
    }

    // ── Game Package ──

    private async Task<NetworkStorageResult> HandleGamePackageAsync(
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
            return NetworkStorageResult.Ok(
                new { ok = true, gamePackage = (object?)null },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind == JsonValueKind.Undefined)
        {
            return NetworkStorageResult.Ok(
                new { ok = true, gamePackage = (object?)null },
                storagePathsRead: new[] { resourcePath },
                authDecision: authDecision);
        }

        // Add Unix timestamp convenience fields matching legacy server's withUnixTimestamps()
        var obj = new Dictionary<string, object?>();
        foreach (var prop in result.Value.EnumerateObject())
        {
            obj[prop.Name] = JsonValueToObject(prop.Value);
        }

        obj["revisionFirstSyncedAtUnix"] = TryParseUnixTimestamp(result.Value, "revisionPublishedAt");
        obj["lastSyncedAtUnix"] = TryParseUnixTimestamp(result.Value, "lastSyncedAt");

        return NetworkStorageResult.Ok(
            new { ok = true, gamePackage = (object)obj },
            storagePathsRead: new[] { resourcePath },
            authDecision: authDecision);
    }

    // Reference: routeManageGetAgentManifest (manage-api-controller.js L1118-1252). Reads the live
    // definitions only, never staged revisions. A missing game package only drops revision fields.
    private async Task<NetworkStorageResult> HandleAgentManifestAsync(
        NetworkStorageRequest request, long userId, WorkspaceProject project, string authDecision)
    {
        var ct = request.CancellationToken;
        var projectId = project.Id;
        var storagePaths = new[] { "endpoints.json", "game-package.json", "collections.json", "workflows.json" }
            .Select(file => $"network-storage/users/{userId}/{projectId}/{file}")
            .ToArray();

        JsonElement? endpoints;
        JsonElement? collections;
        JsonElement? workflows;
        try
        {
            endpoints = await ReadProjectFileAsync(userId, projectId, "endpoints.json", ct);
            collections = await ReadProjectFileAsync(userId, projectId, "collections.json", ct);
            workflows = await ReadProjectFileAsync(userId, projectId, "workflows.json", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return NetworkStorageResult.Error(
                500,
                "MANAGEMENT_READ_ERROR",
                new { ok = false, error = new { code = "MANAGEMENT_READ_ERROR", message = "Failed to read the agent manifest sources." } },
                storagePathsRead: storagePaths,
                authDecision: authDecision);
        }

        JsonElement? gamePackage;
        try
        {
            gamePackage = await ReadProjectFileAsync(userId, projectId, "game-package.json", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            gamePackage = null;
        }

        var manifest = ManagementAgentManifest.Build(
            project,
            projectId,
            request.ApiBaseUrl ?? string.Empty,
            endpoints.GetValueOrDefault(),
            gamePackage.GetValueOrDefault(),
            collections.GetValueOrDefault(),
            workflows.GetValueOrDefault(),
            request.Query);
        manifest.Insert(0, "ok", true);

        return NetworkStorageResult.Ok(manifest, storagePathsRead: storagePaths, authDecision: authDecision);
    }

    private async Task<JsonElement?> ReadProjectFileAsync(long userId, string projectId, string fileName, CancellationToken ct)
        => await _workspaceClient.GetProjectResourceAsync<JsonElement>(userId, projectId, fileName, ct);

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

    private async Task<NetworkStorageResult> HandleRateLimitRulesAsync(
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
            return NetworkStorageResult.Error(
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

        return NetworkStorageResult.Ok(
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

    private Task<NetworkStorageResult> HandleSettingsAsync(
        WorkspaceProject project, long userId, string projectId, string authDecision)
    {
        // Shared with PUT settings so both routes return the same projection.
        var settings = ManagementSettings.View(project);

        // legacy server structures for backward compatibility — settings and config carry the same data
        var enableAuthSessions = project.EnableAuthSessions == true;
        var enableEncryptedRequests = EncryptedRequestModes.RequiresEncryption(project.EncryptedRequestMode);

        var response = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["projectId"] = projectId,
            ["settings"] = settings,
            ["config"] = settings,
            ["enableAuthSessions"] = enableAuthSessions,
            ["enableEncryptedRequests"] = enableEncryptedRequests,
        };

        return Task.FromResult(NetworkStorageResult.Ok(
            response,
            storagePathsRead: new[] { $"network-storage/users/{userId}/{projectId}/projects.json" },
            authDecision: authDecision));
    }

    // ── Validate ──

    private async Task<NetworkStorageResult> HandleValidateAsync(
        NetworkStorageRequest request, string apiKey, StorageApiKeyAuthResult auth, string projectId, long ownerUserId)
    {
        // legacy server's routeManageValidate builds a checks result: projectId check + secretKey check
        // Since we already resolved the key to reach here, both checks succeed. The library's Setup window
        // sends the game's public key in x-public-key, so report on it instead of always "Not provided".
        var checks = new Dictionary<string, object>
        {
            ["projectId"] = new { ok = true, message = "Found" },
            ["secretKey"] = new { ok = true, message = "Valid" },
            ["publicKey"] = await CheckPublicKeyAsync(request.PublicKey, projectId, request.CancellationToken),
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

        return NetworkStorageResult.Ok(
            response,
            storagePathsRead: Array.Empty<string>(),
            authDecision: auth.KeyType);
    }

    private async Task<object> CheckPublicKeyAsync(string? publicKey, string projectId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(publicKey))
            return new { ok = false, message = "Not provided" };

        StorageApiKeyAuthResult? resolved;
        try
        {
            resolved = await _apiKeyResolver.ResolveApiKeyAsync(publicKey, projectId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            resolved = null;
        }

        if (resolved is null || !resolved.Enabled)
            return new { ok = false, message = "Invalid" };
        return string.Equals(resolved.KeyType, "public", StringComparison.OrdinalIgnoreCase)
            ? new { ok = true, message = "Valid" }
            : new { ok = false, message = "Not a public key" };
    }

    // ── Sync jobs (async endpoint sync; see ManagementSyncJobStore) ──

    private NetworkStorageResult HandleSyncJob(NetworkStorageRequest request, long userId, string projectId, string authDecision)
    {
        if (!request.Route.RouteParameters.TryGetValue("jobId", out var jobId) || string.IsNullOrWhiteSpace(jobId))
        {
            return NetworkStorageResult.Error(
                400, "MISSING_JOB_ID",
                new { ok = false, error = "MISSING_JOB_ID" },
                storagePathsRead: Array.Empty<string>(), authDecision: authDecision);
        }

        var lookup = _syncJobs.Lookup(jobId, userId, projectId);
        return lookup.State switch
        {
            SyncJobLookupState.Found => NetworkStorageResult.Ok(
                new { ok = true, job = lookup.Job },
                storagePathsRead: Array.Empty<string>(), authDecision: authDecision),
            SyncJobLookupState.Expired => NetworkStorageResult.Error(
                404, "JOB_EXPIRED",
                new { ok = false, error = "JOB_EXPIRED", message = "Sync job has expired and been cleaned up." },
                storagePathsRead: Array.Empty<string>(), authDecision: authDecision),
            _ => NetworkStorageResult.Error(
                404, "JOB_NOT_FOUND",
                new { ok = false, error = "JOB_NOT_FOUND" },
                storagePathsRead: Array.Empty<string>(), authDecision: authDecision),
        };
    }

    // ── Helpers ──

    // There are no separate package/test scopes. Those resources can contain
    // definitions from every category, so require every documented read scope.
    private static string[] RequiredReadScopes(string resourceName) => resourceName switch
    {
        "endpoints" or "collections" or "workflows" or "queries" => [resourceName],
        "game-values" => ["game_values"],
        "rate-limit-rules" => ["rate_limits"],
        "settings" or "config" => ["settings"],
        // Jobs are created by endpoint sync and carry endpoint results, so they need the endpoints read scope.
        "sync-jobs" => ["endpoints"],
        "agent-manifest" => ["endpoints"],
        "tests" or "game-package" =>
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

    private static NetworkStorageResult NotImplementedResult(string routeName)
    {
        return NetworkStorageResult.Error(
            501,
            "MANAGEMENT_ROUTE_NOT_IMPLEMENTED",
            new
            {
                ok = false,
                error = "MANAGEMENT_ROUTE_NOT_IMPLEMENTED",
                message = $"The route '{routeName}' is not implemented.",
            },
            storagePathsRead: Array.Empty<string>(),
            authDecision: null);
    }

    private static NetworkStorageResult ManagementAuthError()
    {
        return NetworkStorageResult.Error(
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
