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
/// <c>/api/{x}</c> and <c>/v3/{x}</c> return identical status, headers and bodies: the same handler,
/// auth, body limits, rate-limit partition, library policy and error capture serve both roots, while
/// the request log keeps the original path.
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
        var apiPath = NetworkStorageRoots.Api + v3Path.Substring(NetworkStorageRoots.V3.Length);
        // HttpContent is single-use: snapshot the bytes before either send.
        var bytes = content is null ? null : await content.ReadAsByteArrayAsync();
        var mediaType = content?.Headers.ContentType?.ToString();
        HttpContent? Fresh() => bytes is null ? null
            : new ByteArrayContent(bytes) { Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType!) } };
        var v3 = await SendAsync(client, method, v3Path, Fresh());
        var api = await SendAsync(client, method, apiPath, Fresh());
        Assert.Equal(v3.Status, api.Status);
        Assert.Equal(v3.Body, api.Body);
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
        Assert.Equal(["/v3"], root.GetProperty("aliases").EnumerateArray().Select(e => e.GetString()).ToArray());
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
        // /api/v3/... stays unrouted.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, "/api/v3/values/x")).Status);
    }
}

public sealed class ApiRootMirrorTests_Sqlite(SqliteHostFactory factory) : ApiRootMirrorTests<SqliteHostFactory>(factory);

public sealed class ApiRootMirrorTests_Postgres(PostgresHostFactory factory) : ApiRootMirrorTests<PostgresHostFactory>(factory);
