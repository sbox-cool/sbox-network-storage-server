using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Store-backed global read projections: the append producer persists to
/// <c>INetworkStorageStore.global_records</c>, so GET list/record must serve those durable
/// rows (previously they read the obsolete workspace <c>{collection}/global</c> enumerator
/// and returned empty/404). Covers the append→list/get round trip, cursor pagination over
/// both timestamp shapes (ISO strings from legacy server, unix-ms from the native producer), and the
/// auth/visibility boundaries (disabled keys, cross-project isolation, shared store view).
/// </summary>
public abstract class NetworkStorageGlobalStoreReadRegressionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Collection = "leaderboard_global";

    private readonly TFactory _factory;

    protected NetworkStorageGlobalStoreReadRegressionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private async Task<SelfHostProject> CreateProjectWithGlobalCollectionAsync(
        string collectionId = Collection,
        string visibility = "public")
    {
        var project = await _factory.CreateProjectAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.SeedCollectionAsync(
            project.ProjectId, collectionId, collectionId, visibility, new { collectionType = "global" });
        return project;
    }

    private static async Task<JsonElement> AppendAsync(
        HttpClient client, string projectId, string collectionId, string apiKey,
        string steamId, object body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v3/storage/{projectId}/{collectionId}/append?apiKey={apiKey}");
        request.Headers.Add("x-steam-id", steamId);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task SeedGlobalRowAsync(
        INetworkStorageStore store, string projectId, string collectionId,
        string recordId, object payload)
        => store.UpsertGlobalRecordAsync(
            projectId, collectionId, recordId,
            JsonSerializer.SerializeToElement(payload), version: 1, CancellationToken.None);

    [SkippableFact]
    public async Task AppendThenListReturnsDurableRows()
    {
        var project = await CreateProjectWithGlobalCollectionAsync();
        using var client = _factory.CreateClient();
        await AppendAsync(client, project.ProjectId, Collection, project.SecretKey,
            "76561198000000001", new { score = 1200, playerName = "Parity Tester" });
        await AppendAsync(client, project.ProjectId, Collection, project.SecretKey,
            "76561198000000002", new { score = 900, playerName = "Second Player" });

        using var listed = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var records = body.GetProperty("records");
        Assert.Equal(2, records.GetArrayLength());

        // Both durable rows are present with their producer-stamped fields.
        var byName = records.EnumerateArray()
            .ToDictionary(r => r.GetProperty("playerName").GetString()!, r => r);
        Assert.Equal(1200, byName["Parity Tester"].GetProperty("score").GetInt32());
        Assert.Equal(900, byName["Second Player"].GetProperty("score").GetInt32());
        foreach (var record in records.EnumerateArray())
        {
            Assert.True(record.TryGetProperty("_id", out _));
            Assert.True(record.TryGetProperty("_timestamp", out _));
            Assert.True(record.TryGetProperty("_writerId", out _));
        }

        // Two rows under the default limit → page not full → null cursor.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cursor").ValueKind);
    }

    [SkippableFact]
    public async Task AppendThenSingleRecordReturnsDurableRow()
    {
        var project = await CreateProjectWithGlobalCollectionAsync();
        using var client = _factory.CreateClient();
        var appended = await AppendAsync(client, project.ProjectId, Collection, project.SecretKey,
            "76561198000000001", new { score = 1200, playerName = "Parity Tester" });
        var recordId = appended.GetProperty("record").GetProperty("_id").GetString()!;

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/record/{recordId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var record = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(recordId, record.GetProperty("_id").GetString());
        Assert.Equal(1200, record.GetProperty("score").GetInt32());
        Assert.Equal("Parity Tester", record.GetProperty("playerName").GetString());
        Assert.Equal("76561198000000001", record.GetProperty("_writerId").GetString());
    }

    [SkippableFact]
    public async Task MissingRecordReturnsNotFound()
    {
        var project = await CreateProjectWithGlobalCollectionAsync();
        using var client = _factory.CreateClient();

        using var fetched = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/record/0123456789abcdef0123456789abcdef?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.NotFound, fetched.StatusCode);
        var body = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task ListPaginatesWithMeaningfulCursor()
    {
        var project = await CreateProjectWithGlobalCollectionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await SeedGlobalRowAsync(store, project.ProjectId, Collection, "page-r1", new
        {
            _id = "page-r1",
            _timestamp = "2026-05-30T12:00:00.000Z",
            _writerId = "u1",
            score = 3,
        });
        await SeedGlobalRowAsync(store, project.ProjectId, Collection, "page-r2", new
        {
            _id = "page-r2",
            _timestamp = "2026-05-30T11:00:00.000Z",
            _writerId = "u2",
            score = 2,
        });
        await SeedGlobalRowAsync(store, project.ProjectId, Collection, "page-r3", new
        {
            _id = "page-r3",
            _timestamp = "2026-05-30T10:00:00.000Z",
            _writerId = "u3",
            score = 1,
        });

        using var client = _factory.CreateClient();
        using var first = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}&limit=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var page = firstBody.GetProperty("records");
        Assert.Equal(2, page.GetArrayLength());
        Assert.Equal("page-r1", page[0].GetProperty("_id").GetString());
        Assert.Equal("page-r2", page[1].GetProperty("_id").GetString());

        // Full page → cursor is the last row's raw _timestamp.
        var cursor = firstBody.GetProperty("cursor").GetString();
        Assert.Equal("2026-05-30T11:00:00.000Z", cursor);

        using var second = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}&limit=2&after={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var remainder = secondBody.GetProperty("records");
        Assert.Equal("page-r3", Assert.Single(remainder.EnumerateArray()).GetProperty("_id").GetString());
    }

    [SkippableFact]
    public async Task ListSortsNativeProducerUnixMsTimestamps()
    {
        // The native append producer stamps _timestamp as unix milliseconds (a JSON
        // number), not an ISO string. List must order those rows newest-first and still
        // produce a usable cursor.
        var project = await CreateProjectWithGlobalCollectionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await SeedGlobalRowAsync(store, project.ProjectId, Collection, "ms-old", new
        {
            _id = "ms-old",
            _timestamp = 1780000000000L,
            _writerId = "u1",
        });
        await SeedGlobalRowAsync(store, project.ProjectId, Collection, "ms-new", new
        {
            _id = "ms-new",
            _timestamp = 1780000009000L,
            _writerId = "u2",
        });

        using var client = _factory.CreateClient();
        using var listed = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}&limit=1");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var page = body.GetProperty("records");
        Assert.Equal("ms-new", Assert.Single(page.EnumerateArray()).GetProperty("_id").GetString());
        Assert.Equal(1780000009000L, body.GetProperty("cursor").GetInt64());
    }

    [SkippableFact]
    public async Task DisabledKeyCannotList()
    {
        var project = await CreateProjectWithGlobalCollectionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var owner = NetworkStorageServices.LocalOwnerUserId;
        // Toggle before first use: the key resolver caches per key, so the disabled
        // state must be stored before anything resolves this key.
        await projects.ToggleProjectKeyAsync(owner, project.ProjectId, project.PublicKey, CancellationToken.None);

        using var client = _factory.CreateClient();
        using var listed = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task PrivateVisibilityCollectionReadsFromSharedStore()
    {
        // The store is the single visibility: reads must not default to a private view
        // that hides producer rows. A private-visibility global collection still lists.
        var project = await CreateProjectWithGlobalCollectionAsync(visibility: "private");
        using var client = _factory.CreateClient();
        await AppendAsync(client, project.ProjectId, Collection, project.SecretKey,
            "76561198000000001", new { score = 7 });

        using var listed = await client.GetAsync(
            $"/v3/storage/{project.ProjectId}/{Collection}/list?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, Assert.Single(body.GetProperty("records").EnumerateArray())
            .GetProperty("score").GetInt32());
    }

    [SkippableFact]
    public async Task CrossProjectKeyCannotList()
    {
        var projectA = await CreateProjectWithGlobalCollectionAsync();
        var projectB = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        await AppendAsync(client, projectA.ProjectId, Collection, projectA.SecretKey,
            "76561198000000001", new { score = 1 });

        using var listed = await client.GetAsync(
            $"/v3/storage/{projectA.ProjectId}/{Collection}/list?apiKey={projectB.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, listed.StatusCode);
    }
}

public sealed class NetworkStorageGlobalStoreReadRegressionTests_Sqlite(SqliteHostFactory factory)
    : NetworkStorageGlobalStoreReadRegressionTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageGlobalStoreReadRegressionTests_Postgres(PostgresHostFactory factory)
    : NetworkStorageGlobalStoreReadRegressionTests<PostgresHostFactory>(factory);
