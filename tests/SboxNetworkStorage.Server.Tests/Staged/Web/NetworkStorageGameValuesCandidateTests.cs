using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageGameValuesCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory factory;
    private readonly HttpClient client;

    protected NetworkStorageGameValuesCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
        client = factory.WithWebHostBuilder(builder =>
        {
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageCandidateRequest BuildRequest(
        string projectId,
        string? apiKey = "sbox_sk_testpublickey")
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/values/{projectId}");
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

    // ── Success: valid public key → 200 + client-format body ──

    [SkippableFact]
    public async Task ValidPublicKeyReturnsOkWithClientFormatPayload()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "game-values-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixture);
        var gv = fixtureDoc.RootElement.GetProperty("gameValues").Clone();
        var collections = fixtureDoc.RootElement.GetProperty("collections").Clone();
        var expected = fixtureDoc.RootElement.GetProperty("expectedClientFormat");
        var handler = new GameValuesCandidateHandler(
            new FakeKeyResolver("test-public-key", "demo-project", "public"),
            new FakeBunnyGameValues(gv, collections, "demo-project"),
            new InMemoryNetworkStorageStore(),
            Options.Create(new ScyllaDbOptions()),
            (Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesCandidateHandler>.Instance));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: "test-public-key"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("public", result.AuthDecision);
        Assert.Contains(result.StoragePathsRead,
            p => p.EndsWith("game-values.json", StringComparison.Ordinal));
        Assert.Contains(result.StoragePathsRead,
            p => p.EndsWith("collections.json", StringComparison.Ordinal));

        var json = JsonSerializer.SerializeToElement(result.Body);

        Assert.Equal(
            expected.GetProperty("version").GetString(),
            json.GetProperty("version").GetString());
        Assert.Equal(
            expected.GetProperty("updatedAt").GetString(),
            json.GetProperty("updatedAt").GetString());

        var groups = json.GetProperty("groups");
        Assert.Equal(
            expected.GetProperty("groups").GetProperty("speed").GetProperty("name").GetString(),
            groups.GetProperty("speed").GetProperty("name").GetString());
        Assert.Equal(
            expected.GetProperty("groups").GetProperty("speed").GetProperty("values").GetProperty("walk").GetInt32(),
            groups.GetProperty("speed").GetProperty("values").GetProperty("walk").GetInt32());
        Assert.Equal(
            expected.GetProperty("groups").GetProperty("economy").GetProperty("_collection").GetString(),
            groups.GetProperty("economy").GetProperty("_collection").GetString());
        // Collection constant takes precedence, so existing-group still appears
        Assert.True(groups.TryGetProperty("existing-group", out _));

        var tables = json.GetProperty("tables");
        Assert.Equal(
            expected.GetProperty("tables").GetProperty("weapons").GetProperty("name").GetString(),
            tables.GetProperty("weapons").GetProperty("name").GetString());
        Assert.Equal(
            expected.GetProperty("tables").GetProperty("weapons").GetProperty("columns").GetArrayLength(),
            tables.GetProperty("weapons").GetProperty("columns").GetArrayLength());
        Assert.Equal(
            expected.GetProperty("tables").GetProperty("weapons").GetProperty("rows").GetArrayLength(),
            tables.GetProperty("weapons").GetProperty("rows").GetArrayLength());
        Assert.Equal(
            expected.GetProperty("tables").GetProperty("levels").GetProperty("_collection").GetString(),
            tables.GetProperty("levels").GetProperty("_collection").GetString());
        Assert.Equal(
            expected.GetProperty("tables").GetProperty("levels").GetProperty("color").GetString(),
            tables.GetProperty("levels").GetProperty("color").GetString());
    }

    [SkippableFact]
    public async Task LiveRoute_V3Values_IsServedNativelyWithoutGateway()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "game-values-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixture);
        var gv = fixtureDoc.RootElement.GetProperty("gameValues").Clone();
        var collections = fixtureDoc.RootElement.GetProperty("collections").Clone();
        var expected = fixtureDoc.RootElement.GetProperty("expectedClientFormat");

        using var liveClient = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.Configure<ScyllaDbOptions>(o => o.Primary = false));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver("test-public-key", "demo-project", "public"));
                services.RemoveAll<IBunnyWorkspaceClient>();
                services.AddScoped<IBunnyWorkspaceClient>(_ => new FakeBunnyGameValues(gv, collections, "demo-project"));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/v3/values/demo-project?apiKey=test-public-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(expected.GetProperty("version").GetString(), root.GetProperty("version").GetString());
        Assert.True(root.TryGetProperty("groups", out _));
        Assert.True(root.TryGetProperty("tables", out _));
    }

    // ── Auth failure: missing key → 401 UNAUTHORIZED ──

    [SkippableFact]
    public async Task MissingApiKeyReturnsUnauthorized()
    {
        var handler = new GameValuesCandidateHandler(
            new FakeKeyResolver(null, "demo-project", null),
            new FakeBunnyGameValues(default, default, "demo-project"),
            new InMemoryNetworkStorageStore(),
            Options.Create(new ScyllaDbOptions()),
            (Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesCandidateHandler>.Instance));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: null));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKeyReturnsUnauthorized()
    {
        var handler = new GameValuesCandidateHandler(
            new FakeKeyResolver("bad-key", "demo-project", null),
            new FakeBunnyGameValues(default, default, "demo-project"),
            new InMemoryNetworkStorageStore(),
            Options.Create(new ScyllaDbOptions()),
            (Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesCandidateHandler>.Instance));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Project disabled → PROJECT_DISABLED ──

    [SkippableFact]
    public async Task DisabledProjectReturnsProjectDisabled()
    {
        var handler = new GameValuesCandidateHandler(
            new FakeKeyResolver("disabled-key", "disabled-project", "public"),
            new FakeBunnyGameValues(
                default, default, "disabled-project",
                projects: new List<BunnyProject>
                {
                    new(Id: "disabled-project", Name: "Disabled", Description: null, Enabled: false,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                }),
            new InMemoryNetworkStorageStore(),
            Options.Create(new ScyllaDbOptions()),
            (Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesCandidateHandler>.Instance));

        var result = await handler.ExecuteAsync(BuildRequest("disabled-project", apiKey: "disabled-key"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("PROJECT_DISABLED", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("PROJECT_DISABLED", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Store read failure → error ──

    [SkippableFact]
    public async Task StoreReadFailureReturnsError()
    {
        var handler = new GameValuesCandidateHandler(
            new FakeKeyResolver("readfail-key", "fail-project", "secret"),
            new FakeBunnyGameValues(
                default, default, "fail-project",
                projects: new List<BunnyProject>
                {
                    new(Id: "fail-project", Name: "Failer", Description: null, Enabled: true,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                },
                throwsOnRead: true),
            new InMemoryNetworkStorageStore(),
            Options.Create(new ScyllaDbOptions()),
            (Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesCandidateHandler>.Instance));

        var result = await handler.ExecuteAsync(BuildRequest("fail-project", apiKey: "readfail-key"));

        // When the storage read throws, the handler returns 500 with ENDPOINT_CONFIG_ERROR
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("ENDPOINT_CONFIG_ERROR", result.PublicErrorCode);
    }

    // ── Shadow endpoint integration test ──

    // ── Fakes ──

    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string? _validKey;
        private readonly string? _projectId;
        private readonly string? _keyType;

        public FakeKeyResolver(string? validKey, string? projectId, string? keyType)
        {
            _validKey = validKey;
            _projectId = projectId;
            _keyType = keyType;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
        {
            if (_validKey is null || _keyType is null)
            {
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            }

            // Match exact key or treat all keys as valid except null
            return Task.FromResult<StorageApiKeyAuthResult?>(
                new StorageApiKeyAuthResult(
                    UserId: 42,
                    ProjectId: projectId,
                    Enabled: true,
                    KeyType: _keyType));
        }
    }

    private sealed class FakeBunnyGameValues : IBunnyWorkspaceClient
    {
        private readonly JsonElement _gameValues;
        private readonly JsonElement _collections;
        private readonly string _projectId;
        private readonly IReadOnlyList<BunnyProject>? _projects;
        private readonly bool _throwsOnRead;

        public FakeBunnyGameValues(
            JsonElement gameValues,
            JsonElement collections,
            string projectId,
            IReadOnlyList<BunnyProject>? projects = null,
            bool throwsOnRead = false)
        {
            _gameValues = gameValues;
            _collections = collections;
            _projectId = projectId;
            _projects = projects;
            _throwsOnRead = throwsOnRead;
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (_throwsOnRead)
            {
                throw new InvalidOperationException($"simulated read failure for {resourcePath}");
            }

            object? result = resourcePath switch
            {
                "game-values.json" => _gameValues.ValueKind == JsonValueKind.Undefined ? null : _gameValues,
                "collections.json" => _collections.ValueKind == JsonValueKind.Undefined ? null : _collections,
                _ => null
            };

            return Task.FromResult((T?)result);
        }

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
        {
            if (_projects is not null)
                return Task.FromResult(_projects);

            // Default: return a project matching _projectId, enabled
            return Task.FromResult<IReadOnlyList<BunnyProject>>(
                new List<BunnyProject>
                {
                    new(Id: _projectId, Name: "Test Project", Description: null, Enabled: true,
                        CreatedAt: null, UpdatedAt: null, CompiledAt: null)
                });
        }

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

public sealed class NetworkStorageGameValuesCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageGameValuesCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageGameValuesCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageGameValuesCandidateTests<PostgresHostFactory>(factory);
