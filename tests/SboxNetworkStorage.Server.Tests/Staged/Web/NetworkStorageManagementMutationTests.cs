using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageManagementMutationTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient _client;

    protected NetworkStorageManagementMutationTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageRequest BuildRequest(
        string method,
        string projectId,
        string? apiKey = "sbox_sk_testsecretkey",
        string routeSuffix = "endpoints",
        string? additionalParam = null,
        string? body = null)
    {
        var path = additionalParam is not null
            ? $"/v3/manage/{projectId}/{routeSuffix}/{additionalParam}"
            : $"/v3/manage/{projectId}/{routeSuffix}";

        var route = NetworkStorageRouteClassifier.Classify(method, path);
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageRequest(route,
        query,
        ContentType: "application/json",
        AuthSignals: new Dictionary<string, bool>(),
        Credentials: new NetworkStorageCredentials(
            ApiKey: apiKey,
            SteamId: null,
            AuthSessionToken: null,
            SessionToken: null,
            EncryptedRequestId: null),
        Body: body,
        ResolvedOwnerUserId: null,
        CancellationToken: CancellationToken.None);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Fake write methods throw — ensures tests detect any accidental production writes.
    /// </summary>
    private static void AssertWriteNotCalled()
    {
        throw new InvalidOperationException("Production write should never be called from a dry-run handler.");
    }

    // ══════════════════════════════════════════════════════════════════
    // Auth failure: missing/invalid/public key returns management auth error
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver(null, "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: null, routeSuffix: "game-values"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNAUTHORIZED",
            json.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-wrong", routeSuffix: "collections"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
    }

    [SkippableFact]
    public async Task PublicApiKeyRejectedForManagementMutation()
    {
        var resolver = new FakeKeyResolver("pk-valid", "proj-1", keyType: "public");
        var handler = new ManagementMutationHandler(
            resolver,
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "pk-valid", routeSuffix: "endpoints"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
    }

    [SkippableFact]
    public async Task DisabledProjectReturnsProjectDisabled()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "disabled-proj"),
            new FakeWorkspace("disabled-proj", enabled: false),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "disabled-proj", apiKey: "sk-valid", routeSuffix: "game-values"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("PROJECT_DISABLED", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("PROJECT_DISABLED",
            json.GetProperty("error").GetProperty("code").GetString());
    }

    // ══════════════════════════════════════════════════════════════════
    // GET routes not handled by this mutation handler
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task GetMethodNotHandled()
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/manage/proj-1/endpoints");
        Assert.False(new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System).CanHandle(route));
    }

    // ══════════════════════════════════════════════════════════════════
    // Unsupported route returns explicit not-implemented marker
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task UnhandledDispatchReturnsNotImplemented()
    {
        // Verify that executing a route that isn't in the catalog's management mutation
        // entries still handles gracefully (null entry → NotImplementedResult).
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        // Build a request with a suffix that has no catalog entry
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "nonexistent-route"));

        // Handler shouldn't crash; returns NotImplemented if ExtractResourcePath fails
        Assert.Equal(501, result.StatusCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        var errorProp = json.GetProperty("error");
        Assert.Contains("NOT_IMPLEMENTED", errorProp.GetString() ?? "");
    }

    // ══════════════════════════════════════════════════════════════════
    // Fakes
    // ══════════════════════════════════════════════════════════════════

    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _projectId;
        private readonly string? _keyType;
        private readonly bool _disabled;

        public FakeKeyResolver(string? validKey, string? projectId, string? keyType = "secret", bool disabled = false)
        {
            _validKey = validKey;
            _projectId = projectId;
            _keyType = keyType;
            _disabled = disabled;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(
            string apiKey, string projectId, CancellationToken cancellationToken)
        {
            if (_validKey is null || _keyType is null)
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            if (!string.Equals(apiKey, _validKey, StringComparison.Ordinal))
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            return Task.FromResult<StorageApiKeyAuthResult?>(
                new StorageApiKeyAuthResult(
                    UserId: 42,
                    ProjectId: projectId,
                    Enabled: !_disabled,
                    KeyType: _keyType));
        }
    }

    private sealed class FakeWorkspace : IWorkspaceStore
    {
        private readonly string _projectId;
        private readonly bool _enabled;

        public bool ThrowOnWrite { get; set; }

        public FakeWorkspace(string projectId, bool enabled = true)
        {
            _projectId = projectId;
            _enabled = enabled;
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
        {
            return Task.FromResult<T?>(default);
        }

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceProject>>(
                new List<WorkspaceProject>
                {
                    new(Id: _projectId, Name: "Test Project", Description: null, Enabled: _enabled,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                });
        }

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken ct)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken ct)
        {
            if (ThrowOnWrite) AssertWriteNotCalled();
            return Task.CompletedTask;
        }

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken ct)
        {
            if (ThrowOnWrite) AssertWriteNotCalled();
            return Task.CompletedTask;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken ct)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken ct)
        {
            if (ThrowOnWrite) AssertWriteNotCalled();
            return Task.CompletedTask;
        }

        public Task DeleteRawAsync(string absolutePath, CancellationToken ct)
        {
            if (ThrowOnWrite) AssertWriteNotCalled();
            return Task.CompletedTask;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Production write tests: SuppressSideEffects = false
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task PutEndpoints_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"slug":"get-leaderboard","method":"GET","enabled":true,"definition":{"type":"leaderboard"}}]""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Endpoints);
    }

    [SkippableFact]
    public async Task PutCollections_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"name":"scores","visibility":"public","definition":{"schema":[{"name":"player_id","type":"string"}]}}]""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "collections", body: body));
        Assert.Single(store.Collections);
    }

    [SkippableFact]
    public async Task PutWorkflows_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"name":"on-submit","definition":{"nodes":[]}}]""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "workflows", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Workflows);
    }

    [SkippableFact]
    public async Task PutGameValues_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """{"values":{"maxScore":1000}}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "game-values", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.GameValues);
    }

    [SkippableFact]
    public async Task PutRateLimitRules_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """{"rules":[{"name":"global","requestsPerSecond":10}]}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "rate-limit-rules", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.RateLimitRules);
    }

    [SkippableFact]
    public async Task ProductionWrite_SetsVersionTimestamp()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"slug":"get-leaderboard","method":"GET","enabled":true,"definition":{}}]""";
        await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        var stored = store.Endpoints.Single().Value;
        Assert.True(stored.GetProperty("version").GetInt64() > 0);
    }

    [SkippableFact]
    public async Task PostEndpoints_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"slug":"post-leaderboard","method":"POST","enabled":true,"definition":{"type":"leaderboard"}}]""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Endpoints);
    }

    [SkippableFact]
    public async Task PostQueries_PerformsRealWriteToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """[{"id":"q-1","name":"top-scores","requiresSecretKey":false,"definition":{"sql":"SELECT * FROM scores"}}]""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "queries", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Queries);
    }

    [SkippableFact]
    public async Task DeleteEndpoint_RemovesFromStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        await store.UpsertEndpointAsync("proj-1", "ep-1", "leaderboard", "GET", true, JsonDocument.Parse("{}").RootElement, null, 1, CancellationToken.None);
        Assert.Single(store.Endpoints);

        var result = await handler.ExecuteAsync(
            BuildRequest("DELETE", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", additionalParam: "ep-1"));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(store.Endpoints);
    }

    [SkippableFact]
    public async Task DeleteCollection_RemovesFromStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        await store.UpsertCollectionAsync("proj-1", "col-1", "scores", "public", JsonDocument.Parse("{}").RootElement, 1, CancellationToken.None);
        Assert.Single(store.Collections);

        var result = await handler.ExecuteAsync(
            BuildRequest("DELETE", "proj-1", apiKey: "sk-valid", routeSuffix: "collections", additionalParam: "col-1"));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(store.Collections);
    }

    [SkippableFact]
    public async Task DeleteQuery_RemovesFromStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        await store.UpsertQueryAsync("proj-1", "q-1", "top-scores", false, JsonDocument.Parse("{}").RootElement, 1, CancellationToken.None);
        Assert.Single(store.Queries);

        var result = await handler.ExecuteAsync(
            BuildRequest("DELETE", "proj-1", apiKey: "sk-valid", routeSuffix: "queries", additionalParam: "q-1"));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(store.Queries);
    }
    // ══════════════════════════════════════════════════════════════════
    // Sync preflight (read-only validation) — POST /sync/preflight
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task PreflightSync_ValidPayload_Passes()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var body = """
        {"endpoints":[{"slug":"get-leaderboard","method":"GET"}],
         "collections":[{"name":"scores"}],
         "workflows":[{"name":"on-submit"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(result.IntendedWritePaths);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("preflight", json.GetProperty("mode").GetString());
        Assert.Equal("would_create", json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("action").GetString());
        Assert.Equal(3, json.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("summary").GetProperty("failed").GetInt32());
    }

    [SkippableFact]
    public async Task PreflightSync_MissingEndpointSlug_Fails()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var body = """{"endpoints":[{"method":"GET"}]}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("PREFLIGHT_VALIDATION_FAILED", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        var epResult = json.GetProperty("endpoints").GetProperty("results")[0];
        Assert.False(epResult.GetProperty("ok").GetBoolean());
        Assert.Equal("ENDPOINT_SLUG_REQUIRED",
            epResult.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task PreflightSync_ScriptSyntaxError_IsAnErrorAndFailsPreflight()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[{"slug":"broken","sourcePath":"hello.endpoint.js","runtime":"javascript","script":"const a = 1;\nconst b = ;\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("PREFLIGHT_VALIDATION_FAILED", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var endpoint = json.GetProperty("endpoints").GetProperty("results")[0];
        Assert.False(endpoint.GetProperty("ok").GetBoolean());
        var syntax = endpoint.GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "SCRIPT_SYNTAX");
        Assert.Equal("error", syntax.GetProperty("severity").GetString());
        Assert.Equal("/script", syntax.GetProperty("sourcePath").GetString());
        Assert.Equal("hello.endpoint.js", syntax.GetProperty("file").GetString());
        Assert.Equal(2, syntax.GetProperty("line").GetInt32());
        Assert.True(syntax.GetProperty("column").GetInt32() > 0);
        Assert.StartsWith("Line 2, column ", syntax.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task PreflightSync_ImportOfMissingGlobal_IsAnErrorAndFailsPreflight()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[{"slug":"caller","runtime":"javascript","script":"import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, coins: coinsOf() }; }\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("PREFLIGHT_VALIDATION_FAILED", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var diagnostics = json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("diagnostics").EnumerateArray();
        var missing = diagnostics.Single(item => item.GetProperty("code").GetString() == "GLOBAL_NOT_FOUND");
        Assert.Equal("error", missing.GetProperty("severity").GetString());
    }

    [SkippableFact]
    public async Task PreflightSync_ExistingEndpoint_ReportsWouldUpdate()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync("proj-1", "get-leaderboard", "get-leaderboard", "GET", true,
            JsonDocument.Parse("{}").RootElement, null, 1, CancellationToken.None);

        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """{"endpoints":[{"slug":"get-leaderboard","method":"GET"}]}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("would_update",
            json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("action").GetString());
    }

    [SkippableFact]
    public async Task PreflightSync_SectionNotArray_Fails()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var body = """{"endpoints":{"slug":"x"}}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(400, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("endpoints").GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task PreflightSync_InvalidCollectionName_Warns()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var body = """{"collections":[{"name":"MyScores"}]}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        // A format warning does not fail preflight (push would still accept it).
        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
    }

    // The scaffold shipped by the editor library once emitted a condition with field/operator and no check.
    // The executor answers 400 ENDPOINT_ERROR.CONDITION for it at run time; the push still stores what it is given.
    private const string CheckLessCondition = """
        {"endpoints":[{"slug":"init-player","method":"POST","enabled":true,
          "steps":[{"id":"guard","type":"condition","field":"player","operator":"is_null","onFail":"return"}]}]}
        """;

    [SkippableFact]
    public async Task PreflightSync_ConditionWithoutCheck_WarnsButStillPasses()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: CheckLessCondition));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.GetProperty("summary").GetProperty("warnings").GetInt32());
        var diagnostics = json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("warning", d.GetProperty("severity").GetString());
            Assert.Equal("init-player", d.GetProperty("resourceId").GetString());
        });
        // The missing check, and the scaffold's string onFail ("return") that the executor also rejects.
        Assert.Equal(["INVALID_ON_FAIL:/steps/0/onFail", "MISSING_CHECK:/steps/0/check"],
            diagnostics.Select(d => $"{d.GetProperty("code").GetString()}:{d.GetProperty("sourcePath").GetString()}").Order().ToArray());
    }

    [SkippableFact]
    public async Task PutSync_ConditionWithoutCheck_IsStoredAsGiven()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: CheckLessCondition));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(await store.ListEndpointsAsync("proj-1", CancellationToken.None));
    }

    // ══════════════════════════════════════════════════════════════════
    // Sync batch push — PUT /sync
    // ══════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task PutSync_BatchWritesAllSectionsToStore()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """
        {"endpoints":[{"slug":"get-leaderboard","method":"GET","definition":{"type":"leaderboard"}}],
         "collections":[{"name":"scores","definition":{}}],
         "workflows":[{"name":"on-submit","definition":{"nodes":[]}}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Endpoints);
        Assert.Single(store.Collections);
        Assert.Single(store.Workflows);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task PutSync_InvalidItem_ReturnsSectionFailure()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);

        var body = """{"endpoints":[{"method":"GET"}]}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Empty(store.Endpoints);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("endpoints").GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task PutSync_BrokenScript_RefusesWholeBatchAndWritesNothing()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[
          {"slug":"get-leaderboard","method":"GET","definition":{"type":"leaderboard"}},
          {"slug":"hello","sourcePath":"hello.endpoint.js","runtime":"javascript","script":"const a = 1;\nconst b = ;\n"}],
         "collections":[{"name":"scores","definition":{}}],
         "workflows":[{"name":"on-submit","definition":{"nodes":[]}}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("VALIDATION_FAILED", result.PublicErrorCode);
        Assert.Empty(result.IntendedWritePaths);
        Assert.Empty(store.Endpoints);
        Assert.Empty(store.Collections);
        Assert.Empty(store.Workflows);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("candidate", json.GetProperty("source").GetString());
        var failed = json.GetProperty("endpoints").GetProperty("results").EnumerateArray()
            .Single(item => !item.GetProperty("ok").GetBoolean());
        Assert.Equal("hello", failed.GetProperty("resourceId").GetString());
        var flat = Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("SCRIPT_SYNTAX", flat.GetProperty("code").GetString());
        Assert.Equal("error", flat.GetProperty("severity").GetString());
        Assert.Equal("endpoint", flat.GetProperty("resourceKind").GetString());
        Assert.Equal("hello", flat.GetProperty("resourceId").GetString());
        Assert.Equal("hello.endpoint.js", flat.GetProperty("file").GetString());
        Assert.Equal(2, flat.GetProperty("line").GetInt32());
        Assert.True(flat.GetProperty("column").GetInt32() > 0);
        var syntax = failed.GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "SCRIPT_SYNTAX");
        Assert.Equal("error", syntax.GetProperty("severity").GetString());
        Assert.Equal("hello.endpoint.js", syntax.GetProperty("file").GetString());
        Assert.Equal(2, syntax.GetProperty("line").GetInt32());
        Assert.True(syntax.GetProperty("column").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task PutSync_ImportOfGlobalInSameBatch_ResolvesAndWrites()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[
          {"slug":"bank","runtime":"javascript","scope":"global","script":"export function coinsOf() { return 7; }\n"},
          {"slug":"caller","runtime":"javascript","script":"import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, coins: coinsOf() }; }\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, store.Endpoints.Count);
    }

    [SkippableFact]
    public async Task PutSync_ImportOfStoredGlobal_Resolves()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync("proj-1", "bank", "bank", "GET", true,
            JsonDocument.Parse("""{"runtime":"javascript","scope":"global","script":"export function coinsOf() { return 7; }\n"}""").RootElement,
            null, 1, CancellationToken.None);
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[{"slug":"caller","runtime":"javascript","script":"import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, coins: coinsOf() }; }\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, store.Endpoints.Count);
    }

    [SkippableFact]
    public async Task PutSync_ImportOfMissingGlobal_RefusesBatchAndWritesNothing()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        {"endpoints":[{"slug":"caller","runtime":"javascript","script":"import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, coins: coinsOf() }; }\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("VALIDATION_FAILED", result.PublicErrorCode);
        Assert.Empty(store.Endpoints);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var diagnostics = json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("diagnostics").EnumerateArray();
        Assert.Equal("GLOBAL_NOT_FOUND", diagnostics.Single().GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task PutSync_BatchGlobalWithoutImportedExport_RefusesAndKeepsStoredGlobal()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync("proj-1", "bank", "bank", "GET", true,
            JsonDocument.Parse("""{"runtime":"javascript","scope":"global","script":"export function coinsOf() { return 7; }\n"}""").RootElement,
            null, 1, CancellationToken.None);
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        // The batch replaces the stored bank without coinsOf, so the caller's import no longer resolves.
        var body = """
        {"endpoints":[
          {"slug":"bank","runtime":"javascript","scope":"global","script":"export function other() { return 1; }\n"},
          {"slug":"caller","runtime":"javascript","script":"import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, coins: coinsOf() }; }\n"}]}
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("VALIDATION_FAILED", result.PublicErrorCode);
        var stored = Assert.Single(store.Endpoints).Value;
        Assert.Contains("coinsOf", stored.GetProperty("definition_json").GetProperty("script").GetString());
        var json = JsonSerializer.SerializeToElement(result.Body);
        var diagnostics = json.GetProperty("endpoints").GetProperty("results").EnumerateArray()
            .Single(item => !item.GetProperty("ok").GetBoolean())
            .GetProperty("diagnostics").EnumerateArray();
        Assert.Equal("GLOBAL_EXPORT_NOT_FOUND", diagnostics.Single().GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task SaveOwnerResource_BrokenScript_IsRefusedAndWritesNothing()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var resource = ParseJson("""{"slug":"hello","runtime":"javascript","script":"const a = 1;\nconst b = ;\n"}""");
        var result = await handler.SaveOwnerResourceAsync(1, "proj-1", "endpoint", resource, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("PREFLIGHT_VALIDATION_FAILED", result.PublicErrorCode);
        Assert.Empty(store.Endpoints);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task PutEndpoints_BrokenScript_RefusesWholeRequestAndWritesNothing()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """
        [{"slug":"get-leaderboard","method":"GET","enabled":true,"definition":{"type":"leaderboard"}},
         {"slug":"hello","method":"GET","enabled":true,"sourcePath":"hello.endpoint.js","runtime":"javascript","script":"const a = 1;\nconst b = ;\n"}]
        """;
        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("VALIDATION_FAILED", result.PublicErrorCode);
        Assert.Empty(result.IntendedWritePaths);
        Assert.Empty(store.Endpoints);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        var flat = Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("SCRIPT_SYNTAX", flat.GetProperty("code").GetString());
        Assert.Equal("hello", flat.GetProperty("resourceId").GetString());
        Assert.Equal("hello.endpoint.js", flat.GetProperty("file").GetString());
        Assert.Equal(2, flat.GetProperty("line").GetInt32());
    }

    [SkippableFact]
    public async Task PatchEndpoint_BrokenScript_IsRefusedAndWritesNothing()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """{"slug":"hello","sourcePath":"hello.endpoint.js","runtime":"javascript","script":"const a = 1;\nconst b = ;\n"}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PATCH", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("VALIDATION_FAILED", result.PublicErrorCode);
        Assert.Empty(result.IntendedWritePaths);
        Assert.Empty(store.Endpoints);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        var flat = Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("SCRIPT_SYNTAX", flat.GetProperty("code").GetString());
        Assert.Equal("hello.endpoint.js", flat.GetProperty("file").GetString());
        Assert.Equal(2, flat.GetProperty("line").GetInt32());
    }

    [SkippableFact]
    public async Task PatchEndpoint_ValidScript_IsStored()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            endpointExecutor: null,
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var body = """{"slug":"caller","runtime":"javascript","script":"export default function handle() { return { ok: true }; }\n"}""";
        var result = await handler.ExecuteAsync(
            BuildRequest("PATCH", "proj-1", apiKey: "sk-valid", routeSuffix: "endpoints", body: body));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Endpoints);
    }

    [SkippableFact]
    public async Task PreflightSync_LegacyJsonSourceWrapper_Passes()
    {
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            new InMemoryNetworkStorageStore(),
            TimeProvider.System);
        var body = JsonSerializer.Serialize(new
        {
            endpoints = new[]
            {
                new
                {
                    kind = "endpoint",
                    id = "legacy-save",
                    sourceFormat = "json",
                    sourcePath = "legacy-save.json",
                    sourceText = """{"slug":"legacy-save","method":"POST","steps":[]}""",
                },
            },
        });

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "sync/preflight", body: body));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("legacy-save",
            json.GetProperty("endpoints").GetProperty("results")[0].GetProperty("resourceId").GetString());
    }

    [SkippableFact]
    public async Task PutSync_SourceWrappersCompileBeforePersistence()
    {
        var store = new InMemoryNetworkStorageStore();
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System);
        var body = JsonSerializer.Serialize(new
        {
            endpoints = new[]
            {
                new
                {
                    kind = "endpoint",
                    id = "legacy-save",
                    sourceFormat = "json",
                    sourcePath = "legacy-save.json",
                    sourceText = """{"slug":"legacy-save","method":"POST","steps":[{"id":"done","type":"return"}]}""",
                },
            },
            collections = new[]
            {
                new
                {
                    kind = "collection",
                    id = "players",
                    sourceFormat = "yaml",
                    sourcePath = "players.collection.yml",
                    sourceText = """
                        sourceVersion: 1
                        kind: collection
                        id: players
                        name: Players
                        definition:
                          collectionType: player
                          schema:
                            score:
                              type: number
                        """,
                },
            },
        });

        var result = await handler.ExecuteAsync(
            BuildRequest("PUT", "proj-1", apiKey: "sk-valid", routeSuffix: "sync", body: body));

        Assert.Equal(200, result.StatusCode);
        var endpointRow = await store.ReadEndpointAsync("proj-1", "legacy-save", CancellationToken.None);
        Assert.NotNull(endpointRow);
        var endpointDefinition = endpointRow.Value.GetProperty("definition_json");
        Assert.Equal("POST", endpointDefinition.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Array, endpointDefinition.GetProperty("steps").ValueKind);

        var collectionRow = await store.ReadCollectionAsync("proj-1", "players", CancellationToken.None);
        Assert.NotNull(collectionRow);
        var collectionDefinition = collectionRow.Value.GetProperty("definition_json");
        Assert.Equal("player", collectionDefinition.GetProperty("collectionType").GetString());
        Assert.Equal("number",
            collectionDefinition.GetProperty("schema").GetProperty("score").GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task AutoTest_ExecutesNativeEndpointAndReturnsClientShape()
    {
        var definition = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?>(),
            ["response"] = new Dictionary<string, object?>
            {
                ["status"] = 200d,
                ["body"] = new Dictionary<string, object?> { ["ok"] = true },
            },
        };
        var definitionJson = JsonSerializer.SerializeToElement(definition);
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync(
            "proj-1", "ping", "ping", "POST", true, definitionJson, null, 1, CancellationToken.None);
        var executor = new EndpointExecutor(new AutoTestDataSource(definition));
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            executor);

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "auto-test", body: """{"slug":"ping"}"""));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("passed").GetBoolean());
        Assert.Equal("ping", json.GetProperty("endpoint").GetString());
        Assert.Equal("POST", json.GetProperty("method").GetString());
    }

    [SkippableFact]
    public async Task AutoTest_ResolvesGameValuesLikeTestEndpoint()
    {
        var definition = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?>(),
            ["response"] = new Dictionary<string, object?>
            {
                ["status"] = 200d,
                ["body"] = new Dictionary<string, object?> { ["granted"] = "{{values.rules.xp_per_level}}" },
            },
        };
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync(
            "proj-1", "ping", "ping", "POST", true, JsonSerializer.SerializeToElement(definition), null, 1, CancellationToken.None);
        var handler = new ManagementMutationHandler(
            new FakeKeyResolver("sk-valid", "proj-1"),
            new FakeWorkspace("proj-1"),
            store,
            TimeProvider.System,
            new EndpointExecutor(new AutoTestDataSource(definition)),
            valuesProvider: new FixedValuesProvider(new Dictionary<string, object?>
            {
                ["rules"] = new Dictionary<string, object?> { ["xp_per_level"] = 100d },
            }));

        var result = await handler.ExecuteAsync(
            BuildRequest("POST", "proj-1", apiKey: "sk-valid", routeSuffix: "auto-test", body: """{"slug":"ping"}"""));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("passed").GetBoolean());
        Assert.Equal(100, json.GetProperty("result").GetProperty("body").GetProperty("granted").GetDouble());
    }

    private sealed class FixedValuesProvider(Dictionary<string, object?> values) : IQueryValuesContextProvider
    {
        public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    private sealed class AutoTestDataSource(Dictionary<string, object?> definition) : IEndpointDataSource
    {
        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(
            string projectId, string endpointSlug, CancellationToken ct) =>
            Task.FromResult<Dictionary<string, object?>?>(endpointSlug == "ping" ? definition : null);

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(
            string projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>([]);

        public Task<object?> ReadRecordAsync(
            string projectId, string collectionId, string key, CancellationToken ct) =>
            Task.FromResult<object?>(null);

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(
            string projectId, string collectionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<object?>>([]);

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(
            string projectId, string workflowId, CancellationToken ct) =>
            Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IReadOnlyList<string>> ListWorkflowIdsAsync(string projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListEndpointIdsAsync(string projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Auto-test must not persist record writes.");

        public Task<object?> ReadGlobalRecordAsync(
            string projectId, string collectionId, string recordId, CancellationToken ct) =>
            Task.FromResult<object?>(null);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> IsWebhookOutboxEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<EndpointFetchPolicy> GetEndpointFetchPolicyAsync(string projectId, CancellationToken ct) => Task.FromResult(EndpointFetchPolicy.Closed);

        public Task WriteGlobalRecordAsync(
            string projectId, string collectionId, string recordId,
            IReadOnlyDictionary<string, object?> payload, CancellationToken ct) =>
            throw new InvalidOperationException("Auto-test must not persist global writes.");
    }

}

public sealed class NetworkStorageManagementMutationTests_Sqlite(SqliteHostFactory factory) : NetworkStorageManagementMutationTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageManagementMutationTests_Postgres(PostgresHostFactory factory) : NetworkStorageManagementMutationTests<PostgresHostFactory>(factory);
