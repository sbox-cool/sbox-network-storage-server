using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Constants shared by the native management mutation handler.
/// </summary>
internal static class ManagementMutationConstants
{
    /// <summary>Base CDN path segment for user/project resource files.</summary>
    public const string UserStoragePrefix = "network-storage/users";

    /// <summary>Public error code for unsupported mutation routes.</summary>
    public const string NotImplementedCode = "MANAGEMENT_MUTATION_NOT_IMPLEMENTED";

    /// <summary>Public error code for validation failures.</summary>
    public const string ValidationFailedCode = "VALIDATION_FAILED";
}

public sealed partial class ManagementMutationHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly INetworkStorageStore _store;
    private readonly TimeProvider _time;
    private readonly EndpointExecutor? _endpointExecutor;
    private readonly IQueryValuesContextProvider? _valuesProvider;
    private readonly IReadOnlyList<IEndpointScriptRuntime> _scriptRuntimes;
    private readonly INetworkStorageProjectService? _keyRevocation;
    private readonly ManagementSyncJobStore _syncJobs;

    public ManagementMutationHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageStore store,
        TimeProvider time)
        : this(apiKeyResolver, workspaceClient, store, time, endpointExecutor: null)
    {
    }

    public ManagementMutationHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageStore store,
        TimeProvider time,
        EndpointExecutor? endpointExecutor,
        IQueryValuesContextProvider? valuesProvider = null,
        IReadOnlyList<IEndpointScriptRuntime>? scriptRuntimes = null,
        INetworkStorageProjectService? keyRevocation = null,
        ManagementSyncJobStore? syncJobs = null)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _store = store;
        _time = time;
        _endpointExecutor = endpointExecutor;
        _valuesProvider = valuesProvider;
        _scriptRuntimes = scriptRuntimes ?? [];
        _keyRevocation = keyRevocation;
        _syncJobs = syncJobs ?? new ManagementSyncJobStore(time);
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Management;

    /// <summary>
    /// Owner-panel adapter sharing the editor compiler and native management writes; never creates a credential.
    /// <paramref name="versionSource"/> labels the endpoint/workflow version snapshot this save records.
    /// <paramref name="targetsNext"/> stages endpoints and collections like a Sync Tool Push Staged (see
    /// <see cref="NetworkStorageRequest.TargetsNext"/>); other kinds always write live.
    /// </summary>
    public async Task<NetworkStorageResult> SaveOwnerResourceAsync(
        long ownerUserId, string projectId, string kind, JsonElement resource, CancellationToken ct,
        string versionSource = "owner-dashboard", bool targetsNext = false)
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
            var displayName = kind == "endpoint" ? GetOptionalString(compiled, "slug") ?? id : GetOptionalString(compiled, "name") ?? id;
            if (await IsModuleOwnedAsync(ownerUserId, projectId, kind, id, displayName, ct))
                return ValidationFailedResult(kind, "This definition is managed by an installed module. Uninstall the module (keeping data) before editing it here.");
        }
        var section = kind switch { "game-values" => kind, "query" => "queries", _ => kind + "s" };
        var route = NetworkStorageRouteClassifier.Classify("POST", $"/v3/manage/{projectId}/{section}");
        var request = new NetworkStorageRequest(route, new Dictionary<string, string>(), "application/json",
            new Dictionary<string, bool>(), NetworkStorageCredentials.None, compiled.GetRawText(), ownerUserId, ct)
        {
            TargetsNext = targetsNext,
        };
        if (kind == "game-values") return await PutGameValuesAsync(request, projectId);
        if (kind == "query")
        {
            // The query write itself drops cached results (MetadataCachingNetworkStore → QueryResultCache).
            request = request with { Body = JsonSerializer.Serialize(new[] { compiled }) };
            return await PutQueriesAsync(request, projectId);
        }
        request = request with { Body = JsonSerializer.Serialize(new Dictionary<string, object> { [kind + "s"] = new[] { compiled } }) };
        var preflight = await PreflightSyncAsync(request, projectId, "owner");
        return preflight.StatusCode >= 400 ? preflight : await PutSyncAsync(request, projectId, ownerUserId, versionSource);
    }

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Management
        && IsMutationMethod(route.Method);

    /// <summary>
    /// Validates the request's API key without touching the body. Returns the
    /// rejection result for a missing, unknown, disabled or non-secret key, or
    /// <c>null</c> when the key may proceed to <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task<NetworkStorageResult?> RejectUnauthenticatedAsync(NetworkStorageRequest request)
        => await ResolveSecretKeyAsync(request) is null ? ManagementAuthError() : null;

    private async Task<StorageApiKeyAuthResult?> ResolveSecretKeyAsync(NetworkStorageRequest request)
    {
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, request.ProjectId ?? string.Empty, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // Management mutation routes require a SECRET key, not a public key.
        return auth is { Enabled: true } && string.Equals(auth.KeyType, "secret", StringComparison.Ordinal) ? auth : null;
    }

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var method = request.Route.Method;
        var resourcePath = ExtractResourcePath(request.Route);
        if (resourcePath is null)
        {
            return NotImplementedResult(resourcePath ?? "(unknown)");
        }

        // ── Auth for all management mutation routes ──

        var auth = await ResolveSecretKeyAsync(request);
        if (auth is null)
        {
            return ManagementAuthError();
        }

        var projectId = request.ProjectId ?? string.Empty;

        var permissionError = AuthorizeMutation(request, resourcePath, auth);
        if (permissionError is not null)
        {
            return permissionError;
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
            return ProjectDisabledResult(auth.KeyType);
        }

        var key = $"{method}:{resourcePath}";
        return key switch
        {
            "PUT:game-values" or "POST:game-values" => await PutGameValuesAsync(request, projectId),

            "DELETE:game-values" => await DeleteGameValuesAsync(request, projectId),

            "PUT:endpoints" or "POST:endpoints" => await PutEndpointsAsync(request, projectId, ownerUserId),

            "DELETE:endpoints" => await DeleteEndpointsAsync(request, projectId),

            "PUT:collections" or "POST:collections" => await PutCollectionsAsync(request, projectId, ownerUserId),

            "DELETE:collections" => await DeleteCollectionsAsync(request, projectId),

            "PUT:workflows" or "POST:workflows" => await PutWorkflowsAsync(request, projectId, ownerUserId),

            "DELETE:workflows" => await DeleteWorkflowsAsync(request, projectId),

            "PUT:rate-limit-rules" or "POST:rate-limit-rules" => await PutRateLimitRulesAsync(request, projectId),

            "DELETE:rate-limit-rules" => await DeleteRateLimitRulesAsync(request, projectId),

            "POST:queries" => await PutQueriesAsync(request, projectId),

            "DELETE:queries" => await DeleteQueriesAsync(request, projectId),

            "PUT:settings" => await PutSettingsAsync(request, projectId, ownerUserId, projects, project),

            "PATCH:endpoints" => await PatchResourceAsync(request, projectId, ownerUserId, "endpoint"),

            "PATCH:collections" => await PatchResourceAsync(request, projectId, ownerUserId, "collection"),

            "PATCH:workflows" => await PatchResourceAsync(request, projectId, ownerUserId, "workflow"),

            "PUT:tests" => await PutTestsAsync(request, projectId, ownerUserId),

            "DELETE:keys" => await DestroyKeyPairAsync(request, projectId, ownerUserId),

            "PUT:sync" => await PutSyncAsync(request, projectId, ownerUserId, "sync"),

            "POST:source-upgrade" => SourceUpgrade(request, auth.KeyType),

            "POST:auto-test" => await AutoTestAsync(request, projectId, ownerUserId, auth.KeyType),

            "POST:run-tests" => await RunSavedTestsAsync(request, projectId, ownerUserId, project.PlayerKeyMode, auth.KeyType, request.CancellationToken),

            "POST:test-endpoint" => await TestEndpointAsync(request, projectId, ownerUserId, project.PlayerKeyMode, auth.KeyType),

            "POST:suggest-tests" => await SuggestTestsAsync(request, projectId, auth.KeyType),

            "POST:sync/preflight" => await PreflightSyncAsync(request, projectId, auth.KeyType),

            _ => NotImplementedResult(resourcePath),
        };
    }

    private async Task<NetworkStorageResult> AutoTestAsync(
        NetworkStorageRequest request,
        string projectId,
        long ownerUserId,
        string authDecision)
    {
        var testContext = await TestContextAsync(request.TargetsNext, ownerUserId, projectId, request.CancellationToken);
        return await NetworkStorageManagementAutoTestRunner.RunAsync(
            request, projectId, ownerUserId, authDecision, _store, testContext.Executor, testContext.Overlay, testContext.Values);
    }

    // Pure compile + validate: never writes. Reference: routeManageSourceUpgrade.
    private static NetworkStorageResult SourceUpgrade(NetworkStorageRequest request, string authDecision)
    {
        if (!TryParseBody(request.Body, out var document, out _))
        {
            return InvalidJsonResult(authDecision);
        }

        using (document)
        {
            // A wrapped `resource` object (or array, which then fails the object check) is the payload; otherwise the body itself.
            var root = document.RootElement;
            var payload = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("resource", out var wrapped)
                && (wrapped.ValueKind == JsonValueKind.Object || wrapped.ValueKind == JsonValueKind.Array)
                    ? wrapped
                    : root;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return ValidationFailedResult("project-source", "Body must be a source resource object.");
            }

            if (!ManagementSourceUpgrade.TryUpgrade(payload, out var body))
            {
                return NetworkStorageResult.Error(400, "SOURCE_UPGRADE_FAILED", body, authDecision: authDecision);
            }

            return NetworkStorageResult.Ok(body, authDecision: authDecision);
        }
    }

    // Reference: routeManageSuggestTests. Reads endpoints, collections and game values; writes nothing.
    private async Task<NetworkStorageResult> SuggestTestsAsync(
        NetworkStorageRequest request, string projectId, string authDecision)
    {
        if (!TryParseBody(request.Body, out var document, out _))
        {
            return InvalidJsonResult(authDecision);
        }

        using (document)
        {
            var root = document.RootElement;
            var slugValue = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slug", out var found)
                ? found
                : default;
            if (IsFalsy(slugValue))
            {
                return NetworkStorageResult.Error(400, "MISSING_SLUG",
                    new { ok = false, error = "MISSING_SLUG", message = "Provide 'slug'." },
                    authDecision: authDecision);
            }

            // Reference: endpoints.find(e => e.slug === slug). A non-string slug can never match.
            var slug = slugValue.ValueKind == JsonValueKind.String ? slugValue.GetString() : null;
            var endpoints = await _store.ListEndpointsAsync(projectId, request.CancellationToken);
            var endpoint = slug is null
                ? default
                : endpoints.FirstOrDefault(row => row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("slug", out var rowSlug)
                    && rowSlug.ValueKind == JsonValueKind.String
                    && rowSlug.GetString() == slug);
            if (endpoint.ValueKind != JsonValueKind.Object)
            {
                var label = slug ?? slugValue.GetRawText();
                return NetworkStorageResult.Error(400, "ENDPOINT_NOT_FOUND",
                    new { ok = false, error = "ENDPOINT_NOT_FOUND", message = $"Endpoint \"{label}\" not found." },
                    authDecision: authDecision);
            }

            var collectionDefinitions = new List<JsonElement>();
            foreach (var row in await _store.ListCollectionsAsync(projectId, request.CancellationToken))
            {
                if (GameValueCollectionDefinition.TryRead(row, out var definition, out _)) collectionDefinitions.Add(definition);
            }
            // Reference passes the endpoint definition object (it carries slug), not the store row.
            var endpointDefinition = ManagementEndpointTestRunner.Definition(endpoint) ?? endpoint;
            var gameValues = await _store.ReadGameValuesAsync(projectId, request.CancellationToken);
            var suggestions = ManagementSuggestTests.Suggest(
                endpointDefinition, JsonSerializer.SerializeToElement(collectionDefinitions), gameValues);

            return NetworkStorageResult.Ok(
                new JsonObject { ["ok"] = true, ["endpoint"] = slug, ["suggestions"] = suggestions },
                authDecision: authDecision);
        }
    }

    private static NetworkStorageResult InvalidJsonResult(string authDecision) =>
        NetworkStorageResult.Error(400, "INVALID_JSON",
            new { ok = false, error = new { code = "INVALID_JSON", message = "Body must be valid JSON." } },
            authDecision: authDecision);

    // Mirrors JS falsiness for the reference `if (!slug)` check.
    private static bool IsFalsy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => true,
        JsonValueKind.String => value.GetString()!.Length == 0,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number == 0,
        _ => false,
    };

    // ── Production write helpers ──

    private static readonly string[] AllManagementScopes =
        ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"];

    private static NetworkStorageResult? AuthorizeMutation(
        NetworkStorageRequest request, string resourcePath, StorageApiKeyAuthResult auth)
    {
        var level = resourcePath switch
        {
            "auto-test" or "run-tests" or "test-endpoint" => "x",
            "suggest-tests" => "r",
            _ => "rw",
        };
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
        "suggest-tests" => ["endpoints"],
        "source-upgrade" => [],
        "tests" or "game-package" or "gamePackage" or "package-sync" => AllManagementScopes,
        _ => [],
    };

    private static NetworkStorageResult PermissionDeniedResult() =>
        NetworkStorageResult.Error(
            403, "FORBIDDEN",
            new { ok = false, error = new { code = "FORBIDDEN", message = "This key does not have permission for this operation." } },
            storagePathsRead: Array.Empty<string>(), authDecision: "denied");

    private async Task<NetworkStorageResult> PutGameValuesAsync(NetworkStorageRequest request, string projectId)
    {
        if (!TryParseBody(request.Body, out var doc, out var error))
        {
            return ValidationFailedResult("game-values", error);
        }

        var version = NextVersion();
        var payload = doc.RootElement;
        var ownedItems = await ModuleOwnedValuesItemsAsync(request.ResolvedOwnerUserId, projectId, request.CancellationToken);
        if (ownedItems.Count > 0)
        {
            var existing = await _store.ReadGameValuesAsync(projectId, request.CancellationToken);
            payload = Modules.ModuleInstaller.MergeValuesPreservingOwned(UnwrapPayload(existing), payload, ownedItems);
        }
        await _store.UpsertGameValuesAsync(projectId, payload, versionHash: null, version, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "game-values", action = "upsert" });
    }

    /// <summary>
    /// Game-values item ids owned by installed modules (kept across
    /// full-document replaces). Fail-closed: a state read failure throws, so
    /// the request fails loudly instead of dropping owned items.
    /// </summary>
    private async Task<HashSet<string>> ModuleOwnedValuesItemsAsync(long? ownerUserId, string projectId, CancellationToken ct)
    {
        if (ownerUserId is null) return [];
        var states = new Modules.ModuleStateStore(_workspaceClient);
        var state = await states.LoadAsync(ownerUserId.Value, projectId, ct);
        return new HashSet<string>(state.Modules.SelectMany(module => module.ValuesItems), StringComparer.Ordinal);
    }

    private static JsonElement? UnwrapPayload(JsonElement? row)
    {
        if (row is not { ValueKind: JsonValueKind.Object }) return null;
        if (!row.Value.TryGetProperty("payload_json", out var payload)) return row;
        if (payload.ValueKind == JsonValueKind.String)
        {
            try { return JsonDocument.Parse(payload.GetString() ?? "null").RootElement; }
            catch (JsonException) { return null; }
        }
        return payload;
    }

    private async Task<NetworkStorageResult> PutRateLimitRulesAsync(NetworkStorageRequest request, string projectId)
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

    // ── Settings: PUT /settings. Port of routeManagePutSettings (manage-api-controller.js L2152-2204). ──
    private async Task<NetworkStorageResult> PutSettingsAsync(
        NetworkStorageRequest request, string projectId, long ownerUserId,
        IReadOnlyList<WorkspaceProject> projects, WorkspaceProject project)
    {
        if (!TryParseBody(request.Body, out var doc, out var error))
        {
            return ValidationFailedResult("project-settings", error);
        }

        using (doc)
        {
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object)
            {
                return ValidationFailedResult("project-settings", "Body must be a project settings object.");
            }
            if (ManagementSettings.FirstUnstoredKey(body) is { } unstored)
            {
                return new NetworkStorageResult(
                    400, "SETTING_NOT_STORED",
                    new { ok = false, error = "SETTING_NOT_STORED", message = $"'{unstored}' is not a setting this server stores." },
                    Array.Empty<string>(), Array.Empty<string>(), "allowed");
            }
            if (body.TryGetProperty("encryptedRequestMode", out var mode)
                && (mode.ValueKind != JsonValueKind.String || !EncryptedRequestModes.IsValid(mode.GetString())))
                return ManagementRouteFailure(400, "VALIDATION_FAILED", "encryptedRequestMode must be off, v1, v2-preferred, or v2-required.");

            var updated = ManagementSettings.Apply(project, body);
            var list = projects.ToList();
            var index = list.FindIndex(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
            if (index < 0)
            {
                return ManagementAuthError();
            }
            list[index] = updated;
            await _workspaceClient.SaveUserProjectsAsync(ownerUserId, list, request.CancellationToken);

            var settings = ManagementSettings.View(updated);
            return ProductionOkResult(new
            {
                ok = true,
                projectId,
                settings,
                config = settings,
                enableAuthSessions = settings["enableAuthSessions"],
                enableEncryptedRequests = settings["enableEncryptedRequests"],
            });
        }
    }

    private async Task<NetworkStorageResult> PutCollectionsAsync(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "collection", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }

        var publish = await ResolvePublishAsync(request, ownerUserId, projectId);
        var staged = new StagedRevisionWrites();
        var ownedCollections = await ModuleOwnedNamesAsync(ownerUserId, projectId, "collection", request.CancellationToken);
        var result = await UpsertResourceArrayAsync(
            projectId,
            items,
            "collection",
            (id, name, _, def, version, ct) =>
            {
                if (publish != PublishDecision.Staged)
                    return _store.UpsertCollectionAsync(projectId, id, name, GetOptionalString(def, "visibility") ?? "private", def, version, ct);
                AddStaged(staged, "collection", id, name, def);
                return Task.CompletedTask;
            },
            request.CancellationToken,
            (id, name) => Task.FromResult(ownedCollections.Contains(id) || ownedCollections.Contains(name)));
        await StageAsync(ownerUserId, projectId, staged, request.CancellationToken);
        return ReportPublishTarget(result, publish);
    }

    private async Task<NetworkStorageResult> PutEndpointsAsync(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "endpoint", requiredField: "slug");
        if (validationError is not null)
        {
            return validationError;
        }
        var batch = BatchEndpointsFromItems(items);
        if (await RefuseScriptErrorsAsync(projectId, batch, items.Count, request.CancellationToken) is { } refused)
        {
            return refused;
        }

        // Reference PUT endpoints?async=true: accept the payload now, write it in the background, poll sync-jobs.
        if (request.Query.GetValueOrDefault("async") == "true")
        {
            return StartEndpointSyncJob(request, projectId, ownerUserId);
        }

        return await UpsertEndpointsAsync(request, projectId, ownerUserId, items);
    }

    private async Task<NetworkStorageResult> UpsertEndpointsAsync(
        NetworkStorageRequest request,
        string projectId,
        long ownerUserId,
        IReadOnlyList<JsonElement> items)
    {
        var publish = await ResolvePublishAsync(request, ownerUserId, projectId);
        var staged = new StagedRevisionWrites();
        var replaceAll = publish != PublishDecision.Staged && request.Query.GetValueOrDefault("replaceAll") == "true";
        var writtenIds = replaceAll ? new HashSet<string>(StringComparer.Ordinal) : null;
        var ownedEndpoints = await ModuleOwnedNamesAsync(ownerUserId, projectId, "endpoint", request.CancellationToken);
        var result = await UpsertResourceArrayAsync(
            projectId,
            items,
            "endpoint",
            async (id, slug, _, def, version, ct) =>
            {
                if (publish == PublishDecision.Staged)
                {
                    AddStaged(staged, "endpoint", id, slug, def);
                    return;
                }
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
                writtenIds?.Add(id);
            },
            request.CancellationToken,
            (id, name) => Task.FromResult(ownedEndpoints.Contains(id) || ownedEndpoints.Contains(name)));
        await StageAsync(ownerUserId, projectId, staged, request.CancellationToken);
        // sync.py requests a full replacement explicitly; normal editor pushes remain upserts.
        // Never delete old definitions after a partial failure, or for a staged push.
        if (writtenIds is not null && writtenIds.Count == items.Count)
        {
            foreach (var row in await _store.ListEndpointsAsync(projectId, request.CancellationToken))
            {
                var id = GetOptionalString(row, "endpoint_id");
                // A full replacement never removes module-owned endpoints; they are
                // kept even when the pushed package does not mention them.
                if (id is not null && !writtenIds.Contains(id)
                    && !ownedEndpoints.Contains(id)
                    && !ownedEndpoints.Contains(GetOptionalString(row, "slug") ?? string.Empty))
                    await _store.DeleteEndpointAsync(projectId, id, request.CancellationToken);
            }
        }
        return ReportPublishTarget(result, publish);
    }

    /// <summary>
    /// Reference <c>PUT endpoints?async=true</c>: the payload is accepted now and written in the background.
    /// The caller polls <c>GET sync-jobs/:jobId</c>.
    /// </summary>
    private NetworkStorageResult StartEndpointSyncJob(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var jobId = _syncJobs.Create(ownerUserId, projectId, "endpoints");
        _syncJobs.Stage(jobId, "validating", "complete", "Payload validated, starting background processing");

        // The job outlives this request, so the background copy must not inherit the caller's cancellation.
        var detached = request with { CancellationToken = CancellationToken.None };
        _ = Task.Run(() => RunEndpointSyncJobAsync(jobId, detached, projectId, ownerUserId));

        return NetworkStorageResult.Accepted(new
        {
            ok = true,
            jobId,
            status = "accepted",
            message = "Sync job accepted. Poll GET /manage/:projectId/sync-jobs/:jobId for status.",
        });
    }

    private async Task RunEndpointSyncJobAsync(string jobId, NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var stage = "compiling";
        try
        {
            _syncJobs.Stage(jobId, "compiling", "processing", "Compiling and validating endpoints...");
            var (items, validationError) = ParseResourceArray(request.Body, "endpoint", requiredField: "slug");
            if (validationError is not null)
            {
                FailSyncJob(jobId, ManagementMutationConstants.ValidationFailedCode, "Body must be an array of endpoints.", stage);
                return;
            }

            // Reject the whole batch before any write when an identifier is unusable, so an async
            // request never half-applies. Matches UpsertResourceArrayAsync's identifier rule.
            var identifiers = new List<string>(items.Count);
            var invalid = new JsonArray();
            foreach (var item in items)
            {
                var id = EndpointSyncIdentifier(item);
                identifiers.Add(id ?? string.Empty);
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith("endpoint-", StringComparison.Ordinal))
                {
                    invalid.Add(new JsonObject
                    {
                        ["ok"] = false,
                        ["resourceKind"] = "endpoint",
                        ["resourceId"] = id ?? string.Empty,
                        ["error"] = ManagementMutationConstants.ValidationFailedCode,
                        ["message"] = "endpoint is missing a required identifier field.",
                    });
                }
            }

            if (invalid.Count > 0)
            {
                FailSyncJob(jobId, ManagementMutationConstants.ValidationFailedCode,
                    "One or more endpoints failed validation.", stage, invalid);
                return;
            }

            var existingIds = (await _store.ListEndpointsAsync(projectId, request.CancellationToken))
                .Select(row => GetOptionalString(row, "endpoint_id"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            stage = "saving";
            _syncJobs.Stage(jobId, "compiling", "complete", "All endpoints validated");
            _syncJobs.Stage(jobId, "saving", "processing", "Saving endpoints to storage...");
            var response = await UpsertEndpointsAsync(request, projectId, ownerUserId, items);
            var results = (JsonSerializer.SerializeToNode(response.Body) as JsonObject)?["results"]?.DeepClone() as JsonArray
                ?? new JsonArray();
            if (response.StatusCode != 200)
            {
                FailSyncJob(jobId, "SYNC_FAILED", "Endpoint save failed.", stage, results);
                return;
            }
            if (results.Any(row => row?["ok"]?.GetValue<bool>() != true))
            {
                FailSyncJob(jobId, "SYNC_FAILED", "One or more endpoints failed to save.", stage, results);
                return;
            }

            // One result per item, in input order. Counts come from the pre-save listing.
            var created = 0;
            var updated = 0;
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i]?["action"]?.GetValue<string>() != "upsert")
                {
                    continue;
                }
                if (existingIds.Contains(identifiers[i])) updated++;
                else created++;
            }

            _syncJobs.Stage(jobId, "saving", "complete", "Endpoints saved");
            _syncJobs.Complete(jobId, new JsonObject
            {
                ["ok"] = true,
                ["updatedCount"] = updated,
                ["createdCount"] = created,
                ["results"] = results,
            });
        }
        catch (Exception ex)
        {
            FailSyncJob(jobId, "SYNC_FAILED", ex.Message, stage);
        }
    }

    private void FailSyncJob(string jobId, string code, string message, string stage, JsonNode? results = null)
    {
        _syncJobs.Stage(jobId, stage, "failed", message);
        var error = new JsonObject { ["code"] = code, ["message"] = message, ["stage"] = stage };
        if (results is not null)
        {
            error["results"] = results;
        }
        _syncJobs.Fail(jobId, error);
    }

    private static string? EndpointSyncIdentifier(JsonElement item) =>
        GetOptionalString(item, "id") ?? GetOptionalString(item, "slug") ?? GetOptionalString(item, "name");


    private async Task<NetworkStorageResult> PutWorkflowsAsync(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "workflow", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }
        var ownedWorkflows = await ModuleOwnedNamesAsync(ownerUserId, projectId, "workflow", request.CancellationToken);
        return await UpsertResourceArrayAsync(
            projectId,
            items,
            "workflow",
            async (id, name, _, def, version, ct) =>
            {
                await _store.UpsertWorkflowAsync(projectId, id, name, def, GetOptionalString(def, "versionHash"), version, ct);
                await RecordVersionAsync(ownerUserId, projectId, "workflow", id, def, "put", version, ct);
            },
            request.CancellationToken,
            (id, name) => Task.FromResult(ownedWorkflows.Contains(id) || ownedWorkflows.Contains(name)));
    }

    private async Task<NetworkStorageResult> PutQueriesAsync(NetworkStorageRequest request, string projectId)
    {
        var (items, validationError) = ParseResourceArray(request.Body, "query", requiredField: "name");
        if (validationError is not null)
        {
            return validationError;
        }

        var version = NextVersion();
        var ownedQueries = await ModuleOwnedNamesAsync(request.ResolvedOwnerUserId, projectId, "query", request.CancellationToken);

        foreach (var item in items)
        {
            var id = GetOptionalString(item, "id") ?? GetOptionalString(item, "name") ?? $"query-{Guid.NewGuid():N}";
            var name = GetOptionalString(item, "name") ?? id;
            if (ownedQueries.Contains(id) || ownedQueries.Contains(name))
                continue;

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

    private async Task<NetworkStorageResult> DeleteCollectionsAsync(NetworkStorageRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("collectionId", out var collectionId) || string.IsNullOrWhiteSpace(collectionId))
        {
            return ValidationFailedResult("collection", "Missing route parameter 'collectionId'.");
        }

        if (ModuleDeleteRefusal("collection", collectionId,
                await ModuleOwnedNamesAsync(request.ResolvedOwnerUserId, projectId, "collection", request.CancellationToken)) is { } collectionRefusal)
            return collectionRefusal;
        await _store.DeleteCollectionAsync(projectId, collectionId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "collection", action = "delete", resourceId = collectionId });
    }

    private async Task<NetworkStorageResult> DeleteEndpointsAsync(NetworkStorageRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("endpointId", out var endpointId) || string.IsNullOrWhiteSpace(endpointId))
        {
            return ValidationFailedResult("endpoint", "Missing route parameter 'endpointId'.");
        }
        if (ModuleDeleteRefusal("endpoint", endpointId,
                await ModuleOwnedNamesAsync(request.ResolvedOwnerUserId, projectId, "endpoint", request.CancellationToken)) is { } endpointRefusal)
            return endpointRefusal;
        await _store.DeleteEndpointAsync(projectId, endpointId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "endpoint", action = "delete", resourceId = endpointId });
    }

    private async Task<NetworkStorageResult> DeleteWorkflowsAsync(NetworkStorageRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("workflowId", out var workflowId) || string.IsNullOrWhiteSpace(workflowId))
        {
            return ValidationFailedResult("workflow", "Missing route parameter 'workflowId'.");
        }
        if (ModuleDeleteRefusal("workflow", workflowId,
                await ModuleOwnedNamesAsync(request.ResolvedOwnerUserId, projectId, "workflow", request.CancellationToken)) is { } workflowRefusal)
            return workflowRefusal;
        await _store.DeleteWorkflowAsync(projectId, workflowId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "workflow", action = "delete", resourceId = workflowId });
    }

    private async Task<NetworkStorageResult> DeleteQueriesAsync(NetworkStorageRequest request, string projectId)
    {
        if (!request.Route.RouteParameters.TryGetValue("queryId", out var queryId) || string.IsNullOrWhiteSpace(queryId))
        {
            return ValidationFailedResult("query", "Missing route parameter 'queryId'.");
        }
        if (ModuleDeleteRefusal("query", queryId,
                await ModuleOwnedNamesAsync(request.ResolvedOwnerUserId, projectId, "query", request.CancellationToken)) is { } queryRefusal)
            return queryRefusal;
        await _store.DeleteQueryAsync(projectId, queryId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "query", action = "delete", resourceId = queryId });
    }

    private async Task<NetworkStorageResult> DeleteGameValuesAsync(NetworkStorageRequest request, string projectId)
    {
        await _store.DeleteGameValuesAsync(projectId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "game-values", action = "delete" });
    }

    private async Task<NetworkStorageResult> DeleteRateLimitRulesAsync(NetworkStorageRequest request, string projectId)
    {
        await _store.DeleteRateLimitRulesAsync(projectId, request.CancellationToken);

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "rate-limit-rules", action = "delete" });
    }

    // ── Sync: batch push (PUT /sync) ──

    /// <summary>
    /// Batch-writes the endpoints/collections/workflows sections of a Push All
    /// payload to the store using the same native upserts as the per-resource
    /// routes. Mirrors the legacy server <c>PUT /sync</c> response shape:
    /// <c>{ ok, endpoints?, collections?, workflows? }</c> with per-section results.
    /// A next-targeted push stages the endpoints and collections; workflows always go live.
    /// </summary>
    private async Task<NetworkStorageResult> PutSyncAsync(NetworkStorageRequest request, string projectId,
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
        if (TryGetSection(root, "endpoints", out var pushedEndpoints) && pushedEndpoints.ValueKind == JsonValueKind.Array
            && await RefuseScriptErrorsAsync(projectId, CompileBatchEndpoints(pushedEndpoints), pushedEndpoints.GetArrayLength(), request.CancellationToken) is { } refused)
        {
            return refused;
        }

        var version = NextVersion();
        var response = new Dictionary<string, object?> { ["ok"] = true, ["source"] = "candidate" };
        var overallOk = true;
        var publish = await ResolvePublishAsync(request, ownerUserId, projectId);
        var staged = new StagedRevisionWrites();

        foreach (var (sectionName, resourceKind) in new[] { ("endpoints", "endpoint"), ("collections", "collection"), ("workflows", "workflow") })
        {
            if (!TryGetSection(root, sectionName, out var sectionValue)) continue;
            var stageSection = publish == PublishDecision.Staged && resourceKind != "workflow";
            var section = await SyncSectionAsync(projectId, resourceKind, sectionValue, version,
                async (id, name, def, ct) =>
                {
                    if (!stageSection)
                        return await UpsertDefinitionAsync(ownerUserId, projectId, resourceKind, id, name, def, version, versionSource, ct);
                    if (await IsModuleOwnedAsync(ownerUserId, projectId, resourceKind, id, name, ct))
                        return false;
                    AddStaged(staged, resourceKind, id, name, def);
                    return true;
                },
                request.CancellationToken);
            response[sectionName] = section.Body;
            overallOk &= section.Ok;
        }

        await StageAsync(ownerUserId, projectId, staged, request.CancellationToken);
        response["ok"] = overallOk;
        return ReportPublishTarget(new NetworkStorageResult(
            overallOk ? 200 : 400,
            overallOk ? null : ManagementMutationConstants.ValidationFailedCode,
            response,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "allowed"), publish);
    }

    private async Task<(bool Ok, object Body)> SyncSectionAsync(
        string projectId, string resourceKind, JsonElement incoming, long version,
        Func<string, string, JsonElement, CancellationToken, Task<bool>> upsertAsync,
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
                var written = await upsertAsync(id, displayName, definition, ct);
                results.Add(new { ok = true, resourceKind, resourceId = id, action = written ? "upsert" : "kept_module_owned" });
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

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.None, 100)]
    private static partial Regex CollectionNameRegex();

    /// <summary>
    /// Read-only validation of a Push All payload. Never writes. Validates each
    /// endpoint/collection/workflow against the same identifier requirements the
    /// native write path enforces, so preflight faithfully predicts push success.
    /// Mirrors the legacy server response shape: per-section diagnostics + a rollup summary.
    /// </summary>
    private async Task<NetworkStorageResult> PreflightSyncAsync(
        NetworkStorageRequest request, string projectId, string authDecision)
    {
        if (!TryParseBody(request.Body, out var doc, out _))
        {
            return new NetworkStorageResult(
                400, "INVALID_JSON",
                new { ok = false, mode = "preflight", error = "INVALID_JSON", message = "Body must be valid JSON." },
                Array.Empty<string>(), Array.Empty<string>(), authDecision);
        }

        var root = doc.RootElement;
        var body = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["mode"] = "preflight",
            ["publishTarget"] = request.TargetsNext ? NetworkStoragePublishTarget.Next : NetworkStoragePublishTarget.Live,
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

        return new NetworkStorageResult(
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
        var validation = new DefinitionValidationContext { ScriptRuntimes = _scriptRuntimes };
        var batchGlobals = resourceKind == "endpoint"
            ? ProjectGlobalsAfterBatch(existing, CompileBatchEndpoints(incoming))
            : new Dictionary<string, ScriptModuleInfo>();

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
            var file = GetOptionalString(item, "sourcePath");
            var diagnostics = new List<object>();
            var scriptErrors = 0;
            if (resourceKind == "collection")
            {
                var name = GetOptionalString(definition, "name");
                if (!string.IsNullOrEmpty(name) && !CollectionNameRegex().IsMatch(name))
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
            else if (resourceKind is "endpoint" or "workflow")
            {
                // A script the owner save refuses is an error: the Sync Tool refuses the batch on it and writes nothing.
                // Other findings are warnings. The push stores what it is given, as the hosted backend does, and each
                // one fails when the resource runs (for example a condition with no check answers 400
                // ENDPOINT_ERROR.CONDITION), so say so before the game does.
                var context = resourceKind == "endpoint"
                    ? EndpointScriptContext(ResolveSyncId(resourceKind, definition) ?? resourceId, batchGlobals)
                    : validation;
                foreach (var finding in NetworkStorageDefinitionValidator.Validate(resourceKind, definition, context))
                {
                    if (!finding.IsError) continue;
                    if (NetworkStorageDefinitionValidator.RefusesScriptSave(finding))
                    {
                        scriptErrors++;
                        diagnostics.Add(SyncDiagnostic("error", finding, resourceKind, resourceId, file));
                    }
                    else
                    {
                        warnings++;
                        diagnostics.Add(SyncDiagnostic("warning", finding, resourceKind, resourceId, file));
                    }
                }
            }

            if (scriptErrors > 0)
            {
                failed++;
                results.Add(new
                {
                    ok = false,
                    resourceKind,
                    resourceId,
                    error = ManagementMutationConstants.ValidationFailedCode,
                    message = $"The script of {resourceKind} \"{resourceId}\" has errors the server refuses to save, so the push refuses the whole batch until they are fixed.",
                    diagnostics,
                });
                continue;
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

    /// <summary>An endpoint of a Sync Tool batch, compiled: its store id, its definition, and the source file it names.</summary>
    private sealed record BatchEndpoint(string Id, JsonElement Definition, string? SourcePath);

    /// <summary>
    /// The batch's endpoints as the write stores them. An item that does not compile or has no id is left out here;
    /// its own result reports the problem.
    /// </summary>
    private static List<BatchEndpoint> CompileBatchEndpoints(JsonElement endpoints)
    {
        var batch = new List<BatchEndpoint>();
        foreach (var item in endpoints.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!NetworkStorageSourceResourceCompiler.TryCompile(item, "endpoint", out var definition, out _)) continue;
            if (ResolveSyncId("endpoint", definition) is not { } id) continue;
            batch.Add(new BatchEndpoint(id, definition, GetOptionalString(item, "sourcePath")));
        }
        return batch;
    }

    /// <summary>
    /// The endpoints of a per-resource push as the write stores them. Each item is the definition, and its id is the first
    /// of id, slug or name that is set, as UpsertResourceArrayAsync resolves it.
    /// </summary>
    private static List<BatchEndpoint> BatchEndpointsFromItems(IEnumerable<JsonElement> items)
    {
        var batch = new List<BatchEndpoint>();
        foreach (var item in items)
        {
            var id = GetOptionalString(item, "id") ?? GetOptionalString(item, "slug") ?? GetOptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(id)) continue;
            batch.Add(new BatchEndpoint(id, item, GetOptionalString(item, "sourcePath")));
        }
        return batch;
    }

    /// <summary>
    /// The project's globals as a batch leaves them: the stored endpoints, each one the batch replaces swapped for its
    /// incoming definition. An import of a global pushed in the same batch therefore resolves.
    /// </summary>
    private IReadOnlyDictionary<string, ScriptModuleInfo> ProjectGlobalsAfterBatch(
        IEnumerable<JsonElement> storedEndpoints, IReadOnlyList<BatchEndpoint> batch)
    {
        var endpoints = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in storedEndpoints)
            if (GetOptionalString(row, "endpoint_id") is { } id && RevisionOverlay.DefinitionColumn(row) is { } definition)
                endpoints[id] = definition;
        foreach (var endpoint in batch)
            endpoints[endpoint.Id] = endpoint.Definition;
        return NetworkStorageDefinitionValidator.ProjectGlobalModules(
            endpoints.Select(pair => (pair.Key, pair.Value)), _scriptRuntimes);
    }

    /// <summary>
    /// The validation context for one endpoint of a batch: the batch's and the project's globals, minus the endpoint itself.
    /// </summary>
    private DefinitionValidationContext EndpointScriptContext(string id, IReadOnlyDictionary<string, ScriptModuleInfo> globals) => new()
    {
        ScriptRuntimes = _scriptRuntimes,
        ResourceId = id,
        GlobalModules = globals.Where(pair => pair.Key != id).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };

    /// <summary>One finding as the Sync Tool reports it: severity, code, message, JSON path, and file, line and column when known.</summary>
    private static Dictionary<string, object?> SyncDiagnostic(
        string severity, DefinitionDiagnostic finding, string resourceKind, string resourceId, string? file)
    {
        var diagnostic = new Dictionary<string, object?>
        {
            ["severity"] = severity,
            ["code"] = finding.Code,
            ["message"] = finding.Message,
            ["resourceKind"] = resourceKind,
            ["resourceId"] = resourceId,
            ["sourcePath"] = finding.Path,
        };
        if (file is not null) diagnostic["file"] = file;
        if (finding.Line is { } line)
        {
            diagnostic["line"] = line;
            diagnostic["column"] = finding.Column;
        }
        return diagnostic;
    }

    /// <summary>
    /// Refuses an endpoint write whose scripts the owner save would refuse. <paramref name="batch"/> is every endpoint
    /// the write stores, compiled. It is checked before any section is written, so a refused write stores nothing, and an
    /// import of a global in the same batch still resolves. <paramref name="total"/> is the number of items pushed.
    /// </summary>
    private async Task<NetworkStorageResult?> RefuseScriptErrorsAsync(string projectId, IReadOnlyList<BatchEndpoint> batch, int total, CancellationToken ct)
    {
        var globals = ProjectGlobalsAfterBatch(await _store.ListEndpointsAsync(projectId, ct), batch);
        var failures = new List<object>();
        var diagnostics = new List<Dictionary<string, object?>>();
        foreach (var endpoint in batch)
        {
            var refusals = NetworkStorageDefinitionValidator
                .Validate("endpoint", endpoint.Definition, EndpointScriptContext(endpoint.Id, globals))
                .Where(NetworkStorageDefinitionValidator.RefusesScriptSave)
                .Select(finding => SyncDiagnostic("error", finding, "endpoint", endpoint.Id, endpoint.SourcePath))
                .ToList();
            if (refusals.Count == 0) continue;
            diagnostics.AddRange(refusals);
            failures.Add(new
            {
                ok = false,
                resourceKind = "endpoint",
                resourceId = endpoint.Id,
                error = ManagementMutationConstants.ValidationFailedCode,
                message = $"The script of endpoint \"{endpoint.Id}\" has errors the server refuses to save.",
                diagnostics = refusals,
            });
        }
        if (failures.Count == 0) return null;

        var response = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["source"] = "candidate",
            ["error"] = ManagementMutationConstants.ValidationFailedCode,
            ["message"] = "Nothing was written. Fix the script errors below and push again.",
            ["diagnostics"] = diagnostics,
            ["endpoints"] = new { ok = false, total, failed = failures.Count, results = failures },
        };
        return new NetworkStorageResult(400, ManagementMutationConstants.ValidationFailedCode, response,
            Array.Empty<string>(), Array.Empty<string>(), "allowed");
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

    private static NetworkStorageResult ProductionErrorResult(string resourceKind, string message) =>
        NetworkStorageResult.Error(
            500,
            $"MANAGEMENT_{resourceKind.ToUpperInvariant()}_FAILED",
            new { error = $"MANAGEMENT_{resourceKind.ToUpperInvariant()}_FAILED", message },
            authDecision: "allowed");

    // ── Shared production helpers ──

    private async Task<NetworkStorageResult> UpsertResourceArrayAsync(
        string projectId,
        IReadOnlyList<JsonElement> items,
        string resourceKind,
        Func<string, string, string?, JsonElement, long, CancellationToken, Task> upsertAsync,
        CancellationToken ct,
        Func<string, string, Task<bool>>? isKeptAsync = null)
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
                if (isKeptAsync is not null && await isKeptAsync(id, displayName))
                {
                    results.Add(new { ok = true, resourceKind, resourceId = id, action = "kept_module_owned" });
                    continue;
                }
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

    private static (IReadOnlyList<JsonElement> Items, NetworkStorageResult? Error) ParseResourceArray(string? body, string resourceKind, string requiredField)
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

    private static (string Id, JsonElement Definition, NetworkStorageResult? Error) ParseSingleResource(
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

    private static NetworkStorageResult ProductionOkResult(object body) =>
        new(200, null, body, Array.Empty<string>(), Array.Empty<string>(), "allowed");

    private static NetworkStorageResult ValidationFailedResult(string resourceKind, string message) =>
        NetworkStorageResult.Error(
            400,
            ManagementMutationConstants.ValidationFailedCode,
            new
            {
                ok = false,
                resourceKind,
                error = new { code = ManagementMutationConstants.ValidationFailedCode, message }
            },
            authDecision: "allowed");

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

    private static bool IsMutationMethod(string method) =>
        string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);

    private static NetworkStorageResult NotImplementedResult(string routeName)
    {
        return NetworkStorageResult.Error(
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

    private static NetworkStorageResult ManagementAuthError()
    {
        return NetworkStorageResult.Error(
            401,
            "UNAUTHORIZED",
            new { ok = false, error = new { code = "UNAUTHORIZED", message = "Invalid or missing management API key." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: "denied");
    }

    private static NetworkStorageResult ProjectDisabledResult(string authDecision)
    {
        return NetworkStorageResult.Error(
            403,
            "PROJECT_DISABLED",
            new { ok = false, error = new { code = "PROJECT_DISABLED", message = "This project is currently disabled." } },
            storagePathsRead: Array.Empty<string>(),
            authDecision: authDecision);
    }
}
