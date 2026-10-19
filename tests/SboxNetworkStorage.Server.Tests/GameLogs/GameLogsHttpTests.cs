using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests.GameLogs;

/// <summary>
/// HTTP behaviour of <c>/v3/game-logs/{projectId}</c> (epic #111 L1/L2): default-off invisibility, the
/// public-key actor rules, secret-key actor handling, search access and settings persistence.
/// </summary>
public sealed class GameLogsHttpTests
{
    private const string Player = "76561198000000001";
    private const string Victim = "76561198000000099";
    private const string Route = "/v3/game-logs";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<(SqliteHostFactory Factory, SelfHostProject Project)> EnabledProjectAsync(
        Dictionary<string, string>? extra = null)
    {
        var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync(requireSboxAuth: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var owner = NetworkStorageServices.LocalOwnerUserId;
        await projects.UpdateProjectSettingsAsync(owner, project.ProjectId, "security", new()
        {
            ["requireSboxAuth"] = "true",
            ["enableAuthSessions"] = "true",
        }, Ct);

        var form = new Dictionary<string, string>
        {
            ["gameLogsEnabled"] = "on",
            ["gameLogs_default_enabled"] = "on",
            ["gameLogs_audit_enabled"] = "on",
        };
        foreach (var (key, value) in extra ?? new()) form[key] = value;
        await projects.UpdateProjectSettingsAsync(owner, project.ProjectId, "game-logs", form, Ct);
        // Keys made by default carry no logs scope; the helper hands out a key that holds logs:r only.
        var (_, logKey) = await projects.CreateProjectKeyAsync(owner, project.ProjectId, "Log reader", "secret",
            permissions: new Dictionary<string, string> { ["logs"] = "r" }, Ct);
        return (factory, project with { SecretKey = logKey });
    }

    private static HttpRequestMessage Ingest(SelfHostProject project, string key, string json,
        string? onBehalfOf = null, string? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Route}/{project.ProjectId}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", key);
        if (onBehalfOf is not null) request.Headers.Add("x-on-behalf-of", onBehalfOf);
        if (session is not null) request.Headers.Add("x-auth-session", session);
        return request;
    }

