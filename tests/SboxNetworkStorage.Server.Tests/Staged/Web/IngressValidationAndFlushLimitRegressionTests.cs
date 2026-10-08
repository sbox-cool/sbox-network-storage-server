using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Direct document ingress validation + endpoint flush size regressions:
/// malformed collection/key identifiers are client input (404, never a backend
/// 500), and an over-limit endpoint-persisted record is rejected 413 pre-commit
/// with zero mutation — without capping the whole endpoint request at the
/// single-record size. Runs against both relational drivers.
/// </summary>
public abstract class IngressValidationAndFlushLimitRegressionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";
    private const string Collection = "players";

    private const string SaveProfileDefinition =
        """{"steps":[{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"playerName","value":"{{input.playerName}}"}]}],"response":{"status":200,"body":{"ok":true}}}""";

    private const string SaveTwoDefinition =
        """{"steps":[{"id":"a","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"blobA","value":"{{input.blobA}}"}]},{"id":"b","type":"write","collection":"players","key":"{{steamId}}_backup","ops":[{"op":"set","path":"blobB","value":"{{input.blobB}}"}]}],"response":{"status":200,"body":{"ok":true}}}""";

    private readonly SelfHostFactory _factory;

    protected IngressValidationAndFlushLimitRegressionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task MalformedCollection_Get_ReturnsNotFoundNeverStorageError()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/..%2F..%2Fetc/{SteamId}?apiKey={project.SecretKey}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task EncodedSlashKey_Get_ReturnsNotFoundNeverStorageError()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/a%2Fb?apiKey={project.SecretKey}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetString());
        Assert.Equal("Key not found.", body.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task MalformedIds_PostAndDelete_ReturnNotFoundWithoutMutation()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        var validPath = $"/v3/storage/{project.ProjectId}/{Collection}/{SteamId}?apiKey={project.SecretKey}";

        using var seeded = await client.PostAsJsonAsync(validPath, new { playerName = "original" });
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        using var badPost = await client.PostAsJsonAsync(
            $"/v3/storage/{project.ProjectId}/..%2F..%2Fetc/{SteamId}?apiKey={project.SecretKey}",
            new { playerName = "evil" });
        Assert.Equal(HttpStatusCode.NotFound, badPost.StatusCode);

        using var badDelete = await client.DeleteAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/a%2Fb?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.NotFound, badDelete.StatusCode);

        using var read = await client.GetAsync(validPath);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("original", body.GetProperty("playerName").GetString());
    }

    [SkippableFact]
    public async Task LegitimateKeyChars_RoundTripUnchanged()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        const string key = "player:1-2_3";
        var path = $"/v3/storage/{project.ProjectId}/{Collection}/{key}?apiKey={project.SecretKey}";

        using var saved = await client.PostAsJsonAsync(path, new { score = 7 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var loaded = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        var body = await loaded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, body.GetProperty("score").GetInt32());
    }

    [SkippableFact]
    public async Task EndpointOversizedRecord_ReturnsPayloadTooLargeWithZeroMutation()
    {
        var project = await _factory.CreateProjectAsync();
        await SeedEndpointAsync(project.ProjectId, "save-profile", SaveProfileDefinition);
        using var client = _factory.CreateClient();
        var directPath = $"/v3/storage/{project.ProjectId}/{Collection}/{SteamId}?apiKey={project.SecretKey}";

        using var seeded = await client.PostAsJsonAsync(directPath, new { playerName = "original" });
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        using var oversized = await PostEndpointAsync(client, project, "save-profile",
            new { playerName = new string('x', 1_200_000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var error = await oversized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PAYLOAD_TOO_LARGE", error.GetProperty("error").GetProperty("code").GetString());

        using var read = await client.GetAsync(directPath);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("original", body.GetProperty("playerName").GetString());
    }

    [SkippableFact]
    public async Task EndpointRecordSize_ExactLimitAcceptedOneByteOverRejected()
    {
        var project = await _factory.CreateProjectAsync();
        await SeedEndpointAsync(project.ProjectId, "save-profile", SaveProfileDefinition);
        using var client = _factory.CreateClient();
        var maxBytes = await GetMaxPayloadBytesAsync();
        var overhead = Encoding.UTF8.GetByteCount("{\"playerName\":\"\"}");

        using var accepted = await PostEndpointAsync(client, project, "save-profile",
            new { playerName = new string('x', maxBytes - overhead) });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var rejected = await PostEndpointAsync(client, project, "save-profile",
            new { playerName = new string('x', maxBytes - overhead + 1) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PAYLOAD_TOO_LARGE", error.GetProperty("error").GetProperty("code").GetString());

        using var read = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/{SteamId}?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(maxBytes - overhead, body.GetProperty("playerName").GetString()!.Length);
    }

    [SkippableFact]
    public async Task EndpointMultiRecordBatch_TotalOverLimitEachUnderLimit_Accepted()
    {
        var project = await _factory.CreateProjectAsync();
        await SeedEndpointAsync(project.ProjectId, "save-two", SaveTwoDefinition);
        using var client = _factory.CreateClient();
        var maxBytes = await GetMaxPayloadBytesAsync();
        var blob = new string('y', maxBytes / 2);

        using var response = await PostEndpointAsync(client, project, "save-two",
            new { blobA = blob, blobB = blob });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var key in new[] { SteamId, SteamId + "_backup" })
        {
            using var read = await client.GetAsync(
                $"/v3/storage/{project.ProjectId}/{Collection}/{key}?apiKey={project.SecretKey}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> PostEndpointAsync(
        HttpClient client, SelfHostProject project, string slug, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{project.ProjectId}/{slug}?apiKey={project.SecretKey}");
        request.Headers.Add("x-steam-id", SteamId);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private async Task SeedEndpointAsync(string projectId, string slug, string definitionJson)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        using var definition = JsonDocument.Parse(definitionJson);
        await store.UpsertEndpointAsync(projectId, slug, slug, "POST", enabled: true,
            definition.RootElement.Clone(), versionHash: null, version: 1, CancellationToken.None);
    }

    private async Task<int> GetMaxPayloadBytesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().MaxPayloadBytes;
    }
}

public sealed class IngressValidationAndFlushLimitRegressionTests_Sqlite(SqliteHostFactory factory)
    : IngressValidationAndFlushLimitRegressionTests<SqliteHostFactory>(factory);

public sealed class IngressValidationAndFlushLimitRegressionTests_Postgres(PostgresHostFactory factory)
    : IngressValidationAndFlushLimitRegressionTests<PostgresHostFactory>(factory);
