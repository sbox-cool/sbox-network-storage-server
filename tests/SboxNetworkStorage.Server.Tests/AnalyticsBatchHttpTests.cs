using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

// POST /v3/storage/{pid}/analytics/events over real HTTP (and the /api/storage alias). A body without an
// array-valued `events` is one event and keeps its exact response. A `{"events":[ ... ]}` body is the opt-in
// batch: one auth pass, per-item validation, and partial success.
public abstract class AnalyticsBatchHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SingleOk = "{\"ok\":true,\"stored\":true,\"reason\":null}";

    private readonly TFactory _factory;

    protected AnalyticsBatchHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task SingleEventBodiesKeepTheirExistingResponses()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        const string steamId = "76561198000000101";

        using var stored = await PostEventsAsync(client, EventsPath(project), new { steamId, type = "custom.single_ok" }, project.PublicKey);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(SingleOk, await stored.Content.ReadAsStringAsync());

        // An `events` property that is not an array keeps the body on the single path.
        using var notArray = await PostEventsAsync(client, EventsPath(project),
            new { steamId, type = "custom.single_events_string", events = "nope" }, project.PublicKey);
        Assert.Equal(HttpStatusCode.OK, notArray.StatusCode);
        Assert.Equal(SingleOk, await notArray.Content.ReadAsStringAsync());

        using var invalid = await PostEventsAsync(client, EventsPath(project),
            new { steamId, severity = "bogus", type = "custom.single_bad" }, project.PublicKey);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "error", "detail" }, error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("INVALID_ANALYTICS_EVENT", error.GetProperty("error").GetString());

        Assert.Equal(new[] { "custom.single_events_string", "custom.single_ok" }, await StoredEventTypesAsync(project, steamId));
    }

    [SkippableTheory]
    [InlineData("/v3/storage", "76561198000000102")]
    [InlineData("/api/storage", "76561198000000103")]
    public async Task BatchStoresEveryValidItemInInputOrderOnBothStorageRoots(string root, string steamId)
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();

        using var response = await PostEventsAsync(client, EventsPath(project, root), new
        {
            events = new object[]
            {
                new { steamId, type = "custom.batch_a" },
                new { steamId, type = "custom.batch_b", severity = "info" },
                new { steamId, type = "custom.batch_c" },
            },
        }, project.PublicKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(3, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetInt32());
        var results = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, results.Select(r => r.GetProperty("index").GetInt32()).ToArray());
        Assert.All(results, r =>
        {
            Assert.Equal(new[] { "index", "ok" }, r.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.True(r.GetProperty("ok").GetBoolean());
        });

        Assert.Equal(new[] { "custom.batch_a", "custom.batch_b", "custom.batch_c" }, await StoredEventTypesAsync(project, steamId));
    }

    [SkippableFact]
    public async Task BatchRejectsEachInvalidItemAloneAndStoresTheRest()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        const string steamId = "76561198000000111";
        var oversizedContext = new { blob = new string('x', 33_000) }; // over the 32 KiB per-event limit

        using var response = await PostEventsAsync(client, EventsPath(project), new
        {
            events = new object[]
            {
                new { steamId, type = "custom.partial_ok_a" },
                new { steamId = "not-numeric", type = "custom.partial_bad_key" },
                "not an object",
                new { steamId, severity = "bogus", type = "custom.partial_bad_severity" },
                new { steamId },
                new { steamId, type = "custom.partial_too_big", context = oversizedContext },
                new { steamId, type = "custom.partial_ok_b" },
            },
        }, project.PublicKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(5, body.GetProperty("rejected").GetInt32());

        var results = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, results.Select(r => r.GetProperty("index").GetInt32()).ToArray());
        Assert.Equal(
            new string?[] { null, "INVALID_KEY", "INVALID_JSON", "INVALID_ANALYTICS_EVENT", "INVALID_ANALYTICS_EVENT", "PAYLOAD_TOO_LARGE", null },
            results.Select(r => r.TryGetProperty("error", out var code) ? code.GetString() : null).ToArray());
        foreach (var rejected in results.Where(r => !r.GetProperty("ok").GetBoolean()))
        {
            Assert.False(string.IsNullOrEmpty(rejected.GetProperty("message").GetString()));
        }

        Assert.Equal(new[] { "custom.partial_ok_a", "custom.partial_ok_b" }, await StoredEventTypesAsync(project, steamId));
    }

    [SkippableFact]
    public async Task BatchEnvelopeLimitsRefuseTheWholeBatchBeforeAnythingIsStored()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        const string steamId = "76561198000000121";
        var path = EventsPath(project);

        using var empty = await PostEventsAsync(client, path, new { events = Array.Empty<object>() }, project.PublicKey);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("INVALID_JSON", (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // 51 valid items: the count limit applies even though every item would be accepted.
        var tooMany = Enumerable.Range(0, 51).Select(i => (object)new { steamId, type = $"custom.many_{i}" }).ToArray();
        using var many = await PostEventsAsync(client, path, new { events = tooMany }, project.PublicKey);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, many.StatusCode);
        Assert.Equal("PAYLOAD_TOO_LARGE", (await many.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // Nine items of about 29.6 KB: each is under the 32 KiB per-event limit, but the batch is over 256 KiB.
        // The wire limit for the data plane (server.limits.data_plane, 256 KiB) rejects it before the handler runs.
        var blob = new string('x', 29_500);
        var heavy = Enumerable.Range(0, 9).Select(i => (object)new { steamId, type = $"custom.heavy_{i}", context = new { blob } }).ToArray();
        using var oversized = await PostEventsAsync(client, path, new { events = heavy }, project.PublicKey);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal("PAYLOAD_TOO_LARGE", (await oversized.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Empty(await StoredEventTypesAsync(project, steamId));

        // Fifty is the largest accepted batch.
        var fifty = Enumerable.Range(0, 50).Select(i => (object)new { steamId, type = $"custom.fifty_{i:D2}" }).ToArray();
        using var atLimit = await PostEventsAsync(client, path, new { events = fifty }, project.PublicKey);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(50, (await atLimit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        Assert.Equal(50, (await StoredEventTypesAsync(project, steamId)).Length);
    }

    [SkippableFact]
    public async Task BatchIsRefusedBeforeAnyItemWhenTheKeyIsNotAccepted()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        const string steamId = "76561198000000131";
        var batch = new { events = new object[] { new { steamId, type = "custom.unauth_a" }, new { steamId, type = "custom.unauth_b" } } };

        using var missing = await PostEventsAsync(client, EventsPath(project), batch);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = await PostEventsAsync(client, EventsPath(project), batch, "not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("UNAUTHORIZED", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Empty(await StoredEventTypesAsync(project, steamId));
    }

    [SkippableFact]
    public async Task ServerInfoAdvertisesAnalyticsBatchNextToAnalytics()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/server-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var capabilities = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("analytics", capabilities);
        Assert.Contains("analytics-batch", capabilities);
    }

    private static string EventsPath(SelfHostProject project, string root = "/v3/storage")
        => $"{root}/{project.ProjectId}/analytics/events";

    private static async Task<HttpResponseMessage> PostEventsAsync(HttpClient client, string path, object body, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        return await client.SendAsync(request);
    }

    private async Task<string[]> StoredEventTypesAsync(SelfHostProject project, string steamId)
    {
        await _factory.Services.GetRequiredService<AnalyticsWriterService>().FlushAsync(CancellationToken.None);
        await using var scope = _factory.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>()
            .ListPlayerEventsAsync(project.ProjectId, steamId, 0, long.MaxValue, 100, CancellationToken.None);
        return events.Select(e => e.GetProperty("event_type").GetString()!).OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }
}

public sealed class SqliteAnalyticsBatchHttpTests(SqliteHostFactory factory)
    : AnalyticsBatchHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresAnalyticsBatchHttpTests(PostgresHostFactory factory)
    : AnalyticsBatchHttpTests<PostgresHostFactory>(factory);
