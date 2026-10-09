using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Native candidate for management mutation routes under <c>/v3/manage/:projectId/*</c>.
/// Performs real ScyllaDB writes when invoked from a production native route
/// (<c>SuppressSideEffects = false</c>); returns a Bun-compatible dry-run diagnostic
/// when invoked from the shadow pipeline (<c>SuppressSideEffects = true</c>).
/// </summary>
internal static class ManagementMutationConstants
{
    /// <summary>Base CDN path segment for user/project resource files.</summary>
    public const string UserStoragePrefix = "network-storage/users";

    /// <summary>Dry-run suppression reason string.</summary>
    public const string WriteSuppressedReason = "write_suppressed_dry_run";

    /// <summary>Public error code for unsupported mutation routes.</summary>
    public const string NotImplementedCode = "MANAGEMENT_MUTATION_NOT_IMPLEMENTED";

    /// <summary>Public error code for validation failures.</summary>
    public const string ValidationFailedCode = "VALIDATION_FAILED";
}

public sealed partial class ManagementMutationCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IBunnyWorkspaceClient _workspaceClient;
    private readonly INetworkStorageStore _store;
    private readonly TimeProvider _time;
    private readonly NativeEndpointShadowExecutor? _endpointExecutor;
    private readonly IQueryValuesContextProvider? _valuesProvider;

    public ManagementMutationCandidateHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IBunnyWorkspaceClient workspaceClient,
        INetworkStorageStore store,
        TimeProvider time)
        : this(apiKeyResolver, workspaceClient, store, time, endpointExecutor: null)
    {
    }

    public ManagementMutationCandidateHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IBunnyWorkspaceClient workspaceClient,
        INetworkStorageStore store,
        TimeProvider time,
        NativeEndpointShadowExecutor? endpointExecutor,
        IQueryValuesContextProvider? valuesProvider = null)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _store = store;
        _time = time;
        _endpointExecutor = endpointExecutor;
        _valuesProvider = valuesProvider;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Management;

    /// <summary>
    /// Owner-panel adapter sharing the editor compiler and native management writes; never creates a credential.
    /// <paramref name="versionSource"/> labels the endpoint/workflow version snapshot this save records.
    /// </summary>
    public async Task<NetworkStorageCandidateResult> SaveOwnerResourceAsync(
        long ownerUserId, string projectId, string kind, JsonElement resource, CancellationToken ct,
        string versionSource = "owner-dashboard")
    {
        var projects = await _workspaceClient.GetUserProjectsAsync(ownerUserId, ct);
        if (!projects.Any(project => project.Id == projectId))
            return ManagementAuthError();
        if (kind is not ("collection" or "endpoint" or "workflow" or "query" or "game-values"))
            return ValidationFailedResult(kind, "Unknown resource kind.");
        if (resource.ValueKind != JsonValueKind.Object)
            return ValidationFailedResult(kind, "The definition must be a JSON object.");
        var compiled = resource.Clone();
        if (kind != "game-values")
        {
            if (!NetworkStorageSourceResourceCompiler.TryCompile(resource, kind, out compiled, out var error))
                return ValidationFailedResult(kind, error ?? "Source compilation failed.");
            var id = GetOptionalString(compiled, "id") ?? GetOptionalString(compiled, kind == "endpoint" ? "slug" : "name");
            var wrapperId = GetOptionalString(resource, "id") ?? GetOptionalString(resource, kind == "endpoint" ? "slug" : "name");
            if (id is null || !StorageIdValidation.IsValidCollectionId(id))
                return ValidationFailedResult(kind, "Provide a valid resource id.");
            if (wrapperId is not null && wrapperId != id)
                return ValidationFailedResult(kind, "Source id must match the resource wrapper id. Keep the existing id when editing.");
        }
        var section = kind switch { "game-values" => kind, "query" => "queries", _ => kind + "s" };
        var route = NetworkStorageRouteClassifier.Classify("POST", $"/v3/manage/{projectId}/{section}");
        var request = new NetworkStorageCandidateRequest(route, new Dictionary<string, string>(), "application/json",
            new Dictionary<string, bool>(), NetworkStorageCredentials.None, compiled.GetRawText(), ownerUserId, ct)
            { SuppressSideEffects = false };
        if (kind == "game-values") return await PutGameValuesAsync(request, projectId);
        if (kind == "query")
        {
            request = request with { Body = JsonSerializer.Serialize(new[] { compiled }) };
            var result = await PutQueriesAsync(request, projectId);
            if (result.StatusCode < 400)
                NativeQueryExecutor.ClearQueryCache(GetOptionalString(compiled, "id") ?? GetOptionalString(compiled, "name") ?? "", projectId);
            return result;
        }
        request = request with { Body = JsonSerializer.Serialize(new Dictionary<string, object> { [kind + "s"] = new[] { compiled } }) };
        var preflight = await PreflightSyncAsync(request, projectId, "owner");
        return preflight.StatusCode >= 400 ? preflight : await PutSyncAsync(request, projectId, ownerUserId, versionSource);
    }

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Management
        && IsMutationMethod(route.Method);

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var method = request.Route.Method;
        var resourcePath = ExtractResourcePath(request.Route);
        if (resourcePath is null)
        {
            return NotImplementedResult(resourcePath ?? "(unknown)");
        }

        // ── Auth for all management mutation routes ──

        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return ManagementAuthError();
        }

        var projectId = request.ProjectId ?? string.Empty;

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

        // Management mutation routes require a SECRET key, not a public key
        if (!string.Equals(auth.KeyType, "secret", StringComparison.Ordinal))
        {
            return ManagementAuthError();
        }

        var permissionError = AuthorizeMutation(request, resourcePath, auth);
        if (permissionError is not null)
        {
            return permissionError;
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
            return ProjectDisabledResult(auth.KeyType);
        }

        var key = $"{method}:{resourcePath}";
        return key switch
        {
            "PUT:game-values" or "POST:game-values" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/game-values.json",
                    action: "write_resource", route: $"{method} /v3/manage/{projectId}/game-values",
                    resourceKind: "game-values")
                : await PutGameValuesAsync(request, projectId),

            "DELETE:game-values" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/game-values.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/game-values",
                    resourceKind: "game-values")
                : await DeleteGameValuesAsync(request, projectId),

            "PUT:endpoints" or "POST:endpoints" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/endpoints.json",
                    action: "write_resource", route: $"{method} /v3/manage/{projectId}/endpoints",
                    resourceKind: "endpoints")
                : await PutEndpointsAsync(request, projectId, ownerUserId),

            "DELETE:endpoints" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/endpoints.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/endpoints/:endpointId",
                    resourceKind: "endpoint")
                : await DeleteEndpointsAsync(request, projectId),

            "PUT:collections" or "POST:collections" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/collections.json",
                    action: "write_resource", route: $"{method} /v3/manage/{projectId}/collections",
                    resourceKind: "collections")
                : await PutCollectionsAsync(request, projectId),

            "DELETE:collections" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/collections.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/collections/:collectionId",
                    resourceKind: "collection")
                : await DeleteCollectionsAsync(request, projectId),

            "PUT:workflows" or "POST:workflows" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/workflows.json",
                    action: "write_resource", route: $"{method} /v3/manage/{projectId}/workflows",
                    resourceKind: "workflows")
                : await PutWorkflowsAsync(request, projectId, ownerUserId),

            "DELETE:workflows" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/workflows.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/workflows/:workflowId",
                    resourceKind: "workflow")
                : await DeleteWorkflowsAsync(request, projectId),

            "PUT:rate-limit-rules" or "POST:rate-limit-rules" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/rate-limit-rules.json",
                    action: "write_resource", route: $"{method} /v3/manage/{projectId}/rate-limit-rules",
                    resourceKind: "rate-limit-rules")
                : await PutRateLimitRulesAsync(request, projectId),

            "DELETE:rate-limit-rules" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/rate-limit-rules.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/rate-limit-rules",
                    resourceKind: "rate-limit-rules")
                : await DeleteRateLimitRulesAsync(request, projectId),

            "POST:queries" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/queries.json",
                    action: "write_resource", route: $"POST /v3/manage/{projectId}/queries",
                    resourceKind: "queries")
                : await PutQueriesAsync(request, projectId),

            "DELETE:queries" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/queries.json",
                    action: "delete_resource", route: $"DELETE /v3/manage/{projectId}/queries/:queryId",
                    resourceKind: "query")
                : await DeleteQueriesAsync(request, projectId),

            "PUT:settings" => DryRunResult(ownerUserId, projectId, auth.KeyType,
                $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/projects.json",
                action: "update_project_settings", route: $"PUT /v3/manage/{projectId}/settings",
                resourceKind: "project-settings"),

            "PATCH:endpoints" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/endpoints.json",
                    action: "upsert_resource", route: $"PATCH /v3/manage/{projectId}/endpoints",
                    resourceKind: "endpoint")
                : await PatchResourceAsync(request, projectId, ownerUserId, "endpoint"),

            "PATCH:collections" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/collections.json",
                    action: "upsert_resource", route: $"PATCH /v3/manage/{projectId}/collections",
                    resourceKind: "collection")
                : await PatchResourceAsync(request, projectId, ownerUserId, "collection"),

            "PATCH:workflows" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/workflows.json",
                    action: "upsert_resource", route: $"PATCH /v3/manage/{projectId}/workflows",
                    resourceKind: "workflow")
                : await PatchResourceAsync(request, projectId, ownerUserId, "workflow"),

            "PUT:tests" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/tests.json",
                    action: "write_resource", route: $"PUT /v3/manage/{projectId}/tests",
                    resourceKind: "tests")
                : await PutTestsAsync(request, projectId, ownerUserId),

            "DELETE:keys" => DryRunResult(ownerUserId, projectId, auth.KeyType,
                $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/key-index.json",
                $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/keys/__{ExtractPublicKeyFromRequest(request)}.json",
                action: "destroy_key_pair", route: $"DELETE /v3/manage/{projectId}/keys",
                resourceKind: "api-keys"),

            "PUT:sync" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    new[]
                    {
                        $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/endpoints.json",
                        $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/collections.json",
                        $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/workflows.json",
                    },
                    action: "sync_project", route: $"PUT /v3/manage/{projectId}/sync",
                    resourceKind: "project-sync")
                : await PutSyncAsync(request, projectId, ownerUserId, "sync"),

            "POST:source-upgrade" => DryRunResult(ownerUserId, projectId, auth.KeyType,
                Array.Empty<string>(),
                action: "source_upgrade", route: $"POST /v3/manage/{projectId}/source-upgrade",
                resourceKind: "project-source"),

            "POST:package-sync" => DryRunResult(ownerUserId, projectId, auth.KeyType,
                $"{ManagementMutationConstants.UserStoragePrefix}/{ownerUserId}/{projectId}/game-package.json",
                action: "package_sync", route: $"POST /v3/manage/{projectId}/package-sync",
                resourceKind: "game-package"),

            "POST:auto-test" => await AutoTestAsync(request, projectId, ownerUserId, auth.KeyType),

            "POST:run-tests" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    Array.Empty<string>(),
                    action: "run_tests", route: $"POST /v3/manage/{projectId}/run-tests",
                    resourceKind: "run-tests")
                : await RunSavedTestsAsync(projectId, ownerUserId, project.PlayerKeyMode, auth.KeyType, request.CancellationToken),

            "POST:test-endpoint" => request.SuppressSideEffects
                ? DryRunResult(ownerUserId, projectId, auth.KeyType,
                    Array.Empty<string>(),
                    action: "test_endpoint", route: $"POST /v3/manage/{projectId}/test-endpoint",
                    resourceKind: "test-endpoint")
                : await TestEndpointAsync(request, projectId, ownerUserId, project.PlayerKeyMode, auth.KeyType),

            "POST:suggest-tests" => DryRunResult(ownerUserId, projectId, auth.KeyType,
                Array.Empty<string>(),
                action: "suggest_tests", route: $"POST /v3/manage/{projectId}/suggest-tests",
                resourceKind: "suggest-tests"),

            "POST:sync/preflight" => await PreflightSyncAsync(request, projectId, auth.KeyType),

            _ => NotImplementedResult(resourcePath),
        };
    }

    private Task<NetworkStorageCandidateResult> AutoTestAsync(
        NetworkStorageCandidateRequest request,
        string projectId,
        long ownerUserId,
        string authDecision) =>
        NetworkStorageManagementAutoTestRunner.RunAsync(
            request,
            projectId,
            ownerUserId,
            authDecision,
            _store,
            _endpointExecutor);

    // ── Production write helpers ──

    private static readonly string[] AllManagementScopes =
        ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"];

    private static NetworkStorageCandidateResult? AuthorizeMutation(
        NetworkStorageCandidateRequest request, string resourcePath, StorageApiKeyAuthResult auth)
    {
        var level = resourcePath is "auto-test" or "run-tests" or "test-endpoint" ? "x" : "rw";
        IEnumerable<string> scopes = MutationScopes(resourcePath);
        if (resourcePath is "sync" or "sync/preflight")
        {
            // Unparseable bodies are rejected by the handler (with the client-contract
            // error shape) before any section is read or written, so deferring is safe.
            if (!TryParseBody(request.Body, out var document, out _))
                return null;
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return ValidationFailedResult("project-sync", "Body must be an object with resource sections.");

                // Authorize all supplied categories before any section is read or
                // written. Empty/null sections must not bypass this batch gate.
                foreach (var section in document.RootElement.EnumerateObject())
                {
                    foreach (var scope in MutationScopes(section.Name))
                    {
                        if (!ApiKeyPermissionPolicy.HasPermission(auth, scope, "rw"))
                            return PermissionDeniedResult();
                    }
                }
            }
            return null;
        }

        foreach (var scope in scopes)
        {
            if (!ApiKeyPermissionPolicy.HasPermission(auth, scope, level))
                return PermissionDeniedResult();
        }
        return null;
    }

    // Package and test payloads cross resource categories; no additional
    // permission namespace is introduced for them.
    private static string[] MutationScopes(string resourcePath) => resourcePath switch
    {
        "endpoints" or "collections" or "workflows" or "queries" => [resourcePath],
        "game-values" or "gameValues" or "game_values" => ["game_values"],
        "rate-limit-rules" or "rateLimitRules" or "rate_limits" => ["rate_limits"],
        "settings" or "config" or "keys" => ["settings"],
        "auto-test" or "run-tests" or "test-endpoint" => ["endpoints", "queries", "collections"],
        "tests" or "suggest-tests" or "game-package" or "gamePackage" or "package-sync" or "source-upgrade" => AllManagementScopes,
        _ => [],
    };

    private static NetworkStorageCandidateResult PermissionDeniedResult() =>
        NetworkStorageCandidateResult.Error(
            403, "FORBIDDEN",
            new { ok = false, error = new { code = "FORBIDDEN", message = "This key does not have permission for this operation." } },
            storagePathsRead: Array.Empty<string>(), authDecision: "denied");

    private async Task<NetworkStorageCandidateResult> PutGameValuesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!TryParseBody(request.Body, out var doc, out var error))
        {
            return ValidationFailedResult("game-values", error);
        }

        var version = NextVersion();
        await _store.UpsertGameValuesAsync(projectId, doc.RootElement, versionHash: null, version, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "game-values", action = "upsert" });
    }

    private async Task<NetworkStorageCandidateResult> PutRateLimitRulesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!TryParseBody(request.Body, out var doc, out var error))
        {
            return ValidationFailedResult("rate-limit-rules", error);
        }

        // The documented shape wraps the array ({rules: [...]}); the store and
        // every reader expect the bare array. Normalize on write.
        var rules = doc.RootElement;
        if (rules.ValueKind == JsonValueKind.Object && rules.TryGetProperty("rules", out var nested)
            && nested.ValueKind == JsonValueKind.Array)
            rules = nested;
        var version = NextVersion();
        await _store.UpsertRateLimitRulesAsync(projectId, rules, version, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "rate-limit-rules", action = "upsert" });
    }

    private async Task<NetworkStorageCandidateResult> PutCollectionsAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "collection", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }

        return await UpsertResourceArrayAsync(
            projectId,
            items,
            "collection",
            (id, name, _, def, version, ct) => _store.UpsertCollectionAsync(projectId, id, name, GetOptionalString(def, "visibility") ?? "private", def, version, ct),
            request.CancellationToken);
    }

    private async Task<NetworkStorageCandidateResult> PutEndpointsAsync(NetworkStorageCandidateRequest request, string projectId, long ownerUserId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "endpoint", requiredField: "slug");
        if (validationError is not null)
        {
            return validationError;
        }

        return await UpsertResourceArrayAsync(
            projectId,
            items,
            "endpoint",
            async (id, slug, _, def, version, ct) =>
            {
                await _store.UpsertEndpointAsync(
                    projectId, id,
                    slug,
                    GetOptionalString(def, "method") ?? "GET",
                    GetOptionalBool(def, "enabled") ?? true,
                    def,
                    GetOptionalString(def, "versionHash"),
                    version,
                    ct);
                await RecordVersionAsync(ownerUserId, projectId, "endpoint", id, def, "put", version, ct);
            },
            request.CancellationToken);
    }

    private async Task<NetworkStorageCandidateResult> PutWorkflowsAsync(NetworkStorageCandidateRequest request, string projectId, long ownerUserId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "workflow", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }

        return await UpsertResourceArrayAsync(
            projectId,
            items,
            "workflow",
            async (id, name, _, def, version, ct) =>
            {
                await _store.UpsertWorkflowAsync(projectId, id, name, def, GetOptionalString(def, "versionHash"), version, ct);
                await RecordVersionAsync(ownerUserId, projectId, "workflow", id, def, "put", version, ct);
            },
            request.CancellationToken);
    }

    private async Task<NetworkStorageCandidateResult> PutQueriesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "query", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }

        var version = NextVersion();

        foreach (var item in items)
        {
            var id = GetOptionalString(item, "id") ?? GetOptionalString(item, "name") ?? $"query-{Guid.NewGuid():N}";
            var name = GetOptionalString(item, "name") ?? id;

            try
            {
                await _store.UpsertQueryAsync(
                    projectId,
                    id,
                    name,
                    GetOptionalBool(item, "requiresSecretKey") ?? false,
                    item,
                    version,
                    request.CancellationToken);
            }
            catch (Exception ex)
            {
                return ProductionErrorResult("query", $"Upsert failed: {ex.Message}");
            }
        }

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "query", action = "upsert" });
    }

    private async Task<NetworkStorageCandidateResult> DeleteCollectionsAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("collectionId", out var collectionId) || string.IsNullOrWhiteSpace(collectionId))
        {
            return ValidationFailedResult("collection", "Missing route parameter 'collectionId'.");
        }

        await _store.DeleteCollectionAsync(projectId, collectionId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "collection", action = "delete", resourceId = collectionId });
    }

    private async Task<NetworkStorageCandidateResult> DeleteEndpointsAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("endpointId", out var endpointId) || string.IsNullOrWhiteSpace(endpointId))
        {
            return ValidationFailedResult("endpoint", "Missing route parameter 'endpointId'.");
        }

        await _store.DeleteEndpointAsync(projectId, endpointId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "endpoint", action = "delete", resourceId = endpointId });
    }

    private async Task<NetworkStorageCandidateResult> DeleteWorkflowsAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("workflowId", out var workflowId) || string.IsNullOrWhiteSpace(workflowId))
        {
            return ValidationFailedResult("workflow", "Missing route parameter 'workflowId'.");
        }

        await _store.DeleteWorkflowAsync(projectId, workflowId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "workflow", action = "delete", resourceId = workflowId });
    }

    private async Task<NetworkStorageCandidateResult> DeleteQueriesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("queryId", out var queryId) || string.IsNullOrWhiteSpace(queryId))
        {
            return ValidationFailedResult("query", "Missing route parameter 'queryId'.");
        }

        await _store.DeleteQueryAsync(projectId, queryId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "query", action = "delete", resourceId = queryId });
    }

    private async Task<NetworkStorageCandidateResult> DeleteGameValuesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        await _store.DeleteGameValuesAsync(projectId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "game-values", action = "delete" });
    }

    private async Task<NetworkStorageCandidateResult> DeleteRateLimitRulesAsync(NetworkStorageCandidateRequest request, string projectId)
    {
        await _store.DeleteRateLimitRulesAsync(projectId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "rate-limit-rules", action = "delete" });
    }

    // ── Sync: batch push (PUT /sync) ──

    /// <summary>
    /// Batch-writes the endpoints/collections/workflows sections of a Push All
    /// payload to ScyllaDB using the same native upserts as the per-resource
    /// routes. Mirrors the Bun <c>PUT /sync</c> response shape:
    /// <c>{ ok, endpoints?, collections?, workflows? }</c> with per-section results.
    /// </summary>
    private async Task<NetworkStorageCandidateResult> PutSyncAsync(NetworkStorageCandidateRequest request, string projectId,
        long ownerUserId, string versionSource)
    {
        if (!TryParseBody(request.Body, out var doc, out var parseError))
        {
            return ValidationFailedResult("project-sync", parseError);
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ValidationFailedResult("project-sync", "Body must be an object with endpoints, collections, and/or workflows.");
        }

        var version = NextVersion();
        var response = new Dictionary<string, object?> { ["ok"] = true, ["source"] = "candidate" };
        var overallOk = true;

        foreach (var (sectionName, resourceKind) in new[] { ("endpoints", "endpoint"), ("collections", "collection"), ("workflows", "workflow") })
        {
            if (!TryGetSection(root, sectionName, out var sectionValue)) continue;
            var section = await SyncSectionAsync(projectId, resourceKind, sectionValue, version,
                (id, name, def, ct) => UpsertDefinitionAsync(ownerUserId, projectId, resourceKind, id, name, def, version, versionSource, ct),
                request.CancellationToken);
            response[sectionName] = section.Body;
            overallOk &= section.Ok;
        }

        response["ok"] = overallOk;
        return new NetworkStorageCandidateResult(
            overallOk ? 200 : 400,
            overallOk ? null : ManagementMutationConstants.ValidationFailedCode,
            response,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "allowed");
    }

    private async Task<(bool Ok, object Body)> SyncSectionAsync(
        string projectId, string resourceKind, JsonElement incoming, long version,
        Func<string, string, JsonElement, CancellationToken, Task> upsertAsync,
        CancellationToken ct)
    {
        if (incoming.ValueKind != JsonValueKind.Array)
        {
            return (false, new
            {
                ok = false,
                error = ManagementMutationConstants.ValidationFailedCode,
                message = $"{resourceKind}s must be an array",
                results = Array.Empty<object>(),
            });
        }

        var results = new List<object>();
        var failed = 0;
        var index = 0;

        foreach (var item in incoming.EnumerateArray())
        {
            var slot = index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                failed++;
                results.Add(new { ok = false, resourceKind, resourceId = $"{resourceKind}[{slot}]", error = ManagementMutationConstants.ValidationFailedCode, message = $"Each {resourceKind} must be an object." });
                continue;
            }

            if (!NetworkStorageSourceResourceCompiler.TryCompile(item, resourceKind, out var definition, out var compileError))
            {
                failed++;
                results.Add(new { ok = false, resourceKind, resourceId = $"{resourceKind}[{slot}]", error = "SOURCE_COMPILE_FAILED", message = compileError });
                continue;
            }

            var id = ResolveSyncId(resourceKind, definition);
            if (string.IsNullOrWhiteSpace(id))
            {
                failed++;
                results.Add(new { ok = false, resourceKind, resourceId = $"{resourceKind}[{slot}]", error = ManagementMutationConstants.ValidationFailedCode, message = $"{resourceKind} is missing a required identifier field." });
                continue;
            }

            var displayName = resourceKind == "endpoint"
                ? GetOptionalString(definition, "slug") ?? id
                : GetOptionalString(definition, "name") ?? id;

            try
            {
                await upsertAsync(id, displayName, definition, ct);
                results.Add(new { ok = true, resourceKind, resourceId = id, action = "upsert" });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { ok = false, resourceKind, resourceId = id, error = "UPSERT_FAILED", message = ex.Message });
            }
        }

        return (failed == 0, new { ok = failed == 0, total = results.Count, results });
    }

    private static string? ResolveSyncId(string resourceKind, JsonElement item) => resourceKind switch
    {
        "endpoint" => GetOptionalString(item, "id") ?? GetOptionalString(item, "slug") ?? GetOptionalString(item, "name"),
        _ => GetOptionalString(item, "id") ?? GetOptionalString(item, "name"),
    };

    // ── Sync: preflight (POST /sync/preflight) ──

    private static readonly Regex CollectionNameRegex = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Read-only validation of a Push All payload. Never writes. Validates each
    /// endpoint/collection/workflow against the same identifier requirements the
    /// native write path enforces, so preflight faithfully predicts push success.
    /// Mirrors the Bun response shape: per-section diagnostics + a rollup summary.
    /// </summary>
    private async Task<NetworkStorageCandidateResult> PreflightSyncAsync(
        NetworkStorageCandidateRequest request, string projectId, string authDecision)
    {
        if (!TryParseBody(request.Body, out var doc, out _))
        {
            return new NetworkStorageCandidateResult(
                400, "INVALID_JSON",
                new { ok = false, mode = "preflight", error = "INVALID_JSON", message = "Body must be valid JSON." },
                Array.Empty<string>(), Array.Empty<string>(), authDecision);
        }

        var root = doc.RootElement;
        var body = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["mode"] = "preflight",
            ["publishTarget"] = request.QueryValue("revisionTarget") ?? "live",
        };
        var overallOk = true;

        foreach (var (sectionName, resourceKind) in new[]
        {
            ("endpoints", "endpoint"), ("collections", "collection"), ("workflows", "workflow"),
        })
        {
            if (!TryGetSection(root, sectionName, out var sectionValue))
            {
                continue;
            }

            var section = await PreflightSectionAsync(projectId, resourceKind, sectionValue, request.CancellationToken);
            body[sectionName] = section.Body;
            overallOk &= section.Ok;
        }

        body["summary"] = BuildPreflightSummary(body);
        body["ok"] = overallOk;
        if (overallOk)
        {
            body["message"] = "Preflight validation passed. No resources were written.";
        }
        else
        {
            body["error"] = "PREFLIGHT_VALIDATION_FAILED";
            body["message"] = "Preflight validation failed. No resources were written.";
        }

        return new NetworkStorageCandidateResult(
            overallOk ? 200 : 400,
            overallOk ? null : "PREFLIGHT_VALIDATION_FAILED",
            body,
            Array.Empty<string>(), Array.Empty<string>(), authDecision);
    }

    private async Task<(bool Ok, object Body)> PreflightSectionAsync(
        string projectId, string resourceKind, JsonElement incoming, CancellationToken ct)
    {
        if (incoming.ValueKind != JsonValueKind.Array)
        {
            return (false, new
            {
                ok = false,
                error = ManagementMutationConstants.ValidationFailedCode,
                message = $"{resourceKind}s must be an array",
                total = 0,
                passed = 0,
                failed = 1,
                warnings = 0,
                results = Array.Empty<object>(),
            });
        }

        var existing = resourceKind switch
        {
            "endpoint" => await _store.ListEndpointsAsync(projectId, ct),
            "collection" => await _store.ListCollectionsAsync(projectId, ct),
            "workflow" => await _store.ListWorkflowsAsync(projectId, ct),
            _ => Array.Empty<JsonElement>(),
        };

        var results = new List<object>();
        var index = 0;
        var failed = 0;
        var warnings = 0;

        foreach (var item in incoming.EnumerateArray())
        {
            var slot = index++;
            var resourceId = PreflightResourceId(resourceKind, item, slot);

            if (item.ValueKind != JsonValueKind.Object)
            {
                failed++;
                results.Add(PreflightFailure(resourceKind, resourceId, ManagementMutationConstants.ValidationFailedCode,
                    $"Each {resourceKind} must be an object.", null, null));
                continue;
            }

            if (!NetworkStorageSourceResourceCompiler.TryCompile(item, resourceKind, out var definition, out var compileError))
            {
                failed++;
                results.Add(PreflightFailure(resourceKind, resourceId, "SOURCE_COMPILE_FAILED",
                    compileError ?? "Source compilation failed.", "sourceText", "Fix the source syntax and retry."));
                continue;
            }

            resourceId = PreflightResourceId(resourceKind, definition, slot);
            var identifierError = PreflightValidateIdentifier(resourceKind, definition, resourceId);
            if (identifierError is not null)
            {
                failed++;
                results.Add(identifierError);
                continue;
            }
            var diagnostics = new List<object>();
            if (resourceKind == "collection")
            {
                var name = GetOptionalString(definition, "name");
                if (!string.IsNullOrEmpty(name) && !CollectionNameRegex.IsMatch(name))
                {
                    warnings++;
                    diagnostics.Add(new
                    {
                        severity = "warning",
                        code = "COLLECTION_NAME_FORMAT",
                        message = "Collection name should be lowercase letters, numbers, or underscores.",
                        resourceKind,
                        resourceId,
                        sourcePath = "name",
                        suggestedFix = "Use a collection name like player_data.",
                    });
                }
            }

            var action = PreflightMatchesExisting(resourceKind, definition, existing) ? "would_update" : "would_create";
            results.Add(new { ok = true, resourceKind, resourceId, action, diagnostics });
        }

        var total = results.Count;
        return (failed == 0, new
        {
            ok = failed == 0,
            total,
            passed = total - failed,
            failed,
            warnings,
            results,
        });
    }

    private static string PreflightResourceId(string resourceKind, JsonElement r, int index) => resourceKind switch
    {
        "endpoint" => GetOptionalString(r, "slug") ?? GetOptionalString(r, "id") ?? GetOptionalString(r, "sourcePath") ?? $"endpoint[{index}]",
        "collection" => GetOptionalString(r, "name") ?? GetOptionalString(r, "id") ?? GetOptionalString(r, "sourcePath") ?? $"collection[{index}]",
        "workflow" => GetOptionalString(r, "id") ?? GetOptionalString(r, "name") ?? GetOptionalString(r, "sourcePath") ?? $"workflow[{index}]",
        _ => GetOptionalString(r, "id") ?? GetOptionalString(r, "name") ?? $"resource[{index}]",
    };

    private static object? PreflightValidateIdentifier(string resourceKind, JsonElement r, string resourceId)
    {
        switch (resourceKind)
        {
            case "endpoint":
                if (string.IsNullOrWhiteSpace(GetOptionalString(r, "slug")) && string.IsNullOrWhiteSpace(GetOptionalString(r, "id")))
                {
                    return PreflightFailure(resourceKind, resourceId, "ENDPOINT_SLUG_REQUIRED",
                        "Endpoint slug is required.", "slug", "Add a slug (or id) to the endpoint.");
                }
                break;
            case "collection":
                if (string.IsNullOrWhiteSpace(GetOptionalString(r, "name")) && string.IsNullOrWhiteSpace(GetOptionalString(r, "id")))
                {
                    return PreflightFailure(resourceKind, resourceId, "COLLECTION_NAME_REQUIRED",
                        "Collection name is required.", "name", "Use a collection name like player_data.");
                }
                break;
            case "workflow":
                if (string.IsNullOrWhiteSpace(GetOptionalString(r, "id")) && string.IsNullOrWhiteSpace(GetOptionalString(r, "name")))
                {
                    return PreflightFailure(resourceKind, resourceId, "WORKFLOW_ID_OR_NAME_REQUIRED",
                        "Workflow id or name is required.", "id", "Add an id (or name) to the workflow.");
                }
                break;
        }
        return null;
    }

    private static object PreflightFailure(string resourceKind, string resourceId, string code, string message, string? sourcePath, string? suggestedFix) => new
    {
        ok = false,
        resourceKind,
        resourceId,
        error = ManagementMutationConstants.ValidationFailedCode,
        diagnostics = new object[]
        {
            new { severity = "error", code, message, resourceKind, resourceId, sourcePath, suggestedFix },
        },
    };

    private static bool PreflightMatchesExisting(string resourceKind, JsonElement incoming, IReadOnlyList<JsonElement> existing)
    {
        var id = GetOptionalString(incoming, "id");
        var nameOrSlug = resourceKind == "endpoint" ? GetOptionalString(incoming, "slug") : GetOptionalString(incoming, "name");
        var idField = resourceKind switch { "endpoint" => "endpoint_id", "collection" => "collection_id", "workflow" => "workflow_id", _ => "id" };
        var nameField = resourceKind == "endpoint" ? "slug" : "name";

        foreach (var row in existing)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!string.IsNullOrEmpty(id) && string.Equals(GetOptionalString(row, idField), id, StringComparison.Ordinal))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(nameOrSlug) && string.Equals(GetOptionalString(row, nameField), nameOrSlug, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static object BuildPreflightSummary(IReadOnlyDictionary<string, object?> response)
    {
        int total = 0, success = 0, failed = 0, warnings = 0;
        foreach (var section in new[] { "endpoints", "collections", "workflows" })
        {
            if (!response.TryGetValue(section, out var value) || value is null) continue;
            total += ReadSummaryInt(value, "total");
            success += ReadSummaryInt(value, "passed");
            failed += ReadSummaryInt(value, "failed");
            warnings += ReadSummaryInt(value, "warnings");
        }

        var nextAction = failed > 0
            ? "Fix the listed validation errors, then retry Push All."
            : warnings > 0
                ? "Review warnings, then Push All can proceed."
                : "Push All can proceed.";

        return new { total, success, failed, warnings, nextAction };
    }

    private static int ReadSummaryInt(object sectionBody, string propertyName)
    {
        var prop = sectionBody.GetType().GetProperty(propertyName);
        return prop?.GetValue(sectionBody) is int value ? value : 0;
    }

    private static bool TryGetSection(JsonElement root, string sectionName, out JsonElement section)
    {
        section = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(sectionName, out var value))
        {
            return false;
        }
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }
        section = value;
        return true;
    }

    private static NetworkStorageCandidateResult ProductionErrorResult(string resourceKind, string message) =>
        NetworkStorageCandidateResult.Error(
            500,
            $"MANAGEMENT_{resourceKind.ToUpperInvariant()}_FAILED",
            new { error = $"MANAGEMENT_{resourceKind.ToUpperInvariant()}_FAILED", message },
            authDecision: "allowed");

    // ── Shared production helpers ──

    private async Task<NetworkStorageCandidateResult> UpsertResourceArrayAsync(
        string projectId,
        IReadOnlyList<JsonElement> items,
        string resourceKind,
        Func<string, string, string?, JsonElement, long, CancellationToken, Task> upsertAsync,
        CancellationToken ct)
    {
        var results = new List<object>();
        var version = NextVersion();

        foreach (var item in items)
        {
            var id = resourceKind switch
            {
                "collection" => GetOptionalString(item, "id") ?? GetOptionalString(item, "name") ?? $"{resourceKind}-{results.Count}",
                "query" => GetOptionalString(item, "id") ?? GetOptionalString(item, "name") ?? $"{resourceKind}-{results.Count}",
                _ => GetOptionalString(item, "id") ?? GetOptionalString(item, "slug") ?? GetOptionalString(item, "name") ?? $"{resourceKind}-{results.Count}",
            };

            if (string.IsNullOrWhiteSpace(id) || id.StartsWith($"{resourceKind}-", StringComparison.Ordinal))
            {
                results.Add(new { ok = false, resourceKind, resourceId = id, error = ManagementMutationConstants.ValidationFailedCode, message = $"{resourceKind} is missing a required identifier field." });
                continue;
            }
            var displayName = resourceKind switch
            {
                "endpoint" => GetOptionalString(item, "slug") ?? id,
                "collection" => GetOptionalString(item, "name") ?? id,
                "workflow" => GetOptionalString(item, "name") ?? id,
                "query" => GetOptionalString(item, "name") ?? id,
                _ => id,
            };

            try
            {
                await upsertAsync(id, displayName, null, item, version, ct);
                results.Add(new { ok = true, resourceKind, resourceId = id, action = "upsert" });
            }
            catch (Exception ex)
            {
                results.Add(new { ok = false, resourceKind, resourceId = id, error = "UPSERT_FAILED", message = ex.Message });
            }
        }

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind, results });
    }

    private static (IReadOnlyList<JsonElement> Items, NetworkStorageCandidateResult? Error) ParseResourceArray(string? body, string resourceKind, string requiredField)
    {
        if (!TryParseBody(body, out var doc, out var error))
        {
            return (Array.Empty<JsonElement>(), ValidationFailedResult(resourceKind, error));
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<JsonElement>(), ValidationFailedResult(resourceKind, $"Body must be an array of {resourceKind} objects."));
        }

        var items = new List<JsonElement>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return (Array.Empty<JsonElement>(), ValidationFailedResult(resourceKind, $"Each {resourceKind} must be an object."));
            }

            if (string.IsNullOrWhiteSpace(GetOptionalString(item, requiredField)) &&
                string.IsNullOrWhiteSpace(GetOptionalString(item, "id")))
            {
                return (Array.Empty<JsonElement>(), ValidationFailedResult(resourceKind, $"Each {resourceKind} must have '{requiredField}' or 'id'."));
            }

            items.Add(item);
        }

        return (items, null);
    }

    private static (string Id, JsonElement Definition, NetworkStorageCandidateResult? Error) ParseSingleResource(
        string? body,
        string resourceKind,
        string requiredField,
        string? alternateIdField = null)
    {
        if (!TryParseBody(body, out var doc, out var error))
        {
            return (string.Empty, default, ValidationFailedResult(resourceKind, error));
        }

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(resourceKind, out var wrapped))
        {
            root = wrapped;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, default, ValidationFailedResult(resourceKind, $"Body must be a {resourceKind} object."));
        }

        var id = (alternateIdField is not null ? GetOptionalString(root, alternateIdField) : null)
            ?? GetOptionalString(root, requiredField)
            ?? GetOptionalString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return (string.Empty, default, ValidationFailedResult(resourceKind, $"{resourceKind} is missing identifier field '{requiredField}' or 'id'."));
        }

        return (id, root, null);
    }

    private static bool TryParseBody(string? body, out JsonDocument doc, out string error)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            doc = null!;
            error = "Request body is empty.";
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(body);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            doc = null!;
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.True ? true : property.ValueKind == JsonValueKind.False ? false : null;
    }

    private long NextVersion() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static NetworkStorageCandidateResult ProductionOkResult(object body) =>
        new(200, null, body, Array.Empty<string>(), Array.Empty<string>(), "allowed");

    private static NetworkStorageCandidateResult ValidationFailedResult(string resourceKind, string message) =>
        NetworkStorageCandidateResult.Error(
            400,
            ManagementMutationConstants.ValidationFailedCode,
            new
            {
                ok = false,
                resourceKind,
                error = new { code = ManagementMutationConstants.ValidationFailedCode, message }
            },
            authDecision: "allowed");

    // ── Dry-run helpers ──

    /// <summary>
    /// Builds a successful dry-run result for a known mutation route.
    /// </summary>
    private static NetworkStorageCandidateResult DryRunResult(
        long userId, string projectId, string authDecision,
        string intendedWritePath,
        string action, string route, string resourceKind)
    {
        return DryRunResult(userId, projectId, authDecision,
            new[] { intendedWritePath }, action, route, resourceKind);
    }

    private static NetworkStorageCandidateResult DryRunResult(
        long userId, string projectId, string authDecision,
        string intendedWritePath1, string intendedWritePath2,
        string action, string route, string resourceKind)
    {
        return DryRunResult(userId, projectId, authDecision,
            new[] { intendedWritePath1, intendedWritePath2 },
            action, route, resourceKind);
    }

    private static NetworkStorageCandidateResult DryRunResult(
        long userId, string projectId, string authDecision,
        string intendedWritePath1, string intendedWritePath2, string intendedWritePath3,
        string action, string route, string resourceKind)
    {
        return DryRunResult(userId, projectId, authDecision,
            new[] { intendedWritePath1, intendedWritePath2, intendedWritePath3 },
            action, route, resourceKind);
    }

    private static NetworkStorageCandidateResult DryRunResult(
        long userId, string projectId, string authDecision,
        string[] intendedWritePaths,
        string action, string route, string resourceKind)
    {
        var body = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["source"] = "candidate",
            ["reason"] = ManagementMutationConstants.WriteSuppressedReason,
            ["route"] = route,
            ["action"] = action,
            ["resourceKind"] = resourceKind,
            ["projectId"] = projectId,
            ["IntendedWritePaths"] = intendedWritePaths,
        };

        return new NetworkStorageCandidateResult(
            200,
            null,
            body,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWritePaths,
            AuthDecision: authDecision);
    }

    /// <summary>Extracts the resource name segment(s) from a management route template.</summary>
    private static string? ExtractResourcePath(NetworkStorageRouteClassification route)
    {
        var entry = route.Entry;
        if (entry is null) return null;

        var template = entry.Template;
        if (string.IsNullOrEmpty(template)) return null;

        // Template is like "v3/manage/:projectId/endpoints" or "v3/manage/:projectId/sync/preflight"
        var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments[0]=v3, segments[1]=manage, segments[2]=:projectId, segments[3..]=resource
        if (segments.Length < 4) return null;

        var resource = segments[3];
        // Strip leading ':' in case it's a parameter segment
        if (resource.Length > 0 && resource[0] == ':')
            return null;

        // Collect additional literal segments for multi-segment routes (e.g. "sync/preflight")
        if (segments.Length > 4)
        {
            var subParts = new List<string>();
            for (var i = 4; i < segments.Length; i++)
            {
                var seg = segments[i];
                if (seg.Length > 0 && seg[0] == ':')
                    break;
                subParts.Add(seg);
            }

            if (subParts.Count > 0)
            {
                resource = $"{resource}/{string.Join("/", subParts)}";
            }
        }

        return resource;
    }

    /// <summary>
    /// Extracts the public key value from the request body or credentials for the DELETE keys route.
    /// Falls back to a placeholder if not available.
    /// </summary>
    private static string ExtractPublicKeyFromRequest(NetworkStorageCandidateRequest request)
    {
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("publicKey", out var pkEl))
                {
                    var pk = pkEl.GetString();
                    if (!string.IsNullOrEmpty(pk))
                        return SanitizeKeyName(pk);
                }
            }
            catch
            {
                // Fall through
            }
        }

        if (request.Query.TryGetValue("publicKey", out var queryPk) && !string.IsNullOrEmpty(queryPk))
        {
            return SanitizeKeyName(queryPk);
        }

        return "(public-key-placeholder)";
    }

    private static string SanitizeKeyName(string key)
    {
        var safe = new System.Text.StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                safe.Append(c);
            else
                safe.Append('_');
        }

        return safe.ToString();
    }

    private static bool IsMutationMethod(string method) =>
        string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);

    private static NetworkStorageCandidateResult NotImplementedResult(string routeName)
    {
        return NetworkStorageCandidateResult.Error(
            501,
            ManagementMutationConstants.NotImplementedCode,
            new
            {
                ok = false,
                error = ManagementMutationConstants.NotImplementedCode,
                message = $"The management mutation route '{routeName}' is not implemented in the native .NET candidate.",
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

    private static NetworkStorageCandidateResult ProjectDisabledResult(string authDecision)
    {
        return NetworkStorageCandidateResult.Error(
            403,
            "PROJECT_DISABLED",
            new { ok = false, error = new { code = "PROJECT_DISABLED", message = "This project is currently disabled." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: authDecision);
    }
}
