using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

public abstract class ResourceScopeSecurityRegressionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;
    private static readonly string[] Scopes =
        ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"];

    protected ResourceScopeSecurityRegressionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task RestrictedSecretReadsOnlyGrantedManagementCategoryAndCanValidateCredentials()
    {
        var project = await factory.CreateProjectAsync();
        var key = await CreateKeyAsync(project.ProjectId, new() { ["collections"] = "r" });
        using var client = factory.CreateClient();
        using var allowed = await SendAsync(client, "GET", project.ProjectId, "collections", key);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        foreach (var category in new[] { "endpoints", "queries", "workflows", "game-values", "rate-limit-rules", "settings", "config", "tests", "game-package", "agent-manifest" })
        {
            using var denied = await SendAsync(client, "GET", project.ProjectId, category, key);
            await AssertForbiddenAsync(denied);
        }
        using var validate = await SendAsync(client, "GET", project.ProjectId, "validate", key);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        using var document = JsonDocument.Parse(await validate.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("endpoints", out _));
        Assert.False(document.RootElement.TryGetProperty("collections", out _));
    }

    [SkippableFact]
    public async Task ReadOnlySecretCannotMutateAnyCategoryOrExecuteAutoTests()
    {
        var project = await factory.CreateProjectAsync();
        var key = await CreateKeyAsync(project.ProjectId, Scopes.ToDictionary(scope => scope, _ => "r"));
        using var client = factory.CreateClient();
        // Only routes registered in NetworkStorageGatewayEndpoints are asserted
        // here. PUT settings, PUT tests, POST source-upgrade/run-tests/
        // test-endpoint, and DELETE keys have no Gateway route and return
        // 404/501 independent of the key (parity gaps tracked by the HTTP
        // corpus, not scope bypasses). Package sync has its own string error
        // envelope and is asserted separately below.
        foreach (var (method, category, body) in new[]
        {
            ("PUT", "endpoints", "[{\"id\":\"forbidden\",\"slug\":\"forbidden\"}]"),
            ("POST", "queries", "[{\"id\":\"forbidden\",\"name\":\"forbidden\"}]"),
            ("PUT", "collections", "[{\"id\":\"forbidden\",\"name\":\"forbidden\"}]"),
            ("PUT", "workflows", "[{\"id\":\"forbidden\",\"name\":\"forbidden\"}]"),
            ("PUT", "game-values", "{\"items\":[]}"),
            ("PUT", "rate-limit-rules", "[]"),
            ("POST", "auto-test", "{}"),
            ("DELETE", "game-values", "{}"),
            ("PATCH", "collections", "{}"),
        })
        {
            using var response = await SendAsync(client, method, project.ProjectId, category, key, body);
            await AssertForbiddenAsync(response);
        }
        using var packageSync = await SendAsync(client, "POST", project.ProjectId, "package-sync", key, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, packageSync.StatusCode);
        Assert.Contains("FORBIDDEN", await packageSync.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        Assert.Empty(await store.ListEndpointsAsync(project.ProjectId, CancellationToken.None));
        Assert.Empty(await store.ListCollectionsAsync(project.ProjectId, CancellationToken.None));
        Assert.Empty(await store.ListWorkflowsAsync(project.ProjectId, CancellationToken.None));
        Assert.Empty(await store.ListQueriesAsync(project.ProjectId, CancellationToken.None));
        Assert.Null(await store.ReadGameValuesAsync(project.ProjectId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task SyncAndPreflightDenyEverySuppliedCategoryBeforeEarlierAllowedWrites()
    {
        var project = await factory.CreateProjectAsync();
        var key = await CreateKeyAsync(project.ProjectId, new() { ["endpoints"] = "rw" });
        using var client = factory.CreateClient();
        foreach (var deniedSection in new[]
        {
            "\"collections\":[{\"id\":\"private_data\",\"name\":\"private_data\"}]",
            "\"collections\":[]", "\"collections\":null", "\"workflows\":[]",
            "\"queries\":[]", "\"game-values\":{}", "\"rate-limit-rules\":[]", "\"settings\":{}",
        })
        {
            var body = "{\"endpoints\":[{\"id\":\"allowed_but_not_written\",\"slug\":\"allowed_but_not_written\",\"steps\":[]}]," + deniedSection + "}";
            using var sync = await SendAsync(client, "PUT", project.ProjectId, "sync", key, body);
            await AssertForbiddenAsync(sync);
            using var preflight = await SendAsync(client, "POST", project.ProjectId, "sync/preflight", key, body);
            await AssertForbiddenAsync(preflight);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        Assert.Empty(await store.ListEndpointsAsync(project.ProjectId, CancellationToken.None));
        using var allowed = await SendAsync(client, "PUT", project.ProjectId, "sync", key,
            "{\"endpoints\":[{\"id\":\"allowed\",\"slug\":\"allowed\",\"steps\":[]}]}" );
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(await store.ListEndpointsAsync(project.ProjectId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task AutoTestRequiresCrossCategoryExecutionRatherThanDefinitionReadOrWrite()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        foreach (var permissions in new[]
        {
            new Dictionary<string, string> { ["endpoints"] = "rw", ["queries"] = "rw", ["collections"] = "rw" },
            new Dictionary<string, string> { ["endpoints"] = "x", ["queries"] = "x" },
            new Dictionary<string, string> { ["endpoints"] = "x", ["collections"] = "x" },
        })
        {
            var deniedKey = await CreateKeyAsync(project.ProjectId, permissions);
            using var denied = await SendAsync(client, "POST", project.ProjectId, "auto-test", deniedKey, "{}");
            await AssertForbiddenAsync(denied);
        }
        var executionKey = await CreateKeyAsync(project.ProjectId,
            new() { ["endpoints"] = "x", ["queries"] = "x", ["collections"] = "x" });
        using var allowed = await SendAsync(client, "POST", project.ProjectId, "auto-test", executionKey, "{}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var document = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task ValuesPreservePublicDefinitionsAndHidePrivateAndDuplicateLegacyValuesOnBothPaths()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        const string collections = """
            [
              {"id":"public_values","name":"public_values","visibility":"PUBLIC",
               "constants":[{"id":"public_constant","name":"Public","entries":{"amount":17}}],
               "tables":[{"id":"public_table","name":"Public table","columns":[],"rows":[{"amount":19}]}]},
              {"id":"private_values","name":"private_values","visibility":"PRIVATE",
               "constants":[{"id":"private_constant","name":"Private","entries":{"secret":23}}],
               "tables":[{"id":"private_table","name":"Private table","columns":[],"rows":[{"secret":29}]}]}
            ]
            """;
        using var written = await SendAsync(client, "PUT", project.ProjectId, "collections", project.SecretKey, collections);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        const string legacy = """
            {"items":[
              {"id":"private_constant","type":"group","entries":{"secret":999}},
              {"id":"private_table","type":"table","columns":[],"rows":[{"secret":999}]},
              {"id":"linked_private","type":"group","_collection":"private_values","entries":{"secret":999}},
              {"id":"legacy_public","type":"group","entries":{"amount":31}}
            ]}
            """;
        using var legacyWritten = await SendAsync(client, "PUT", project.ProjectId, "game-values", project.SecretKey, legacy);
        Assert.Equal(HttpStatusCode.OK, legacyWritten.StatusCode);
        using var listed = await SendAsync(client, "GET", project.ProjectId, "collections", project.SecretKey);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedDocument = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Contains(listedDocument.RootElement.GetProperty("data").EnumerateArray(), collection =>
            collection.GetProperty("id").GetString() == "public_values"
            && collection.GetProperty("constants")[0].GetProperty("entries").GetProperty("amount").GetInt32() == 17);
        var unscoped = await CreateKeyAsync(project.ProjectId, new() { ["game_values"] = "r", ["collections"] = "x" });
        var scoped = await CreateKeyAsync(project.ProjectId, new() { ["collections"] = "r" });

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var persisted = await store.ReadCollectionAsync(project.ProjectId, "public_values", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(JsonValueKind.Object, persisted.Value.GetProperty("definition_json").ValueKind);
        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IStorageApiKeyResolver>();
        var handler = new GameValuesHandler(resolver, workspace, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesHandler>.Instance);
        foreach (var key in new[] { project.PublicKey, unscoped, scoped })
        {
            var result = await handler.ExecuteAsync(ValuesRequest(project.ProjectId, key));
            Assert.Equal(200, result.StatusCode);
            AssertValues(JsonSerializer.SerializeToElement(result.Body), includePrivate: key == scoped);
        }
        foreach (var key in new[] { project.PublicKey, unscoped, scoped })
        {
            using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={Uri.EscapeDataString(key)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var httpDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertValues(httpDocument.RootElement, includePrivate: key == scoped);
        }
    }

    [SkippableFact]
    public async Task StringEncodedCollectionDefinitionsExposeOnlyAuthorizedValues()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        foreach (var (id, visibility, valueId) in new[]
        {
            ("public_values", "public", "public_constant"),
            ("private_values", "private", "private_constant"),
        })
        {
            var definitionText = JsonSerializer.Serialize(new
            {
                id, name = id, visibility,
                constants = new[] { new { id = valueId, name = valueId, entries = new { amount = 41 } } },
            });
            await store.UpsertCollectionAsync(project.ProjectId, id, id, visibility,
                JsonSerializer.SerializeToElement(definitionText), 1, CancellationToken.None);
        }
        var persisted = await store.ReadCollectionAsync(project.ProjectId, "public_values", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(JsonValueKind.String, persisted.Value.GetProperty("definition_json").ValueKind);
        var handler = new GameValuesHandler(scope.ServiceProvider.GetRequiredService<IStorageApiKeyResolver>(), scope.ServiceProvider.GetRequiredService<IWorkspaceStore>(), store, Microsoft.Extensions.Logging.Abstractions.NullLogger<GameValuesHandler>.Instance);
        foreach (var key in new[] { project.PublicKey, project.SecretKey })
        {
            var result = await handler.ExecuteAsync(ValuesRequest(project.ProjectId, key));
            Assert.Equal(200, result.StatusCode);
            var groups = JsonSerializer.SerializeToElement(result.Body).GetProperty("groups");
            Assert.Equal(41, groups.GetProperty("public_constant").GetProperty("values").GetProperty("amount").GetInt32());
            Assert.Equal(key == project.SecretKey, groups.TryGetProperty("private_constant", out _));
        }
    }

    private static void AssertValues(JsonElement values, bool includePrivate)
    {
        var groups = values.GetProperty("groups");
        var tables = values.GetProperty("tables");
        Assert.Equal(17, groups.GetProperty("public_constant").GetProperty("values").GetProperty("amount").GetInt32());
        Assert.Equal(19, tables.GetProperty("public_table").GetProperty("rows")[0].GetProperty("amount").GetInt32());
        Assert.Equal(31, groups.GetProperty("legacy_public").GetProperty("values").GetProperty("amount").GetInt32());
        Assert.Equal(includePrivate, groups.TryGetProperty("private_constant", out var privateConstant));
        Assert.Equal(includePrivate, tables.TryGetProperty("private_table", out var privateTable));
        Assert.Equal(includePrivate, groups.TryGetProperty("linked_private", out _));
        if (includePrivate)
        {
            Assert.Equal(23, privateConstant.GetProperty("values").GetProperty("secret").GetInt32());
            Assert.Equal(29, privateTable.GetProperty("rows")[0].GetProperty("secret").GetInt32());
        }
    }

    private async Task<string> CreateKeyAsync(string projectId, Dictionary<string, string> permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var (_, key) = await service.CreateProjectKeyAsync(NetworkStorageServices.LocalOwnerUserId,
            projectId, "Regression key", "secret", permissions, CancellationToken.None);
        return key;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string projectId,
        string category, string key, string? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/v3/manage/{projectId}/{category}");
        request.Headers.Add("X-Api-Key", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FORBIDDEN", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static NetworkStorageRequest ValuesRequest(string projectId, string key) => new(
        NetworkStorageRouteClassifier.Classify("GET", $"/v3/values/{projectId}"),
        new Dictionary<string, string>(), ContentType: null, AuthSignals: new Dictionary<string, bool>(),
        Credentials: new NetworkStorageCredentials(key, null, null, null, null), Body: null,
        ResolvedOwnerUserId: null, CancellationToken: CancellationToken.None);
}

public sealed class SqliteResourceScopeSecurityRegressionTests(SqliteHostFactory factory)
    : ResourceScopeSecurityRegressionTests<SqliteHostFactory>(factory);

public sealed class PostgresResourceScopeSecurityRegressionTests(PostgresHostFactory factory)
    : ResourceScopeSecurityRegressionTests<PostgresHostFactory>(factory);
