using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Shared camelCase naming policy so all <c>SerializeToElement</c> calls in this file
/// produce property names matching ASP.NET Core's default JSON serialization.
/// </summary>
file static class JsonOpts
{
    internal static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public abstract class NetworkStorageEndpointExecutionCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient client;

    protected NetworkStorageEndpointExecutionCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    // ── BuildRequest helpers ──

    private static NetworkStorageCandidateRequest BuildGetRequest(
        string projectId,
        string endpointSlug,
        string? apiKey = "sbox_sk_testpublickey")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/endpoints/{projectId}/{endpointSlug}");
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageCandidateRequest(
            route,
            query,
            ContentType: null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: new NetworkStorageCredentials(
                ApiKey: apiKey,
                SteamId: null,
                AuthSessionToken: null,
                SessionToken: null,
                EncryptedRequestId: null),
            Body: null,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    private static NetworkStorageCandidateRequest BuildPostRequest(
        string projectId,
        string? endpointSlug,
        string? body,
        string? apiKey = "sbox_sk_testpublickey")
    {
        // POST /v3/endpoints/:projectId — slug lives in body
        var path = endpointSlug is not null
            ? $"/v3/endpoints/{projectId}/{endpointSlug}"
            : $"/v3/endpoints/{projectId}";
        var route = NetworkStorageRouteClassifier.Classify("POST", path);
        var query = new Dictionary<string, string>();
        if (apiKey is not null)
        {
            query["apiKey"] = apiKey;
        }

        return new NetworkStorageCandidateRequest(
            route,
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

    // ── Auth failure tests ──

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver(null, "demo-project", null));

        var result = await handler.ExecuteAsync(BuildGetRequest("demo-project", "my-endpoint", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
        Assert.Empty(result.IntendedWritePaths);
        Assert.Empty(result.StoragePathsRead);
    }

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("bad-key", "demo-project", null));

        var result = await handler.ExecuteAsync(BuildGetRequest("demo-project", "my-endpoint", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
    }

    // ── Project disabled test ──

    [SkippableFact]
    public async Task DisabledProjectReturnsForbidden()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public", enabled: false));

        var result = await handler.ExecuteAsync(BuildGetRequest("demo-project", "my-endpoint", apiKey: "test-public-key"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("PROJECT_DISABLED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
    }

    // ── Successful dry-run: GET ──

    [SkippableFact]
    public async Task GetWithValidKeyReturnsDryRunWithDependencyInventory()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"));

        var result = await handler.ExecuteAsync(BuildGetRequest("demo-project", "load-profile", apiKey: "test-public-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);
        Assert.NotEmpty(result.IntendedWritePaths);

        // Verify intended write paths are placeholder logical paths
        Assert.Contains(result.IntendedWritePaths,
            p => p.Contains("demo-project", StringComparison.Ordinal) && p.Contains("load-profile", StringComparison.Ordinal));
        Assert.Contains(result.IntendedWritePaths,
            p => p.EndsWith("storage-write", StringComparison.Ordinal));
        Assert.Contains(result.IntendedWritePaths,
            p => p.EndsWith("billing-debit", StringComparison.Ordinal));
        Assert.Contains(result.IntendedWritePaths,
            p => p.EndsWith("analytics-event", StringComparison.Ordinal));
        Assert.Contains(result.IntendedWritePaths,
            p => p.EndsWith("audit-log", StringComparison.Ordinal));
        Assert.Contains(result.IntendedWritePaths,
            p => p.EndsWith("webhook-dispatch", StringComparison.Ordinal));

        Assert.Empty(result.StoragePathsRead);

        // Verify dry-run response body shape
        var json = JsonSerializer.SerializeToElement(result.Body, JsonOpts.CamelCase);
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.Equal("dry_run", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("demo-project", json.GetProperty("projectId").GetString());
        Assert.Equal("load-profile", json.GetProperty("endpointSlug").GetString());
        Assert.Equal("GET", json.GetProperty("method").GetString());
        Assert.Equal("route", json.GetProperty("slugSource").GetString());
        Assert.Equal("public", json.GetProperty("authDecision").GetString());
        Assert.True(json.GetProperty("projectEnabled").GetBoolean());
        Assert.True(json.GetProperty("sideEffectsSuppressed").GetBoolean());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("suppressionReason").GetString());

        // Verify dependency inventory categories exist
        var inventory = json.GetProperty("dependencyInventory");
        Assert.True(inventory.GetProperty("auth").GetProperty("resolved").GetBoolean());
        Assert.Equal("public", inventory.GetProperty("auth").GetProperty("keyType").GetString());
        Assert.True(inventory.GetProperty("project").GetProperty("enabled").GetBoolean());
        Assert.Equal("GET", inventory.GetProperty("endpoint").GetProperty("method").GetString());
        Assert.Equal("load-profile", inventory.GetProperty("endpoint").GetProperty("slug").GetString());
        Assert.True(inventory.GetProperty("computeBilling").GetProperty("wouldDebitCredits").GetBoolean());
        Assert.True(inventory.GetProperty("computeBilling").GetProperty("wouldCheckQuota").GetBoolean());
        Assert.True(inventory.GetProperty("storageMutation").GetProperty("wouldPerformWrites").GetBoolean());
        Assert.True(inventory.GetProperty("workflowExecution").GetProperty("requiredForWorkflowSteps").GetBoolean());
        Assert.True(inventory.GetProperty("analyticsEvent").GetProperty("wouldEmitEvent").GetBoolean());
        Assert.True(inventory.GetProperty("webhookDispatch").GetProperty("wouldFireWebhook").GetBoolean());
        Assert.True(inventory.GetProperty("rateLimiting").GetProperty("wouldCheckRateLimits").GetBoolean());
    }

    // ── Successful dry-run: POST ──

    [SkippableFact]
    public async Task PostWithValidKeyReturnsDryRunWithDependencyInventory()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-secret-key", "demo-project", "secret"));

        var result = await handler.ExecuteAsync(BuildPostRequest(
            "demo-project",
            endpointSlug: "save-score",
            body: @"{""score"":100,""player"":""76561197960287930""}",
            apiKey: "test-secret-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("secret", result.AuthDecision);
        Assert.NotEmpty(result.IntendedWritePaths);

        var json = JsonSerializer.SerializeToElement(result.Body, JsonOpts.CamelCase);
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.Equal("dry_run", json.GetProperty("status").GetString());
        Assert.Equal("save-score", json.GetProperty("endpointSlug").GetString());
        Assert.Equal("POST", json.GetProperty("method").GetString());
        Assert.Equal("route", json.GetProperty("slugSource").GetString());
        Assert.Equal("secret", json.GetProperty("authDecision").GetString());
        Assert.True(json.GetProperty("sideEffectsSuppressed").GetBoolean());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("suppressionReason").GetString());
    }

    // ── POST without path slug (slug in body) ──

    [SkippableFact]
    public async Task PostWithoutPathSlugReturnsSlugSourceBody()
    {
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-secret-key", "demo-project", "secret"));

        var result = await handler.ExecuteAsync(BuildPostRequest(
            "demo-project",
            endpointSlug: null,
            body: @"{""endpoint"":""body-slug-endpoint""}",
            apiKey: "test-secret-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body, JsonOpts.CamelCase);
        Assert.Null(json.GetProperty("endpointSlug").GetString());
        Assert.Equal("body", json.GetProperty("slugSource").GetString());
        Assert.Equal("POST", json.GetProperty("method").GetString());

        // Intended write paths should use the placeholder
        Assert.Contains(result.IntendedWritePaths,
            p => p.Contains("<slug-from-body>", StringComparison.Ordinal));
    }

    // ── CanHandle tests ──

    [SkippableFact]
    public void CanHandleReturnsTrueForGet()
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/endpoints/demo-project/my-endpoint");
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"));

        Assert.True(handler.CanHandle(route));
    }

    [SkippableFact]
    public void CanHandleReturnsTrueForPost()
    {
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/endpoints/demo-project/my-endpoint");
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-secret-key", "demo-project", "secret"));

        Assert.True(handler.CanHandle(route));
    }

    [SkippableFact]
    public void CanHandleReturnsFalseForDelete()
    {
        // DELETE on endpoint routes doesn't exist in catalog, but test unknown method
        var route = NetworkStorageRouteClassifier.Classify("DELETE", "/v3/endpoints/demo-project/my-endpoint");
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"));

        Assert.False(handler.CanHandle(route));
    }

    [SkippableFact]
    public void CanHandleReturnsFalseForPut()
    {
        var route = NetworkStorageRouteClassifier.Classify("PUT", "/v3/endpoints/demo-project/my-endpoint");
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"));

        Assert.False(handler.CanHandle(route));
    }

    [SkippableFact]
    public void CanHandleReturnsFalseForNonEndpointFamily()
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/values/demo-project");
        var handler = new EndpointExecutionCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"));

        Assert.False(handler.CanHandle(route));
    }

    // ── Shadow endpoint integration test ──

    // ── Fakes ──

    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _projectId;
        private readonly string? _keyType;
        private readonly bool _enabled;

        public FakeKeyResolver(string? validKey, string? projectId, string? keyType, bool enabled = true)
        {
            _validKey = validKey;
            _projectId = projectId;
            _keyType = keyType;
            _enabled = enabled;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
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
                    Enabled: _enabled,
                    KeyType: _keyType));
        }
    }
}

public sealed class NetworkStorageEndpointExecutionCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageEndpointExecutionCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageEndpointExecutionCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageEndpointExecutionCandidateTests<PostgresHostFactory>(factory);
