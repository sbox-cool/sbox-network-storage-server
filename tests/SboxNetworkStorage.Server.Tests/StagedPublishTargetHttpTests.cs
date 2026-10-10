using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// "Push Staged" and next-revision play sessions (sbox-cool/sbox-network-storage Editor/SyncToolApi.cs,
/// Code/Core/NetworkStorageHttp.cs): management writes carrying <c>x-ns-publish-target: next</c> land in
/// the staged revision, <c>includeStaged=true</c> lists them marked <c>revisionTarget: "next"</c>, and only
/// runtime calls targeting next execute them until a package sync promotes them.
/// </summary>
public abstract class StagedPublishTargetHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000321";
    private readonly TFactory factory;

    protected StagedPublishTargetHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task StagedEndpointPushLeavesLiveUnchangedAndIsListedAsNext()
    {
        var (client, project) = await ProjectWithRevisionAsync(revision: 1);
        using var disposeClient = client;
        var p = project.ProjectId;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("live")))).Status);

        var staged = await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("next"), target: "next"));
        Assert.Equal(HttpStatusCode.OK, staged.Status);
        Assert.Equal("next", staged.Body.GetProperty("publishTarget").GetString());
        Assert.False(staged.Body.TryGetProperty("stagedFallback", out _));

        Assert.Equal("live", await LiveVersionAsync(p, "ping"));

        var listed = await ListAsync(client, project, "endpoints", "?includeStaged=true&revisionTarget=next");
        Assert.Equal(2, listed.Count);
        Assert.False(listed[0].TryGetProperty("revisionTarget", out _));
        Assert.Equal("live", Version(listed[0]));
        Assert.Equal("next", listed[1].GetProperty("revisionTarget").GetString());
        Assert.Equal("ping", listed[1].GetProperty("slug").GetString());
        Assert.Equal("next", Version(listed[1]));

        Assert.Single(await ListAsync(client, project, "endpoints", ""));
        Assert.Single(await ListAsync(client, project, "endpoints", "?includeStaged=true&revisionTarget=live"));

        // A staged PATCH merges over the staged copy, not the live one, and leaves live alone.
        var patched = await SendAsync(client, Manage(HttpMethod.Patch, p, "endpoints", project,
            """{"endpoint":{"slug":"ping","description":"staged edit"}}""", target: "next"));
        Assert.Equal(HttpStatusCode.OK, patched.Status);
        Assert.Equal("updated", patched.Body.GetProperty("action").GetString());
        Assert.Equal("next", patched.Body.GetProperty("publishTarget").GetString());
        var afterPatch = (await ListAsync(client, project, "endpoints", "?includeStaged=true"))[1];
        Assert.Equal("staged edit", afterPatch.GetProperty("description").GetString());
        Assert.Equal("next", Version(afterPatch));
        Assert.Equal("live", await LiveVersionAsync(p, "ping"));
    }

    [SkippableFact]
    public async Task RuntimeTargetingNextRunsStagedDefinitionsWhileLiveRunsLive()
    {
        var (client, project) = await ProjectWithRevisionAsync(revision: 1);
        using var disposeClient = client;
        var p = project.ProjectId;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("live")))).Status);

        // Staged: a new global collection and an endpoint writing to it, plus a new ping version.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "collections", project,
            """[{"name":"scores","collectionType":"global","schema":{"value":{"type":"number"}}}]""", target: "next"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("next"), target: "next"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project,
            """[{"slug":"submit","method":"POST","enabled":true,"steps":[{"id":"save","type":"write","collection":"scores","key":"board","ops":[{"op":"set","path":"value","value":7}]}],"response":{"status":200,"body":{"ok":true}}}]""",
            target: "next"))).Status);

        Assert.Equal("live", (await CallAsync(client, project, "ping", next: false)).Body.GetProperty("version").GetString());
        Assert.Equal("next", (await CallAsync(client, project, "ping", next: true)).Body.GetProperty("version").GetString());
        // The live call after a next call still sees live definitions (no shared cache entry).
        Assert.Equal("live", (await CallAsync(client, project, "ping", next: false)).Body.GetProperty("version").GetString());
        var tested = await SendAsync(client, Manage(HttpMethod.Post, p, "test-endpoint", project,
            """{"slug":"ping"}""", target: "next"));
        Assert.Equal("next", tested.Body.GetProperty("result").GetProperty("body").GetProperty("version").GetString());
        var autoTested = await SendAsync(client, Manage(HttpMethod.Post, p, "auto-test", project,
            """{"slug":"ping"}""", target: "next"));
        Assert.Equal("next", autoTested.Body.GetProperty("result").GetProperty("body").GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(client, project, "submit", next: false)).Status);

        var submitted = await CallAsync(client, project, "submit", next: true);
        Assert.Equal(HttpStatusCode.OK, submitted.Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        // The staged collection's collectionType routes the write to the global records table.
        Assert.NotNull(await store.ReadGlobalRecordAsync(p, "scores", "board", CancellationToken.None));
        Assert.Null(await store.ReadCollectionAsync(p, "scores", CancellationToken.None));
    }

    [SkippableFact]
    public async Task NextPushWithoutSyncedPackageGoesLiveAndReportsTheFallback()
    {
        var project = await factory.CreateProjectAsync("Staged fallback");
        using var client = factory.CreateClient();
        var p = project.ProjectId;

        var pushed = await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("next"), target: "next"));
        Assert.Equal(HttpStatusCode.OK, pushed.Status);
        Assert.Equal("live", pushed.Body.GetProperty("publishTarget").GetString());
        Assert.Equal("no synced game package", pushed.Body.GetProperty("stagedFallback").GetString());

        Assert.Equal("next", await LiveVersionAsync(p, "ping"));
        var listed = await ListAsync(client, project, "endpoints", "?includeStaged=true&revisionTarget=next");
        Assert.Single(listed);
        Assert.False(listed[0].TryGetProperty("revisionTarget", out _));
    }

    [SkippableFact]
    public async Task PackageSyncPromotesStagedPushesAndClearsThem()
    {
        var (client, project) = await ProjectWithRevisionAsync(revision: 1);
        using var disposeClient = client;
        var p = project.ProjectId;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("live")))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Manage(HttpMethod.Put, p, "endpoints", project, Ping("next"), target: "next"))).Status);
        Assert.Equal("live", (await CallAsync(client, project, "ping", next: false)).Body.GetProperty("version").GetString());

        using var synced = await PackageSyncAsync(client, project, revision: 2);
        var promotion = JsonDocument.Parse(await synced.Content.ReadAsStringAsync()).RootElement.GetProperty("promotedRevisionOverrides");
        Assert.True(promotion.GetProperty("promoted").GetBoolean());
        Assert.Equal(1, promotion.GetProperty("endpointCount").GetInt32());

        Assert.Equal("next", await LiveVersionAsync(p, "ping"));
        Assert.Equal("next", (await CallAsync(client, project, "ping", next: false)).Body.GetProperty("version").GetString());
        var listed = await ListAsync(client, project, "endpoints", "?includeStaged=true&revisionTarget=next");
        Assert.Single(listed);
        Assert.False(listed[0].TryGetProperty("revisionTarget", out _));
    }

    [SkippableFact]
    public async Task SyncPushTargetingNextStagesEndpointsAndCollectionsButNotWorkflows()
    {
        var (client, project) = await ProjectWithRevisionAsync(revision: 1);
        using var disposeClient = client;
        var p = project.ProjectId;
        var payload = "{\"endpoints\":" + Ping("next") + """
            ,"collections":[{"name":"inventory","collectionType":"per-steamid","schema":{"items":{"type":"array"}}}],
             "workflows":[{"id":"noop","name":"Noop","steps":[]}]}
            """;

        var preflight = await SendAsync(client, Manage(HttpMethod.Post, p, "sync/preflight", project, payload, target: "next"));
        Assert.Equal(HttpStatusCode.OK, preflight.Status);
        Assert.Equal("next", preflight.Body.GetProperty("publishTarget").GetString());

        var pushed = await SendAsync(client, Manage(HttpMethod.Put, p, "sync", project, payload, target: "next"));
        Assert.Equal(HttpStatusCode.OK, pushed.Status);
        Assert.True(pushed.Body.GetProperty("ok").GetBoolean());
        Assert.Equal("next", pushed.Body.GetProperty("publishTarget").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            Assert.Empty(await store.ListEndpointsAsync(p, CancellationToken.None));
            Assert.Empty(await store.ListCollectionsAsync(p, CancellationToken.None));
            Assert.NotNull(await store.ReadWorkflowAsync(p, "noop", CancellationToken.None));
        }

        var endpoints = await ListAsync(client, project, "endpoints", "?includeStaged=true&revisionTarget=next");
        Assert.Equal("ping", Assert.Single(endpoints).GetProperty("slug").GetString());
        Assert.Equal("next", endpoints[0].GetProperty("revisionTarget").GetString());
        var collections = await ListAsync(client, project, "collections", "?includeStaged=true&revisionTarget=next");
        Assert.Equal("inventory", Assert.Single(collections).GetProperty("name").GetString());
        Assert.Equal("next", collections[0].GetProperty("revisionTarget").GetString());
    }

    private static string Ping(string version) => JsonSerializer.Serialize(new[]
    {
        new { slug = "ping", method = "POST", enabled = true, steps = Array.Empty<object>(), response = new { status = 200, body = new { version } } },
    });

    private static string? Version(JsonElement endpoint)
        => endpoint.GetProperty("response").GetProperty("body").GetProperty("version").GetString();

    private async Task<(HttpClient Client, SelfHostProject Project)> ProjectWithRevisionAsync(long revision)
    {
        var project = await factory.CreateProjectAsync("Staged publish");
        var client = factory.CreateClient();
        using var synced = await PackageSyncAsync(client, project, revision);
        return (client, project);
    }

    private static async Task<HttpResponseMessage> PackageSyncAsync(HttpClient client, SelfHostProject project, long revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Content = JsonContent.Create(new { packageIdent = "test.game", currentRevisionId = revision, isPublishedGameBundle = false });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static HttpRequestMessage Manage(HttpMethod method, string projectId, string path, SelfHostProject project,
        string? body = null, string? target = null)
    {
        var request = new HttpRequestMessage(method, $"/v3/manage/{projectId}/{path}");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        if (target is not null) request.Headers.Add("x-ns-publish-target", target);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        using (var response = await client.SendAsync(request))
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(HttpClient client, SelfHostProject project, string resource, string query)
    {
        var (status, body) = await SendAsync(client, Manage(HttpMethod.Get, project.ProjectId, resource + query, project));
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("data").EnumerateArray().ToList();
    }

    // Matches the game runtime with PublishTarget=next: query revisionTarget/includeStaged plus the header.
    private static Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(HttpClient client, SelfHostProject project, string slug, bool next)
    {
        var url = $"/v3/endpoints/{project.ProjectId}/{slug}?apiKey={project.PublicKey}"
            + (next ? "&revisionTarget=next&includeStaged=true" : "");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("x-secret-key", project.SecretKey);
        request.Headers.Add("x-steam-id", SteamId);
        if (next) request.Headers.Add("x-ns-publish-target", "next");
        return SendAsync(client, request);
    }

    private async Task<string?> LiveVersionAsync(string projectId, string endpointId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var row = await store.ReadEndpointAsync(projectId, endpointId, CancellationToken.None);
        Assert.NotNull(row);
        var column = row.Value.GetProperty("definition_json");
        var definition = column.ValueKind == JsonValueKind.String ? JsonDocument.Parse(column.GetString()!).RootElement : column;
        return Version(definition);
    }
}

public sealed class SqliteStagedPublishTargetHttpTests(SqliteHostFactory factory)
    : StagedPublishTargetHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresStagedPublishTargetHttpTests(PostgresHostFactory factory)
    : StagedPublishTargetHttpTests<PostgresHostFactory>(factory);
