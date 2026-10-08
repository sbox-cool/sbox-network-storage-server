using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageStorageLedgerReadCandidateTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory _factory;

    protected NetworkStorageStorageLedgerReadCandidateTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task StoredTrackedDeltasAreReturnedByPublicAndDedicatedLedgerReads()
    {
        var project = await _factory.CreateProjectAsync();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        // Seed through the same projection contract used by PlayerAnalyticsIngester.
        await store.InsertLedgerEntryAsync(project.ProjectId, "players", "player1", 1,
            JsonSerializer.SerializeToElement(new { ts = "2026-05-15T10:30:00.000Z", field = "gold", source = "game", delta = 2.5 }), CancellationToken.None);
        await store.InsertLedgerEntryAsync(project.ProjectId, "players", "player1", 2,
            JsonSerializer.SerializeToElement(new { ts = "2026-05-15T10:31:00.000Z", field = "gold", source = "admin", delta = -0.5 }), CancellationToken.None);
        using var client = _factory.CreateClient();

        foreach (var key in new[] { project.PublicKey, project.SecretKey })
        {
            using var response = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/player1/ledger?apiKey={key}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("player1", body.GetProperty("steamId").GetString());
            Assert.Equal(2, body.GetProperty("entries").GetArrayLength());
            Assert.Contains(body.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("field").GetString() == "gold");
            var summary = body.GetProperty("summary");
            Assert.Equal(2.5, summary.GetProperty("totalIncrease").GetDouble());
            Assert.Equal(-0.5, summary.GetProperty("totalDecrease").GetDouble());
            Assert.Equal(2, summary.GetProperty("netChange").GetDouble());
            Assert.Equal(2, summary.GetProperty("entryCount").GetInt32());
            Assert.Equal(2.5, summary.GetProperty("sources").GetProperty("game").GetProperty("total").GetDouble());
        }
    }

    [SkippableFact]
    public async Task DirectlySavedRecordWithoutTrackedDeltasHasEmptyLedger()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        using var saved = await client.PostAsJsonAsync($"/v3/storage/{project.ProjectId}/players/player2?apiKey={project.SecretKey}", new { gold = 25 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var response = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/player2/ledger?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("entries").EnumerateArray());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("entryCount").GetInt32());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("netChange").GetDouble());
    }

    [SkippableFact]
    public async Task MissingRecordAndLedgerReturnNotFound()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/absent/ledger?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task MissingAndInvalidApiKeysReturnUnauthorized()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        foreach (var suffix in new[] { "", "?apiKey=invalid" })
        {
            using var response = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/player1/ledger{suffix}");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
        }
    }
}

public sealed class NetworkStorageStorageLedgerReadCandidateTests_Sqlite(SqliteHostFactory factory) : NetworkStorageStorageLedgerReadCandidateTests<SqliteHostFactory>(factory);
public sealed class NetworkStorageStorageLedgerReadCandidateTests_Postgres(PostgresHostFactory factory) : NetworkStorageStorageLedgerReadCandidateTests<PostgresHostFactory>(factory);
