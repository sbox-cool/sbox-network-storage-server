using SboxNetworkStorage.Server.Tests.Hosting;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// End-to-end HTTP round-trip tests for the Network Storage record CRUD API
/// (<c>/api/storage/{projectId}/{collectionId}/{key}</c>). Exercises the full
/// ASP.NET Core pipeline: routing, auth, data plane, serialization.
///
/// All tests run against an in-memory <see cref="InMemoryNetworkStorageStore"/> — no
/// ScyllaDB instance required.
/// </summary>
public abstract class NetworkStorageApiRoundTripTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ApiKey = "sk-test-roundtrip-key";
    private const string ProjectId = "roundtrip-project";
    private const string Collection = "players";
    private const long OwnerUserId = 42;

    private readonly SelfHostFactory _factory;

    protected NetworkStorageApiRoundTripTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private HttpClient CreateClient(InMemoryNetworkStorageStore store, string? overrideApiKey = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ =>
                    new FakeRecordKeyResolver(overrideApiKey ?? ApiKey, ProjectId));
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService, NoopPlayerAnalyticsService>();
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Url(string key, string? apiKey = null)
        => $"/api/storage/{ProjectId}/{Collection}/{key}?apiKey={apiKey ?? ApiKey}";

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ── POST + GET round-trip ─────────────────────────────────────────────

    [SkippableFact]
    public async Task PostThenGet_ReturnsExactPayload()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        var payload = new { hp = 100, name = "Alice", level = 5 };
        using var postResp = await client.PostAsJsonAsync(Url("player-1"), payload);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        using var getResp = await client.GetAsync(Url("player-1"));
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, body.GetProperty("hp").GetInt32());
        Assert.Equal("Alice", body.GetProperty("name").GetString());
        Assert.Equal(5, body.GetProperty("level").GetInt32());
    }

    [SkippableFact]
    public async Task Compressed_Get_Meters_Actual_Transferred_Response_Bytes()
    {
        var store = new InMemoryNetworkStorageStore();
        using var scopedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "roundtrip-usage-compression-test-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeRecordKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService, NoopPlayerAnalyticsService>();
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tracker = scopedFactory.Services.GetRequiredService<NetworkStorageUsageTracker>();
        var month = DateTimeOffset.UtcNow.ToString("yyyy-MM");

        using var post = await client.PostAsJsonAsync(Url("compressed-player"), new
        {
            payload = new string('a', 32 * 1024),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        await tracker.FlushAsync(force: true, CancellationToken.None);
        var before = await store.ReadProjectUsageMonthlyAsync(ProjectId, month, CancellationToken.None);
        var beforeBytesOut = before!.Value.GetProperty("bytes_out").GetInt64();

        using var request = new HttpRequestMessage(HttpMethod.Get, Url("compressed-player"));
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        var transferredBody = await response.Content.ReadAsByteArrayAsync();

        await tracker.FlushAsync(force: true, CancellationToken.None);
        var after = await store.ReadProjectUsageMonthlyAsync(ProjectId, month, CancellationToken.None);
        Assert.Equal(
            transferredBody.Length,
            after!.Value.GetProperty("bytes_out").GetInt64() - beforeBytesOut);
        Assert.True(transferredBody.Length < 1024);
    }

    [SkippableFact]
    public async Task PostOverwrite_GetReturnsLatestValue()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("player-2"), new { hp = 100 });
        await client.PostAsJsonAsync(Url("player-2"), new { hp = 200 });

        using var getResp = await client.GetAsync(Url("player-2"));
        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200, body.GetProperty("hp").GetInt32());
    }

    // ── GET missing record ────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetMissing_Returns404()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var getResp = await client.GetAsync(Url("nonexistent"));
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetString());
    }

    // ── DELETE + GET ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeleteThenGet_Returns404()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("player-3"), new { hp = 50 });
        using var delResp = await client.DeleteAsync(Url("player-3"));
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        using var getResp = await client.GetAsync(Url("player-3"));
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [SkippableFact]
    public async Task DeleteMissing_Returns200_Idempotent()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Delete on absent key is still 200 (data plane is idempotent).
        using var delResp = await client.DeleteAsync(Url("ghost"));
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
    }

    // ── Auth: missing / bad / disabled key ────────────────────────────────

    [SkippableFact]
    public async Task GetWithoutApiKey_Returns401()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var resp = await client.GetAsync($"/api/storage/{ProjectId}/{Collection}/k");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostWithWrongApiKey_Returns401()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var resp = await client.PostAsJsonAsync(Url("k", apiKey: "sk-wrong-key"), new { x = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task DeleteWithWrongApiKey_Returns401()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var resp = await client.DeleteAsync(Url("k", apiKey: "sk-wrong-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostWithDisabledKey_Returns403()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store, overrideApiKey: "sk-disabled-key");

        // The FakeRecordKeyResolver doesn't support disabled keys, so wire a
        // custom resolver that returns enabled=false.
        using var httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new DisabledKeyResolver());
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService, NoopPlayerAnalyticsService>();
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resp = await httpClient.PostAsJsonAsync(
            $"/api/storage/{ProjectId}/{Collection}/k?apiKey=sk-disabled-key",
            new { x = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── POST with invalid body ────────────────────────────────────────────

    [SkippableFact]
    public async Task PostWithNullBody_Returns400()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var resp = await client.PostAsync(Url("k"),
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostWithMalformedJson_Returns400()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        using var resp = await client.PostAsync(Url("k"),
            new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Multiple keys in same collection ──────────────────────────────────

    [SkippableFact]
    public async Task MultipleKeys_IndependentLifecycle()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("a"), new { v = 1 });
        await client.PostAsJsonAsync(Url("b"), new { v = 2 });
        await client.PostAsJsonAsync(Url("c"), new { v = 3 });

        // Delete one
        await client.DeleteAsync(Url("b"));

        using var a = await client.GetAsync(Url("a"));
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);

        using var b = await client.GetAsync(Url("b"));
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);

        using var c = await client.GetAsync(Url("c"));
        Assert.Equal(HttpStatusCode.OK, c.StatusCode);
        var cBody = await c.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, cBody.GetProperty("v").GetInt32());
    }

    // ── Large payload ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PostLargePayload_RoundTrips()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // ~8 KB payload: a realistic game save blob.
        var bigArray = Enumerable.Range(0, 200).Select(i => new { id = i, data = $"item-{i}-" + new string('x', 30) }).ToArray();
        using var postResp = await client.PostAsJsonAsync(Url("bigsave"), new { items = bigArray, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        using var getResp = await client.GetAsync(Url("bigsave"));
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200, body.GetProperty("items").GetArrayLength());
    }

    // ── Content-type consistency ──────────────────────────────────────────

    [SkippableFact]
    public async Task GetRecord_ReturnsJsonContentType()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        await client.PostAsJsonAsync(Url("ct-test"), new { ok = true });
        using var getResp = await client.GetAsync(Url("ct-test"));

        Assert.Equal("application/json", getResp.Content.Headers.ContentType?.MediaType);
    }

    // ── Nested / complex JSON shapes ──────────────────────────────────────

    [SkippableFact]
    public async Task PostNestedJson_PreservesShape()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        var nested = new
        {
            player = new { name = "Bob", stats = new { hp = 100, mp = 50 } },
            inventory = new[] { "sword", "shield", "potion" },
            metadata = new Dictionary<string, object> { ["questId"] = 42, ["completed"] = true }
        };
        await client.PostAsJsonAsync(Url("nested"), nested);

        using var getResp = await client.GetAsync(Url("nested"));
        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Bob", body.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal(50, body.GetProperty("player").GetProperty("stats").GetProperty("mp").GetInt32());
        Assert.Equal(3, body.GetProperty("inventory").GetArrayLength());
        Assert.Equal("shield", body.GetProperty("inventory")[1].GetString());
        Assert.True(body.GetProperty("metadata").GetProperty("completed").GetBoolean());
    }

    // ── API key via header ────────────────────────────────────────────────

    [SkippableFact]
    public async Task ApiKey_ViaXApiKeyHeader_Works()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/storage/{ProjectId}/{Collection}/hdr");
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey);

        using var getResp = await client.SendAsync(req);
        // 404 because the record doesn't exist, but the key was accepted (not 401).
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    // ── Concurrent writes ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task ConcurrentWrites_LastWriterWins()
    {
        var store = new InMemoryNetworkStorageStore();
        using var client = CreateClient(store);

        // Fire 20 concurrent writes to the same key.
        var tasks = Enumerable.Range(0, 20).Select(i =>
            client.PostAsJsonAsync(Url("contested"), new { writeId = i })).ToArray();

        await Task.WhenAll(tasks);
        foreach (var t in tasks) Assert.Equal(HttpStatusCode.OK, t.Result.StatusCode);

        // The record should exist with one of the written values.
        using var getResp = await client.GetAsync(Url("contested"));
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var body = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        var writeId = body.GetProperty("writeId").GetInt32();
        Assert.InRange(writeId, 0, 19);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeRecordKeyResolver(string validKey, string projectId) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult(
                string.Equals(apiKey, validKey, StringComparison.Ordinal) && string.Equals(project, projectId, StringComparison.Ordinal)
                    ? new StorageApiKeyAuthResult(OwnerUserId, project, true, "secret")
                    : null);
    }

    private sealed class DisabledKeyResolver : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult<StorageApiKeyAuthResult?>(new StorageApiKeyAuthResult(OwnerUserId, project, Enabled: false, "public"));
    }

    private sealed class NoopPlayerAnalyticsService : IPlayerAnalyticsService
    {
        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordEndpointEventAsync(string projectId, string steamId, string endpointSlug, string eventType, IReadOnlyDictionary<string, object>? payload, IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class NetworkStorageApiRoundTripTests_Sqlite(SqliteHostFactory factory) : NetworkStorageApiRoundTripTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageApiRoundTripTests_Postgres(PostgresHostFactory factory) : NetworkStorageApiRoundTripTests<PostgresHostFactory>(factory);
