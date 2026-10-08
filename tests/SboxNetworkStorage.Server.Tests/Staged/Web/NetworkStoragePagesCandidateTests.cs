using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStoragePagesCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory _factory;
    private readonly HttpClient _client;

    protected NetworkStoragePagesCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
        _client = factory.WithWebHostBuilder(builder =>
        {
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageCandidateRequest BuildRequest(
        string projectId,
        string pageSlug,
        string? format = null,
        NetworkStorageCredentials? credentials = null)
    {
        var uri = format is not null
            ? $"/api/pages/{projectId}/{pageSlug}?format={format}"
            : $"/api/pages/{projectId}/{pageSlug}";
        var route = NetworkStorageRouteClassifier.Classify("GET", uri);
        return new NetworkStorageCandidateRequest(
            route,
            format is not null
                ? new Dictionary<string, string> { ["format"] = format }
                : new Dictionary<string, string>(),
            ContentType: null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: credentials ?? NetworkStorageCredentials.None,
            Body: null,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    [SkippableFact]
    public async Task PublishedPageReturnsBunCompatibleJsonPayload()
    {
        var pageFixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "page-sample.json"));
        var pageIndex = /*lang=json*/ """{"userId": 42, "projectId": "demo-project"}""";

        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, pageFixture));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome", format: "json"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);
        Assert.Contains("network-storage/page-index/demo-project.json", result.StoragePathsRead);
        Assert.Contains("network-storage/users/42/demo-project/pages/welcome.json", result.StoragePathsRead);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("welcome", json.GetProperty("slug").GetString());
        Assert.Equal("Welcome to My Project", json.GetProperty("title").GetString());
        Assert.Equal("markdown", json.GetProperty("type").GetString());
        Assert.Equal("published", json.GetProperty("statusLevel").GetString());
        Assert.Equal("2026-06-02T10:00:00.000Z", json.GetProperty("updatedAt").GetString());
        Assert.Equal("# Welcome\n\nThis is a sample page for testing.", json.GetProperty("markdown").GetString());
        Assert.Equal("", json.GetProperty("html").GetString());  // empty string for format=json
    }

    [SkippableFact]
    public async Task PublishedPageReturnsBunCompatibleJsonmdPayload()
    {
        var pageFixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "page-sample.json"));
        var pageIndex = /*lang=json*/ """{"userId": 42, "projectId": "demo-project"}""";

        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, pageFixture));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome", format: "jsonmd"));

        Assert.Equal(200, result.StatusCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("welcome", json.GetProperty("slug").GetString());
        Assert.Equal("Welcome to My Project", json.GetProperty("title").GetString());
        Assert.Equal("markdown", json.GetProperty("type").GetString());
        Assert.Equal("published", json.GetProperty("statusLevel").GetString());
        Assert.Equal("2026-06-02T10:00:00.000Z", json.GetProperty("updatedAt").GetString());
        Assert.Equal("# Welcome\n\nThis is a sample page for testing.", json.GetProperty("markdown").GetString());
        // jsonmd format does NOT include the html field
        Assert.False(json.TryGetProperty("html", out _));
    }

    [SkippableFact]
    public async Task StringTypedUserIdResolvesPagePath()
    {
        var pageFixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "page-sample.json"));
        // Production page-index documents store userId as a JSON string (older writers), not a number.
        // Regression: GetInt64() previously threw InvalidOperationException on these documents.
        var pageIndex = /*lang=json*/ """{"userId": "42", "projectId": "demo-project"}""";

        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, pageFixture));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome", format: "json"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Contains("network-storage/users/42/demo-project/pages/welcome.json", result.StoragePathsRead);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("welcome", json.GetProperty("slug").GetString());
        Assert.Equal("Welcome to My Project", json.GetProperty("title").GetString());
    }

    [SkippableFact]
    public async Task MissingUserIdInPageIndexReturnsNotFound()
    {
        var pageIndex = /*lang=json*/ """{"projectId": "demo-project"}""";
        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, "{}"));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("Project not found.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task MissingPageIndexReturnsNotFoundCode()
    {
        var handler = new PagesCandidateHandler(new FakeBunny(null, null));

        var result = await handler.ExecuteAsync(BuildRequest("nonexistent", "page"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Project not found.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task MissingPageReturnsNotFoundCode()
    {
        // page-index exists, but page content does not
        var pageIndex = /*lang=json*/ """{"userId": 42, "projectId": "demo-project"}""";

        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, null));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "nonexistent-page"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("NOT_FOUND", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Page not found.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task PageIndexStoreErrorReturnsReadFailedCode()
    {
        var handler = new PagesCandidateHandler(new FakeBunny(null, null, throwsOnPageIndex: true));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("PAGE_READ_FAILED", result.PublicErrorCode);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("error", json.GetProperty("source").GetString());
    }

    [SkippableFact]
    public async Task PageContentStoreErrorReturnsReadFailedCode()
    {
        var pageIndex = /*lang=json*/ """{"userId": 42, "projectId": "demo-project"}""";
        var handler = new PagesCandidateHandler(new FakeBunny(pageIndex, null, throwsOnPageContent: true));

        var result = await handler.ExecuteAsync(BuildRequest("demo-project", "welcome"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("PAGE_READ_FAILED", result.PublicErrorCode);
    }

    [SkippableFact]
    public async Task PageIndexPathIsUrlEncoded()
    {
        var handler = new PagesCandidateHandler(new FakeBunny(null, null));

        var result = await handler.ExecuteAsync(BuildRequest("space project", "slug"));

        Assert.Contains("network-storage/page-index/space%20project.json", result.StoragePathsRead);
    }

    [SkippableFact]
    public async Task LiveRoute_Pages_IsServedNativelyWithoutGateway()
    {
        var pageFixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-shadow", "page-sample.json"));
        var pageIndex = /*lang=json*/ """{"userId": 42, "projectId": "demo-project"}""";

        using var liveClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBunnyWorkspaceClient>();
                services.AddScoped<IBunnyWorkspaceClient>(_ => new FakeBunny(pageIndex, pageFixture));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/pages/demo-project/welcome?format=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("welcome", root.GetProperty("slug").GetString());
        Assert.Equal("Welcome to My Project", root.GetProperty("title").GetString());
        Assert.Equal("markdown", root.GetProperty("type").GetString());
    }

    private sealed class FakeBunny : IBunnyWorkspaceClient
    {
        private readonly string? _pageIndexJson;
        private readonly string? _pageContentJson;
        private readonly bool _throwsOnPageIndex;
        private readonly bool _throwsOnPageContent;

        public FakeBunny(
            string? pageIndexJson = null,
            string? pageContentJson = null,
            bool throwsOnPageIndex = false,
            bool throwsOnPageContent = false)
        {
            _pageIndexJson = pageIndexJson;
            _pageContentJson = pageContentJson;
            _throwsOnPageIndex = throwsOnPageIndex;
            _throwsOnPageContent = throwsOnPageContent;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
        {
            if (absolutePath.Contains("/page-index/"))
            {
                if (_throwsOnPageIndex)
                {
                    throw new InvalidOperationException($"simulated read failure for {absolutePath}");
                }
                return DeserializeJson<T>(_pageIndexJson);
            }

            if (absolutePath.Contains("/pages/"))
            {
                if (_throwsOnPageContent)
                {
                    throw new InvalidOperationException($"simulated read failure for {absolutePath}");
                }
                return DeserializeJson<T>(_pageContentJson);
            }

            return Task.FromResult<T?>(default);
        }

        private static Task<T?> DeserializeJson<T>(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return Task.FromResult<T?>(default);
            }

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return Task.FromResult(JsonSerializer.Deserialize<T>(json, options));
        }

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BunnyProject>>([]);

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

public sealed class NetworkStoragePagesCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStoragePagesCandidateTests<SqliteHostFactory>(factory);

public sealed class NetworkStoragePagesCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStoragePagesCandidateTests<PostgresHostFactory>(factory);
