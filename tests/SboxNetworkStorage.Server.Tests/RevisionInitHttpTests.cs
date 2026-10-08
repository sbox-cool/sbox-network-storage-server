using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// POST /v3/manage/{projectId}/revision-init answers the game-client startup
/// handshake (<c>sbox-cool/sbox-network-storage</c> NetworkStorageRevisionInit):
/// public key accepted, client revision compared against the synced game
/// package, outdated flag set when the client trails.
/// </summary>
public abstract class RevisionInitHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected RevisionInitHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task CurrentClientIsAcknowledgedAsCurrent()
    {
        using var setup = await CreateWithPackageAsync(currentRevision: 41);
        using var response = await PostInitAsync(setup, clientRevision: 42);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(41, body.RootElement.GetProperty("currentRevisionId").GetInt64());
        Assert.False(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
    }

    [SkippableFact]
    public async Task TrailingClientIsFlaggedOutdated()
    {
        using var setup = await CreateWithPackageAsync(currentRevision: 41);
        using var response = await PostInitAsync(setup, clientRevision: 40);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
    }

    [SkippableFact]
    public async Task MissingPackageStillAcknowledgesHandshake()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}",
            new { projectId = project.ProjectId, clientType = "game", revisionId = 7 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("currentRevisionId").ValueKind);
        Assert.False(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
    }

    [SkippableFact]
    public async Task MissingKeyIsRejected()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/v3/manage/{project.ProjectId}/revision-init", new { revisionId = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<Setup> CreateWithPackageAsync(long currentRevision)
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        // Package sync requires the secret key; the game handshake below uses
        // the public key.
        using var authed = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync");
        authed.Headers.Add("x-api-key", project.SecretKey);
        authed.Content = JsonContent.Create(new
        {
            packageIdent = "test.game",
            currentRevisionId = currentRevision,
            isPublishedGameBundle = false,
        });
        using var syncResponse = await client.SendAsync(authed);
        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);
        return new Setup(factory.CreateClient(), project);
    }

    private static async Task<HttpResponseMessage> PostInitAsync(Setup setup, long clientRevision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/manage/{setup.Project.ProjectId}/revision-init?apiKey={setup.Project.PublicKey}");
        request.Headers.Add("x-public-key", setup.Project.PublicKey);
        request.Headers.Add("x-ns-revision-id", clientRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("x-ns-client-type", "game");
        request.Content = JsonContent.Create(new
        {
            projectId = setup.Project.ProjectId,
            clientType = "game",
            networkStorageVersion = "1.0",
            isPublishedGameBundle = true,
            packageIdent = "test.game",
            revisionId = clientRevision,
        });
        return await setup.Client.SendAsync(request);
    }

    private sealed record Setup(HttpClient Client, SelfHostProject Project) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
        }
    }
}

public sealed class SqliteRevisionInitHttpTests(SqliteHostFactory factory)
    : RevisionInitHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresRevisionInitHttpTests(PostgresHostFactory factory)
    : RevisionInitHttpTests<PostgresHostFactory>(factory);
