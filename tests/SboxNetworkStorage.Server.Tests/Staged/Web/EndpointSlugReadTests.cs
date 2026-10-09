using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class EndpointSlugReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory _factory;
    private readonly HttpClient _client;

    protected EndpointSlugReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
        _client = factory.WithWebHostBuilder(builder =>
        {
            
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageRequest BuildRequest(string projectId, string slug, string? apiKey = "test-key")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/endpoints/{projectId}/{slug}");
        var query = new Dictionary<string, string>();
        if (apiKey is not null) query["apiKey"] = apiKey;
        return new NetworkStorageRequest(
            route, query, null, new Dictionary<string, bool>(),
            new NetworkStorageCredentials(apiKey, null, null, null, null),
            null, null, CancellationToken.None);
    }

    private static JsonElement Endpoints(params (string slug, string method)[] items)
    {
        var arr = string.Join(",", items.Select(s => $$"""{"slug":"{{s.slug}}","method":"{{s.method}}","enabled":true}"""));
        return JsonDocument.Parse($"[{arr}]").RootElement;
    }

    private static async Task<InMemoryNetworkStorageStore> StoreWithEndpointsAsync(JsonElement endpoints)
    {
        var store = new InMemoryNetworkStorageStore();
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            var slug = endpoint.GetProperty("slug").GetString()!;
            await store.UpsertEndpointAsync("proj-1", slug, slug,
                endpoint.GetProperty("method").GetString(), true, endpoint, null, 1, CancellationToken.None);
        }
        return store;
    }

    [SkippableFact]
    public async Task Handler_ReturnsEndpointBySlug()
    {
        var resolver = new FakeKeyResolver("test-key", "proj-1");
        var bunny = new FakeBunny { ResourceResponses = { ["endpoints.json"] = Endpoints(("load-player", "GET"), ("save-data", "POST")) } };
        var store = await StoreWithEndpointsAsync(bunny.ResourceResponses["endpoints.json"]);
        var handler = new EndpointSlugReadHandler(resolver, bunny, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointSlugReadHandler>.Instance);

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", "load-player"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("load-player", json.GetProperty("endpoint").GetProperty("slug").GetString());
    }

    [SkippableFact]
    public async Task Handler_Returns404_WhenSlugNotFound()
    {
        var resolver = new FakeKeyResolver("test-key", "proj-1");
        var bunny = new FakeBunny { ResourceResponses = { ["endpoints.json"] = Endpoints(("save-data", "POST")) } };
        var store = await StoreWithEndpointsAsync(bunny.ResourceResponses["endpoints.json"]);
        var handler = new EndpointSlugReadHandler(resolver, bunny, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointSlugReadHandler>.Instance);

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", "missing-slug"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("ENDPOINT_NOT_FOUND", result.PublicErrorCode);
    }

    [SkippableFact]
    public async Task Handler_Returns401_WhenNoApiKey()
    {
        var resolver = new FakeKeyResolver(null, "proj-1");
        var bunny = new FakeBunny();
        var handler = new EndpointSlugReadHandler(resolver, bunny, new InMemoryNetworkStorageStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointSlugReadHandler>.Instance);

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", "load-player", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
    }

    [SkippableFact]
    public async Task Handler_CaseInsensitiveSlugMatch()
    {
        var resolver = new FakeKeyResolver("test-key", "proj-1");
        var bunny = new FakeBunny { ResourceResponses = { ["endpoints.json"] = Endpoints(("Load-Player", "GET")) } };
        var store = await StoreWithEndpointsAsync(bunny.ResourceResponses["endpoints.json"]);
        var handler = new EndpointSlugReadHandler(resolver, bunny, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointSlugReadHandler>.Instance);

        var result = await handler.ExecuteAsync(BuildRequest("proj-1", "load-player"));

        Assert.Equal(200, result.StatusCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("Load-Player", json.GetProperty("endpoint").GetProperty("slug").GetString());
    }

    [SkippableFact]
    public async Task LiveRoute_WithInvalidApiKey_ReturnsNativeUnauthorized_NoBunProxy()
    {
        using var liveClient = _factory.WithWebHostBuilder(builder =>
        {
            // A dead Bun storage-api port: a 502 here would prove the request proxied
            // to Bun. It must not — native auth rejects the bad key first.
            
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver("test-key", "proj-1"));
                services.RemoveAll<IWorkspaceStore>();
                services.AddScoped<IWorkspaceStore>(_ => new FakeBunny
                {
                    ResourceResponses = { ["endpoints.json"] = Endpoints(("get-join", "GET")) }
                });
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/v3/endpoints/proj-1/get-join?apiKey=wrong-key");

        // Authenticated GET endpoint calls are now served by the .NET native executor
        // (serve-endpoint-execution-dotnet-native): no Bun proxy in the request path.
        // An invalid key is rejected by native auth with a 401 — never a Bun 502 on
        // the dead 127.0.0.1:4547 storage-api.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task LiveRoute_MissingApiKey_ReturnsNativeUnauthorizedWithoutStorageRead()
    {
        using var liveClient = _factory.WithWebHostBuilder(builder =>
        {
            
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkspaceStore>();
                services.AddScoped<IWorkspaceStore>(_ => new SlowBunny());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/v3/endpoints/proj-1/get-join");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string _projectId;
        public FakeKeyResolver(string? validKey, string projectId) { _validKey = validKey; _projectId = projectId; }
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken ct)
            => Task.FromResult(string.Equals(apiKey, _validKey) && string.Equals(projectId, _projectId)
                ? new StorageApiKeyAuthResult(42, projectId, true, "secret", null)
                : null);
    }

    private sealed class FakeBunny : IWorkspaceStore
    {
        public Dictionary<string, JsonElement> ResourceResponses { get; } = new();
        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
        {
            var key = System.IO.Path.GetFileName(resourcePath);
            if (ResourceResponses.TryGetValue(key, out var el))
                return Task.FromResult<T?>((T)(object)el.Clone());
            return Task.FromResult<T?>(default);
        }
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken) => Task.FromResult<T?>(default);
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);
        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken) => Task.FromResult<WorkspaceProjectUsage?>(null);
        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SlowBunny : IWorkspaceStore
    {
        public async Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return default;
        }
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken) => Task.FromResult<T?>(default);
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);
        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken) => Task.FromResult<WorkspaceProjectUsage?>(null);
        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class EndpointSlugReadCandidateTests_Sqlite(SqliteHostFactory factory) : EndpointSlugReadCandidateTests<SqliteHostFactory>(factory);

public sealed class EndpointSlugReadCandidateTests_Postgres(PostgresHostFactory factory) : EndpointSlugReadCandidateTests<PostgresHostFactory>(factory);
