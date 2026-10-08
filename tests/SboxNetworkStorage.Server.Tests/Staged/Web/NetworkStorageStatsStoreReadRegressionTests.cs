using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Store-backed stats reads: <c>NativeStatsHeartbeatHandler</c> persists through
/// <c>INetworkStorageDataPlane</c> (collection <c>player-stats</c>), so GET stats must serve
/// that same record (previously it read the obsolete workspace
/// <c>player-stats/{steamId}.json</c> file and returned 404). Covers the heartbeat→stats
/// round trip with the stored contract intact, the absent-data 404 (never zero-filled),
/// and the auth boundaries (disabled keys, cross-project isolation).
/// </summary>
public abstract class NetworkStorageStatsStoreReadRegressionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";

    private readonly TFactory _factory;

    protected NetworkStorageStatsStoreReadRegressionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private static async Task<JsonElement> HeartbeatAsync(
        HttpClient client, string projectId, string apiKey, string steamId, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/v3/storage/{projectId}/stats/heartbeat?apiKey={apiKey}",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object HeartbeatBody(string steamId) => new
    {
        steamId,
        sessionId = "parity-session-1",
        sessionSeconds = 120,
        @event = "heartbeat",
        playerName = "Parity Tester",
        source = "network-storage-library",
    };

    [SkippableFact]
    public async Task HeartbeatThenStatsReturnsStoredStats()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        var heartbeat = await HeartbeatAsync(
            client, project.ProjectId, project.PublicKey, SteamId, HeartbeatBody(SteamId));
        Assert.True(heartbeat.GetProperty("persisted").GetBoolean());

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/stats/{SteamId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var stats = await fetched.Content.ReadFromJsonAsync<JsonElement>();

        // The stored contract the heartbeat wrote, read back verbatim.
        Assert.Equal(SteamId, stats.GetProperty("steamId").GetString());
        Assert.Equal(2, stats.GetProperty("totalSeconds").GetInt64());
        Assert.True(stats.TryGetProperty("lastHeartbeat", out _));
        Assert.True(stats.TryGetProperty("firstSeen", out _));
    }

    [SkippableFact]
    public async Task SecondHeartbeatAccumulatesOnSameStoredRow()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        await HeartbeatAsync(client, project.ProjectId, project.PublicKey, SteamId, HeartbeatBody(SteamId));
        await HeartbeatAsync(client, project.ProjectId, project.PublicKey, SteamId, HeartbeatBody(SteamId));

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/stats/{SteamId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var stats = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SteamId, stats.GetProperty("steamId").GetString());
        Assert.True(stats.GetProperty("totalSeconds").GetInt64() >= 2);
    }

    [SkippableFact]
    public async Task SecretKeyReadsSameStoredRow()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        await HeartbeatAsync(client, project.ProjectId, project.PublicKey, SteamId, HeartbeatBody(SteamId));

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/stats/{SteamId}?apiKey={project.SecretKey}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var stats = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SteamId, stats.GetProperty("steamId").GetString());
    }

    [SkippableFact]
    public async Task MissingStatsReturnsNotFoundInsteadOfZeros()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/stats/76561198000009999?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.NotFound, fetched.StatusCode);
        var body = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task DisabledKeyCannotReadStats()
    {
        var project = await _factory.CreateProjectAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var owner = NetworkStorageServices.LocalOwnerUserId;
        // Toggle before first use: the key resolver caches per key, so the disabled
        // state must be stored before anything resolves this key.
        await projects.ToggleProjectKeyAsync(owner, project.ProjectId, project.PublicKey, CancellationToken.None);

        using var client = _factory.CreateClient();
        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/stats/{SteamId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, fetched.StatusCode);
        var body = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task CrossProjectKeyCannotReadStats()
    {
        var projectA = await _factory.CreateProjectAsync();
        var projectB = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        await HeartbeatAsync(client, projectA.ProjectId, projectA.PublicKey, SteamId, HeartbeatBody(SteamId));

        using var fetched = await client.GetAsync(
            $"/v3/storage/{projectA.ProjectId}/stats/{SteamId}?apiKey={projectB.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, fetched.StatusCode);
    }
}

public sealed class NetworkStorageStatsStoreReadRegressionTests_Sqlite(SqliteHostFactory factory)
    : NetworkStorageStatsStoreReadRegressionTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageStatsStoreReadRegressionTests_Postgres(PostgresHostFactory factory)
    : NetworkStorageStatsStoreReadRegressionTests<PostgresHostFactory>(factory);
