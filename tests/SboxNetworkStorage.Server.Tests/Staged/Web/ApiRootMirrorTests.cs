using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// HTTP regression coverage for canonical aliases and legacy storage collisions.
/// </summary>
public abstract class ApiRootMirrorTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected ApiRootMirrorTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertMirrorAsync(HttpClient client, HttpMethod method, string v3Path, HttpContent? content = null)
    {
        var bytes = content is null ? null : await content.ReadAsByteArrayAsync();
        var mediaType = content?.Headers.ContentType?.ToString();
        async Task<(HttpStatusCode Status, string Body, string[] Headers)> Capture(string path)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(ClientHeaders.LibraryVersion, "1.2.0");
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType!);
            }
            using var response = await client.SendAsync(request);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(h => h.Key.StartsWith("x-ns-", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => h.Key + ":" + string.Join(",", h.Value)).ToArray();
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), headers);
        }
        var expected = await Capture(v3Path);
        var suffix = v3Path[NetworkStorageRoots.V3.Length..];
        foreach (var root in new[] { NetworkStorageRoots.Api, NetworkStorageRoots.ApiV3 })
        {
            var path = root + suffix;
            var actual = await Capture(path);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Body, actual.Body);
            Assert.Equal(expected.Headers, actual.Headers);
        }
    }

    [SkippableFact]
    public async Task ServerInfoIsIdenticalAndAdvertisesTheApiRoot()
    {
        using var client = factory.CreateClient();
        var (status, body) = await SendAsync(client, HttpMethod.Get, "/api/server-info");
        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement.GetProperty("api");
        Assert.Equal("/api", root.GetProperty("root").GetString());
        Assert.Equal(["/api/v3", "/v3"], root.GetProperty("aliases").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(NetworkStorageRoots.ApiSince, root.GetProperty("since").GetString());
        Assert.Contains("api-root", json.RootElement.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["v3"], json.RootElement.GetProperty("apiVersions").EnumerateArray().Select(e => e.GetString()).ToArray());

        await AssertMirrorAsync(client, HttpMethod.Get, "/v3/server-info");
    }

    [SkippableFact]
    public async Task GameReadsWritesAndManagementAreIdenticalOnBothRoots()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var key = $"?apiKey={project.PublicKey}";

        await AssertMirrorAsync(client, HttpMethod.Get, $"/v3/values/{project.ProjectId}{key}");
        await AssertMirrorAsync(client, HttpMethod.Get, $"/v3/manage/{project.ProjectId}/validate?apiKey={project.SecretKey}");
        await AssertMirrorAsync(client, HttpMethod.Post, $"/v3/manage/{project.ProjectId}/revision-init{key}",
            JsonContent(new { projectId = project.ProjectId, revisionId = 42 }));
        await AssertMirrorAsync(client, HttpMethod.Get, "/v3/nothing-here");

        static StringContent JsonContent(object value)
            => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    [SkippableFact]
    public async Task RequestLogKeepsTheOriginalPath()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var key = $"?apiKey={project.PublicKey}&case=mirror-log";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/api/values/{project.ProjectId}{key}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, $"/v3/values/{project.ProjectId}{key}")).Status);
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        var log = await scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>()
            .ListStorageRequestLogAsync(project.ProjectId, 10, CancellationToken.None);
        var paths = log.Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Contains($"/api/values/{project.ProjectId}", paths);
        Assert.Contains($"/v3/values/{project.ProjectId}", paths);
    }

    [SkippableFact]
    public async Task LibraryPolicyRefusesIdenticallyOnBothRoots()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
                .UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientHeaders.LibraryVersion, "1.1.0");

        var v3 = await SendAsync(client, HttpMethod.Get, $"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        var api = await SendAsync(client, HttpMethod.Get, $"/api/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.UpgradeRequired, v3.Status);
        Assert.Equal(v3.Status, api.Status);
        Assert.Equal(v3.Body, api.Body);
        Assert.Contains("LIBRARY_UNSUPPORTED", api.Body);
    }

    [SkippableFact]
    public async Task OversizedBodiesAreRejectedIdenticallyOnBothRoots()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var big = new { projectId = project.ProjectId, revisionId = 1, pad = new string('x', 512 * 1024) };
        await AssertMirrorAsync(client, HttpMethod.Post, $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}",
            new StringContent(JsonSerializer.Serialize(big), Encoding.UTF8, "application/json"));
        var (status, _) = await SendAsync(client, HttpMethod.Post, $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}",
            new StringContent(JsonSerializer.Serialize(big), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
    }

    [SkippableFact]
    public async Task ReservedApiFamiliesKeepTheirOwnRoutes()
    {
        using var client = factory.CreateClient();
        // /api/storage without a project id is a native 404 on both roots.
        var v3Root = await SendAsync(client, HttpMethod.Get, "/v3/storage");
        var apiRoot = await SendAsync(client, HttpMethod.Get, "/api/storage");
        Assert.Equal(v3Root.Status, apiRoot.Status);
        Assert.Equal(HttpStatusCode.NotFound, apiRoot.Status);
        // Explicit /api/v3 uses the canonical handler rather than falling through.
        await AssertMirrorAsync(client, HttpMethod.Get, "/v3/values/x");
    }
    [SkippableTheory]
    [InlineData("scores", "list", null)]
    [InlineData("scores", "list", "1.1.0")]
    [InlineData("scores", "list", "1.2.0-beta.1")]
    [InlineData("scores", "list", "invalid")]
    [InlineData("scores", "list", "1.3.0-01")]
    [InlineData("stats", "76561198000000001", null)]
    [InlineData("stats", "76561198000000001", "1.1.0")]
    public async Task LegacyStorageDocumentNamesKeepTheirMeaning(string collection, string key, string? version)
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, collection, collection, "private",
            JsonSerializer.SerializeToElement(new { collectionType = "per-steamid", accessMode = "public", allowRecordDelete = true }),
            1, CancellationToken.None);
        using var client = factory.CreateClient();
        if (version is not null) client.DefaultRequestHeaders.Add(ClientHeaders.LibraryVersion, version);
        var path = $"/api/storage/{project.ProjectId}/{collection}/{key}?apiKey={project.PublicKey}";
        using var written = await client.PostAsync(path,
            new StringContent("{\"marker\":\"legacy-document\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var read = await SendAsync(client, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        using var document = JsonDocument.Parse(read.Body);
        Assert.Equal("legacy-document", document.RootElement.GetProperty("marker").GetString());
        using var deleted = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, path)).Status);
    }
    [SkippableTheory]
    [InlineData("1.2.0")]
    [InlineData("1.2.1")]
    [InlineData("2.0.0")]
    public async Task NewLibrariesUseUniformApiStorageWithoutChangingLegacyDocuments(string version)
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>();
        await store.UpsertRecordAsync(project.ProjectId, "scores", "list",
            JsonSerializer.SerializeToElement(new { marker = "legacy-document" }), false, 1, CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientHeaders.LibraryVersion, version);
        var result = await SendAsync(client, HttpMethod.Get, $"/api/storage/{project.ProjectId}/scores/list?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.OK, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("records").ValueKind);
        Assert.False(body.RootElement.TryGetProperty("marker", out _));
        using var legacyClient = factory.CreateClient();
        var legacy = await SendAsync(legacyClient, HttpMethod.Get, $"/api/storage/{project.ProjectId}/scores/list?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.OK, legacy.Status);
        using var document = JsonDocument.Parse(legacy.Body);
        Assert.Equal("legacy-document", document.RootElement.GetProperty("marker").GetString());
    }
}

public sealed class ApiRootMirrorTests_Sqlite(SqliteHostFactory factory) : ApiRootMirrorTests<SqliteHostFactory>(factory);

public sealed class ApiRootMirrorTests_Postgres(PostgresHostFactory factory) : ApiRootMirrorTests<PostgresHostFactory>(factory);
