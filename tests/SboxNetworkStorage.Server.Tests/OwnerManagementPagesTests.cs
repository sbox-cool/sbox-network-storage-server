using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Owner dashboard management pages: rate limit rules, webhooks, endpoint tests, versions, pages, settings.</summary>
public sealed class OwnerManagementPagesTests
{
    private const string SteamId = "76561198000000321";

    private static async Task<(SqliteHostFactory Factory, HttpClient Owner, SelfHostProject Project, string Url)> SetupAsync()
    {
        var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Owner pages");
        return (factory, owner, project, $"/dashboard/projects/{project.ProjectId}");
    }

    private static async Task<HttpResponseMessage> ManageAsync(SqliteHostFactory factory, SelfHostProject project, HttpMethod method, string path, string body)
    {
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(method, $"/v3/manage/{project.ProjectId}/{path}");
        if (body.Length > 0) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add("x-public-key", project.PublicKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    private static JsonElement Definition(JsonElement? row)
    {
        var column = row!.Value.GetProperty("definition_json");
        return column.ValueKind == JsonValueKind.String ? JsonDocument.Parse(column.GetString()!).RootElement.Clone() : column;
    }

    [Fact]
    public async Task RateLimitRulesCanBeCreatedFromPresetsEditedAndDeleted()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        Assert.Contains("Rate limit rules", await owner.GetStringAsync(url + "/rate-limits"));

        using (var added = await PostFormAsync(owner, url + "/rate-limits", url + "/rate-limits",
            ("id", "rl_xp"), ("name", "XP cap"), ("collection", "*"), ("field", "xp"), ("scope", "per_player"), ("action", "clamp"),
            ("perDay", "500"), ("enabled", "true")))
            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        using (var preset = await PostFormAsync(owner, url + "/rate-limits", url + "/rate-limits/preset", ("preset", "gold")))
            Assert.Equal(HttpStatusCode.Redirect, preset.StatusCode);
        using (var invalid = await PostFormAsync(owner, url + "/rate-limits", url + "/rate-limits", ("id", "rl_bad"), ("field", "xp")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var edited = await PostFormAsync(owner, url + "/rate-limits?edit=rl_xp", url + "/rate-limits",
            ("originalId", "rl_xp"), ("id", "rl_xp"), ("collection", "*"), ("field", "xp"), ("scope", "global"), ("action", "reject"), ("perHour", "40")))
            Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);

        using (var listed = await ManageAsync(factory, project, HttpMethod.Get, "rate-limit-rules", ""))
        {
            var rules = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("rules");
            Assert.Equal(2, rules.GetArrayLength());
            var xp = rules.EnumerateArray().Single(rule => rule.GetProperty("id").GetString() == "rl_xp");
            Assert.Equal("global", xp.GetProperty("scope").GetString());
            Assert.Equal(40, xp.GetProperty("maxPerHour").GetInt32());
            Assert.False(xp.GetProperty("enabled").GetBoolean());
            Assert.Contains(rules.EnumerateArray(), rule => rule.GetProperty("id").GetString() == "rl_gold_hourly");
        }

        using (var deleted = await PostFormAsync(owner, url + "/rate-limits", url + "/rate-limits/delete", ("id", "rl_xp"), ("confirmed", "true")))
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        var page = await owner.GetStringAsync(url + "/rate-limits");
        Assert.DoesNotContain("rl_xp", page);
        Assert.Contains("rl_gold_hourly", page);
    }

    [Fact]
    public async Task WebhookProfilesPersistAndFeedTheBuilder()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        const string hook = "https://discord.com/api/webhooks/123/abc";
        using (var rejected = await PostFormAsync(owner, url + "/webhooks", url + "/webhooks/profiles",
            ("id", "shop"), ("name", "Shop"), ("url", "https://example.com/hook"), ("enabled", "true")))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var saved = await PostFormAsync(owner, url + "/webhooks", url + "/webhooks/profiles",
            ("id", "shop"), ("name", "Shop"), ("url", hook), ("color", "ff8800"), ("enabled", "true")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var defaulted = await PostFormAsync(owner, url + "/webhooks", url + "/webhooks/default", ("defaultUrl", hook)))
            Assert.Equal(HttpStatusCode.Redirect, defaulted.StatusCode);

        var json = JsonDocument.Parse(await owner.GetStringAsync(url + "/webhooks/profiles.json")).RootElement;
        Assert.Equal(hook, json.GetProperty("defaultUrl").GetString());
        var profile = Assert.Single(json.GetProperty("profiles").EnumerateArray());
        Assert.Equal("shop", profile.GetProperty("id").GetString());
        Assert.Equal("ff8800", profile.GetProperty("color").GetString());
        var page = await owner.GetStringAsync(url + "/webhooks");
        Assert.Contains("type: webhook", page);
        Assert.Contains(hook, page);

        await using var scope = factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, CancellationToken.None);
        Assert.Equal(hook, access!.Project.DiscordWebhook);
    }

    [Fact]
    public async Task TestRunnerVersionsAndPagesWorkFromTheDashboard()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        (await ManageAsync(factory, project, HttpMethod.Patch, "collections",
            """{"collection":{"name":"players","collectionType":"per-steamid","schema":{"coins":{"type":"number"}}}}""")).Dispose();
        const string endpoint = """{"endpoint":{"slug":"grant_coins","method":"POST","enabled":true,"description":"first","steps":[{"id":"grant","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"inc","path":"coins","value":5}]}],"response":{"status":200,"body":{"ok":true}}}}""";
        using (var created = await ManageAsync(factory, project, HttpMethod.Patch, "endpoints", endpoint))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using (var changed = await ManageAsync(factory, project, HttpMethod.Patch, "endpoints", """{"endpoint":{"slug":"grant_coins","description":"second"}}"""))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // Try it, saved as a test: executes and shows the queued write, persists nothing.
        using (var run = await PostFormAsync(owner, url + "/tests?endpoint=grant_coins", url + "/tests/run",
            ("endpoint", "grant_coins"), ("input", "{}"), ("steamId", SteamId), ("expectOutcome", "pass"), ("name", "Grant five"), ("intent", "save")))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var html = await run.Content.ReadAsStringAsync();
            Assert.Contains("Passed", html);
            Assert.Contains("HTTP 200", html);
            Assert.Contains("Writes that would run", html);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            Assert.Empty(await store.ListRecordsAsync(project.ProjectId, "players", CancellationToken.None));
        }
        using (var tests = await ManageAsync(factory, project, HttpMethod.Get, "tests", ""))
            Assert.Contains("Grant five", await tests.Content.ReadAsStringAsync());
        using (var runAll = await PostFormAsync(owner, url + "/tests", url + "/tests/run-all"))
            Assert.Contains("1 passed", await runAll.Content.ReadAsStringAsync());

        // Versions: two snapshots, restore the older one.
        var versions = await owner.GetStringAsync(url + "/versions?kind=endpoint&id=grant_coins");
        Assert.Contains("Versions of", versions);
        var older = System.Text.RegularExpressions.Regex.Matches(versions, "v=([0-9]{13}-[0-9a-f]{16}\\.json)").Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.Equal(2, older.Count);
        var olderPage = await owner.GetStringAsync(url + "/versions?kind=endpoint&id=grant_coins&v=" + older[1]);
        Assert.Contains("Restore this version", olderPage);
        Assert.Contains("second", olderPage);
        using (var restored = await owner.PostAsync(url + "/versions/restore", Form(("kind", "endpoint"), ("id", "grant_coins"), ("v", older[1]),
            ("__RequestVerificationToken", Csrf(olderPage)))))
            Assert.Equal(HttpStatusCode.Redirect, restored.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            Assert.Equal("first", Definition(await store.ReadEndpointAsync(project.ProjectId, "grant_coins", CancellationToken.None)).GetProperty("description").GetString());
        }

        // Pages: published content is served by the public pages route.
        using (var saved = await PostFormAsync(owner, url + "/pages", url + "/pages",
            ("slug", "patch-notes"), ("title", "Patch notes"), ("type", "markdown"), ("content", "## 1.2\n- Faster saves")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var data = await PostFormAsync(owner, url + "/pages", url + "/pages",
            ("slug", "motd"), ("title", "Message of the day"), ("type", "keyvalue"), ("content", "{\"motd\":\"Welcome\"}")))
            Assert.Equal(HttpStatusCode.Redirect, data.StatusCode);
        using var anonymous = factory.CreateClient();
        var markdown = JsonDocument.Parse(await anonymous.GetStringAsync($"/pages/{project.ProjectId}/patch-notes")).RootElement;
        Assert.Equal("Patch notes", markdown.GetProperty("title").GetString());
        Assert.Contains("Faster saves", markdown.GetProperty("markdown").GetString());
        var keyValue = JsonDocument.Parse(await anonymous.GetStringAsync($"/pages/{project.ProjectId}/motd")).RootElement;
        Assert.Equal("Welcome", keyValue.GetProperty("data").GetProperty("motd").GetString());
        // The page shows the absolute public URL and how the game fetches it.
        var markdownPage = WebUtility.HtmlDecode(await owner.GetStringAsync(url + "/pages?slug=patch-notes"));
        var publicUrl = System.Text.RegularExpressions.Regex.Match(markdownPage, $"<pre>(https?://[^<]+/pages/{project.ProjectId}/patch-notes)</pre>").Groups[1].Value;
        Assert.NotEmpty(publicUrl);
        Assert.Contains($"Http.RequestJsonAsync<JsonElement>( \"{publicUrl}\" );", markdownPage);
        Assert.Contains("page.GetProperty( \"markdown\" )", markdownPage);
        Assert.Contains("page.GetProperty( \"data\" )", WebUtility.HtmlDecode(await owner.GetStringAsync(url + "/pages?slug=motd")));
        using (var deleted = await PostFormAsync(owner, url + "/pages?slug=motd", url + "/pages/delete", ("slug", "motd"), ("confirmed", "true")))
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        using var gone = await anonymous.GetAsync($"/pages/{project.ProjectId}/motd");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ResourceEditorShowsHowTheGameCallsTheOpenResource()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        const string miners = "id: miners\nname: miners\ncollectionType: per-steamid\naccessMode: endpoint\nschema:\n  type: object\n  properties:\n    ore: { type: number, default: 0 }\n";
        const string wallet = "id: wallet\nname: wallet\ncollectionType: per-steamid\naccessMode: public\nschema:\n  type: object\n  properties:\n    coins: { type: number, default: 5 }\n    title: { type: string }\n";
        const string mine = "id: mine\nslug: mine\nmethod: POST\ninput:\n  properties:\n    amount: { type: number, default: 1 }\nsteps:\n  - id: add\n    type: write\n    collection: miners\n    key: \"{{steamId}}\"\n    ops:\n      - { op: inc, path: ore, value: \"{{input.amount}}\" }\nresponse:\n  status: 200\n  body: { ok: true }\n";
        foreach (var (kind, source) in new[] { ("collection", miners), ("collection", wallet), ("endpoint", mine) })
            using (var saved = await PostFormAsync(owner, $"{url}/resources/{kind}", $"{url}/resources/{kind}", ("definition", source)))
                Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var endpoint = WebUtility.HtmlDecode(await owner.GetStringAsync($"{url}/resources/endpoint?id=mine"));
        Assert.Contains("var result = await NetworkStorage.CallEndpoint( \"mine\", new { amount = 1 } );", endpoint);
        Assert.Contains("NetworkStorage.TryGetLastEndpointError( \"mine\", out var code, out var message );", endpoint);
        Assert.Contains($"href=\"{url}/tests?endpoint=mine#try-it\"", endpoint);
        Assert.Contains($"href=\"{url}/versions?kind=endpoint&id=mine\"", endpoint);

        var endpointOnly = WebUtility.HtmlDecode(await owner.GetStringAsync($"{url}/resources/collection?id=miners"));
        Assert.Contains("403 ENDPOINT_ONLY", endpointOnly);
        Assert.DoesNotContain("NetworkStorage.GetDocument", endpointOnly);
        Assert.Contains($"href=\"{url}/data/miners\"", endpointOnly);

        var direct = WebUtility.HtmlDecode(await owner.GetStringAsync($"{url}/resources/collection?id=wallet"));
        Assert.Contains("var doc = Game.SteamId.ToString();", direct);
        Assert.Contains("var data = await NetworkStorage.GetDocument( \"wallet\", doc );", direct);
        Assert.Contains("await NetworkStorage.SaveDocument( \"wallet\", doc, new { coins = 5, title = \"\" } );", direct);

        // A new definition has nothing to call yet.
        Assert.DoesNotContain("Use from the game", await owner.GetStringAsync($"{url}/resources/endpoint"));

        // Links from the data browser open Try it as that player; anything that is not a Steam ID is ignored.
        Assert.Contains("name=\"steamId\" value=\"76561198000000777\"", await owner.GetStringAsync($"{url}/tests?steamId=76561198000000777"));
        Assert.DoesNotContain("value=\"not-an-id\"", await owner.GetStringAsync($"{url}/tests?steamId=not-an-id"));
    }

    [Fact]
    public async Task AllProjectsLeadsWithCreateAndShowsCodingAgentSetup()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var empty = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        var create = empty.IndexOf("id=\"create-project\"", StringComparison.Ordinal);
        Assert.True(create >= 0);
        Assert.True(create < empty.IndexOf("Connect a game", StringComparison.Ordinal));
        Assert.Contains("data-dialog=\"create-project\"", empty);
        Assert.Contains("data-dialog=\"import\"", empty);
        Assert.Contains("data-dialog=\"export\"", empty);
        Assert.Contains("No projects yet", empty);
        Assert.DoesNotContain("<summary>Import or export</summary>", empty);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(empty, ">Projects</h[12]>"));
        Assert.DoesNotContain("Management coverage", empty);
        Assert.DoesNotContain("parity", empty);
        Assert.Contains("claude mcp add sbox-ns -- ssh my-vps sudo sbox-ns mcp", empty);
        Assert.Contains("\"sbox-ns\": { \"command\": \"sbox-ns\", \"args\": [\"mcp\"] }", empty);
        Assert.Contains("/plugin marketplace add sbox-cool/sbox-network-storage-server", empty);
        Assert.Contains("/plugin install sbox-ns@sbox-ns", empty);
        foreach (var setting in new[] { "mcp.allow_writes", "mcp.allow_data_writes", "mcp.allow_destructive" })
            Assert.Contains($"<code>sbox-ns config set {setting} true</code><button type=\"button\" class=\"secondary\" data-copy-previous>", empty);

        var project = await factory.CreateProjectAsync("Agent game");
        var listed = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        Assert.DoesNotContain("<summary>Import or export</summary>", listed);
        Assert.Contains("Import a portable project", listed);

        var overview = WebUtility.HtmlDecode(await owner.GetStringAsync($"/dashboard/projects/{project.ProjectId}"));
        Assert.Contains($"My sbox-ns project ID is {project.ProjectId}. Use the sbox-ns MCP tools", overview);
        Assert.DoesNotContain("parity", overview);
        // Advanced settings start folded, each with a sentence and a doc link; the revision policy says it never blocks.
        foreach (var summary in new[] { "Player keys", "Player projections", "Revision policy \\(applied by the client library\\)", "Auth sessions and encrypted requests" })
            Assert.Matches($"<details class=\"(card )?settings-advanced\">\\s*<summary>{summary}</summary>", overview);
        Assert.Contains("This server does not block any request.", overview);
        foreach (var anchor in new[] { "auth-sessions", "encrypted-requests", "player-key-mode", "legacy-player-projections", "revision-policy" })
            Assert.Contains($"docs/admin-panel.md#{anchor}\"", overview);
    }

    [Fact]
    public async Task DashboardUsesNetworkStorageBrand()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var page = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        Assert.Contains("<title>Network Storage · Owner console</title>", page);
        Assert.Contains("<span>Network Storage</span>", page);
        Assert.Contains("Network Storage · Single local owner", page);
        Assert.DoesNotContain("Networked Storage", page);
    }

    [Fact]
    public async Task RevisionPolicyAndSecretKeyPermissionsAreEditable()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        using (var saved = await PostFormAsync(owner, url, url + "/settings", ("tab", "revisions"), ("revisionEnforcementEnabled", "true"),
            ("revisionEnforcementMode", "force_upgrade"), ("revisionPostGraceAction", "block_all"), ("revisionGracePeriodMinutes", "30"),
            ("revisionNotifyMessage", "Please update"), ("revisionShowNewVersionBanner", "false"), ("revisionShowUpdateOptions", "true"), ("revisionShowPopupOnce", "true")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        using (var settings = await ManageAsync(factory, project, HttpMethod.Get, "settings", ""))
        {
            var values = JsonDocument.Parse(await settings.Content.ReadAsStringAsync()).RootElement.GetProperty("settings");
            Assert.True(values.GetProperty("revisionEnforcementEnabled").GetBoolean());
            Assert.Equal("force_upgrade", values.GetProperty("revisionEnforcementMode").GetString());
            Assert.Equal("block_all", values.GetProperty("revisionPostGraceAction").GetString());
            Assert.Equal(30, values.GetProperty("revisionGracePeriodMinutes").GetInt32());
            Assert.False(values.GetProperty("revisionShowNewVersionBanner").GetBoolean());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var secret = (await projects.GetProjectKeysAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, CancellationToken.None))
            .Single(key => key.KeyType == "secret");
        var fields = new List<(string, string)> { ("keyIdentifier", secret.KeyIdentifier!) };
        fields.AddRange(SboxNetworkStorage.Server.Owner.OwnerDashboardController.KeyScopes.Select(scopeName => ("scope_" + scopeName, scopeName == "endpoints" ? "r" : "none")));
        using (var permissions = await PostFormAsync(owner, url, url + "/keys/permissions", [.. fields]))
            Assert.Equal(HttpStatusCode.Redirect, permissions.StatusCode);
        using var denied = await ManageAsync(factory, project, HttpMethod.Get, "collections", "");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var allowed = await ManageAsync(factory, project, HttpMethod.Get, "endpoints", "");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task LegacyPlayerProjectionsDefaultOffForNewProjectsAndToggleFromProjectSettings()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var host = factory;
        using var client = owner;
        var store = factory.Services.GetRequiredService<INetworkStorageStore>();
        async Task<JsonElement> ProjectAsync() => (await store.ReadProjectAsync(project.ProjectId, CancellationToken.None))!.Value;
        Assert.False((await ProjectAsync()).TryGetProperty("legacyPlayerProjections", out var absent) && absent.ValueKind == JsonValueKind.True);

        using (var on = await PostFormAsync(owner, url, url + "/settings", ("tab", "legacy-projections"), ("legacyPlayerProjections", "true")))
            Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        Assert.True((await ProjectAsync()).GetProperty("legacyPlayerProjections").GetBoolean());
        Assert.Contains("checked", await owner.GetStringAsync(url), StringComparison.Ordinal);

        using (var off = await PostFormAsync(owner, url, url + "/settings", ("tab", "legacy-projections")))
            Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        Assert.False((await ProjectAsync()).GetProperty("legacyPlayerProjections").GetBoolean());
    }
}