    private static HttpRequestMessage Search(SelfHostProject project, string query = "", string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Route}/{project.ProjectId}{query}");
        request.Headers.Add("x-api-key", key ?? project.SecretKey);
        return request;
    }

    /// <summary>The writer flushes asynchronously; poll the secret-key search until the rows are visible.</summary>
    private static async Task<JsonElement> WaitForEntriesAsync(HttpClient client, SelfHostProject project, string query, int expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.SendAsync(Search(project, query));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
            if (body.GetProperty("entries").GetArrayLength() >= expected) return body;
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"game-log search '{query}' never returned {expected} entries");
    }

    [Fact]
    public async Task Disabled_logs_answer_exactly_like_an_unknown_route()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        using var unknown = new HttpRequestMessage(HttpMethod.Get, "/v3/no-such-route");
        unknown.Headers.Add("x-api-key", project.SecretKey);
        using var expected = await client.SendAsync(unknown);
        var expectedBody = await expected.Content.ReadAsStringAsync(Ct);

        using var ingest = await client.SendAsync(Ingest(project, project.SecretKey, "[{\"event\":\"match.end\"}]"));
        using var search = await client.SendAsync(Search(project));
        using var publicIngest = await client.SendAsync(Ingest(project, project.PublicKey, "[{\"event\":\"client.join\"}]"));
        using var unknownProject = await client.SendAsync(Search(project with { ProjectId = "no-such-project" }));

        foreach (var response in new[] { ingest, search, publicIngest, unknownProject })
        {
            Assert.Equal(expected.StatusCode, response.StatusCode);
            Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Api_path_ingests_and_searches_like_the_v3_path()
    {
        var (factory, project) = await EnabledProjectAsync();
        using var factoryScope = factory;
        using var client = factory.CreateClient();

        using var post = new HttpRequestMessage(HttpMethod.Post, $"/api/game-logs/{project.ProjectId}")
        {
            Content = new StringContent("[{\"event\":\"api.probe\"}]", System.Text.Encoding.UTF8, "application/json"),
        };
        post.Headers.Add("x-api-key", project.SecretKey);
        using var accepted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var entries = 0;
        for (var attempt = 0; attempt < 100 && entries == 0; attempt++)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/game-logs/{project.ProjectId}?event=api.probe");
            get.Headers.Add("x-api-key", project.SecretKey);
            using var response = await client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            entries = body.GetProperty("entries").GetArrayLength();
            if (entries == 0) await Task.Delay(100, Ct);
        }

        Assert.Equal(1, entries);
    }

    [Fact]
    public async Task Public_key_actor_field_is_rejected_and_the_stored_actor_is_the_session_player()
    {
        var (factory, project) = await EnabledProjectAsync();
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<INetworkStorageAuthSessionService>()
            .Create(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, Player, 3600).Token!;

        using var response = await client.SendAsync(Ingest(project, project.PublicKey,
            $"[{{\"event\":\"client.join\",\"actor\":\"{Victim}\"}},{{\"event\":\"client.join\",\"message\":\"hi\"}}]",
            session: token));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        var rejected = Assert.Single(body.RootElement.GetProperty("rejected").EnumerateArray());
        Assert.Equal(0, rejected.GetProperty("index").GetInt32());
        Assert.Equal("ACTOR_NOT_PERMITTED", rejected.GetProperty("code").GetString());

        var stored = await WaitForEntriesAsync(client, project, "?event=client.join", 1);
        var entry = Assert.Single(stored.GetProperty("entries").EnumerateArray());
        Assert.Equal(Player, entry.GetProperty("actor").GetString());
        Assert.Equal("hi", entry.GetProperty("message").GetString());

        var forgedTarget = await client.SendAsync(Search(project, $"?player={Victim}"));
        using var forgedBody = JsonDocument.Parse(await forgedTarget.Content.ReadAsStringAsync(Ct));
        Assert.Empty(forgedBody.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Public_key_on_behalf_of_header_is_forbidden()
    {
        var (factory, project) = await EnabledProjectAsync();
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<INetworkStorageAuthSessionService>()
            .Create(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, Player, 3600).Token!;

        using var response = await client.SendAsync(Ingest(project, project.PublicKey,
            "[{\"event\":\"client.join\"}]", onBehalfOf: Victim, session: token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ACTOR_NOT_PERMITTED", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Secret_key_honours_body_actor_and_on_behalf_of_player()
    {
        var (factory, project) = await EnabledProjectAsync();
        using var factoryScope = factory;
        using var client = factory.CreateClient();

        using var bodyActor = await client.SendAsync(Ingest(project, project.SecretKey,
            $"[{{\"event\":\"admin.kick\",\"actor\":\"{Victim}\",\"message\":\"by-body\"}}]"));
        using var headerActor = await client.SendAsync(Ingest(project, project.SecretKey,
            "[{\"event\":\"admin.mute\",\"message\":\"by-header\"}]", onBehalfOf: Player));
        Assert.Equal(HttpStatusCode.Accepted, bodyActor.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, headerActor.StatusCode);

        var kick = await WaitForEntriesAsync(client, project, "?event=admin.kick", 1);
        Assert.Equal(Victim, Assert.Single(kick.GetProperty("entries").EnumerateArray()).GetProperty("actor").GetString());

        var mute = await WaitForEntriesAsync(client, project, "?event=admin.mute", 1);
        Assert.Equal(Player, Assert.Single(mute.GetProperty("entries").EnumerateArray()).GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Search_requires_a_secret_key_with_logs_read_access()
    {
        var (factory, project) = await EnabledProjectAsync();
        using var factoryScope = factory;
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Search(project, "", project.PublicKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("FORBIDDEN", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Default_secret_key_without_logs_scope_cannot_search()
    {
        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync(requireSboxAuth: true);
        var form = new Dictionary<string, string> { ["gameLogsEnabled"] = "on", ["gameLogs_default_enabled"] = "on" };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
                .UpdateProjectSettingsAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "game-logs", form, Ct);
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Search(project));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("FORBIDDEN", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Owner_game_log_settings_persist_through_the_project_store()
    {
        var (factory, project) = await EnabledProjectAsync(new Dictionary<string, string>
        {
            ["gameLogs_chat_enabled"] = "on",
            ["gameLogs_default_retention"] = "45",
            ["gameLogs_default_sampling"] = "0.5",
            ["gameLogs_chat_retention"] = "7",
            ["gameLogsAllowedEvents"] = "match.*, scoreboard.update",
            ["gameLogsRateLimitPerMinute"] = "600",
        });
        using var factoryScope = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, Ct);

        var settings = access!.Project.GameLogs!;
        Assert.True(settings.Enabled);
        Assert.Equal(45, settings.Streams!["default"].RetentionDays);
        Assert.Equal(0.5, settings.Streams["default"].Sampling);
        Assert.True(settings.Streams["chat"].Enabled);
        Assert.Equal(7, settings.Streams["chat"].RetentionDays);
        Assert.Equal(["match.*", "scoreboard.update"], settings.AllowedEvents);
        Assert.Equal(600, settings.RateLimitPerMinute);
    }
}
