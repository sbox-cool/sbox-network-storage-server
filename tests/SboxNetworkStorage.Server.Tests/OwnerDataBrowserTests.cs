using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The owner record editor: authenticated listing, schema-validated creation/editing,
/// atomic snapshot/version conflicts, confirmed deletion and JSON export.
/// </summary>
public abstract class OwnerDataBrowserTests<TFactory> : IDisposable
    where TFactory : SelfHostFactory, new()
{
    private readonly TFactory factory = new();

    protected OwnerDataBrowserTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);

    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task EveryDataRouteRequiresTheOwnerSession()
    {
        var projectId = await SeedAsync();
        using var anonymous = Client(factory);
        foreach (var path in new[]
        {
            $"/dashboard/projects/{projectId}/data",
            $"/dashboard/projects/{projectId}/data/inventory",
            $"/dashboard/projects/{projectId}/data/inventory/records/player-1",
            $"/dashboard/projects/{projectId}/data/inventory/new",
            $"/dashboard/projects/{projectId}/data/inventory/export"
        })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/login", response.Headers.Location!.ToString());
        }
        using var delete = await anonymous.PostAsync($"/dashboard/projects/{projectId}/data/inventory/records/player-1/delete", Form(("confirmation", "player-1")));
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.StartsWith("http://localhost/login", delete.Headers.Location!.ToString());
        Assert.NotNull(await ReadPlayerRecordAsync(projectId, "player-1"));
        foreach (var path in new[]
        {
            $"/dashboard/projects/{projectId}/data/inventory/new",
            $"/dashboard/projects/{projectId}/data/inventory/records/player-1/save"
        })
        {
            using var response = await anonymous.PostAsync(path, Form(("recordKey", "player-1"), ("payload", "{\"gold\":999}"), ("expectedVersion", "1")));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/login", response.Headers.Location!.ToString());
        }
        Assert.Equal(10, (await ReadPlayerRecordAsync(projectId, "player-1"))!.Value.GetProperty("gold").GetInt32());
    }

    [SkippableFact]
    public async Task ListsCollectionsPagesSearchesShowsAndDownloadsRecords()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        var collections = await client.GetStringAsync($"/dashboard/projects/{projectId}/data");
        Assert.Contains("inventory", collections);
        Assert.Contains("leaderboard", collections);
        Assert.Contains("<td>Per-player</td>", collections);
        Assert.Contains("<td>Global</td>", collections);
        Assert.Contains("<td>3</td>", collections);   // tombstoned player-9 is not counted
        Assert.Contains("<td>2</td>", collections);
        Assert.DoesNotContain("<script", collections);
        Assert.DoesNotContain("style=", collections);

        var firstPage = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/inventory?size=2");
        Assert.Contains("Page 1 of 2", firstPage);
        Assert.Contains("player-1", firstPage);
        Assert.Contains("player-2", firstPage);
        Assert.DoesNotContain("player-3", firstPage);
        Assert.DoesNotContain("player-9", firstPage);
        var secondPage = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/inventory?size=2&page=2");
        Assert.Contains("player-3", secondPage);
        var oversized = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/inventory?size=100000&page=-4");
        Assert.Contains("Page 1 of 1", oversized);

        var search = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/inventory?q=YER-2");
        Assert.Contains("1 match(es)", search);
        Assert.Contains("player-2", search);
        Assert.DoesNotContain("player-1</code>", search);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/dashboard/projects/{projectId}/data/inventory?q={new string('x', 300)}")).StatusCode);

        var detail = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/inventory/records/player-2");
        var detailText = System.Net.WebUtility.HtmlDecode(detail);
        Assert.Contains("\"gold\": 20", detailText);
        Assert.Contains("\n  \"", detailText); // pretty-printed, not a single line
        var global = await client.GetStringAsync($"/dashboard/projects/{projectId}/data/leaderboard/records/season-1");
        Assert.Contains("&quot;top&quot;", global);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/dashboard/projects/{projectId}/data/inventory/records/player-9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/dashboard/projects/{projectId}/data/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dashboard/projects/proj_missing/data")).StatusCode);

        using var export = await client.GetAsync($"/dashboard/projects/{projectId}/data/inventory/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition!.DispositionType);
        using var document = JsonDocument.Parse(await export.Content.ReadAsStringAsync());
        Assert.Equal("player", document.RootElement.GetProperty("collectionType").GetString());
        var records = document.RootElement.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(new string?[] { "player-1", "player-2", "player-3" }, records.Select(record => record.GetProperty("key").GetString()));
        Assert.Equal(20, records[1].GetProperty("payload").GetProperty("gold").GetInt32());
    }

    [SkippableFact]
    public async Task DeletingARecordRequiresAntiforgeryAndConfirmationAndIsAudited()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        var recordUrl = $"/dashboard/projects/{projectId}/data/inventory/records/player-1";
        var page = await client.GetStringAsync(recordUrl);

        using var forged = await client.PostAsync(recordUrl + "/delete", Form(("confirmation", "player-1")));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.NotNull(await ReadPlayerRecordAsync(projectId, "player-1"));

        using var unconfirmed = await client.PostAsync(recordUrl + "/delete", EditForm(page, ("confirmation", "player-2")));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.NotNull(await ReadPlayerRecordAsync(projectId, "player-1"));

        using var deleted = await client.PostAsync(recordUrl + "/delete", EditForm(page, ("confirmation", "player-1")));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Equal($"/dashboard/projects/{projectId}/data/inventory", deleted.Headers.Location!.ToString());
        Assert.Null(await ReadPlayerRecordAsync(projectId, "player-1"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(recordUrl)).StatusCode);

        var globalUrl = $"/dashboard/projects/{projectId}/data/leaderboard/records/season-1";
        var globalPage = await client.GetStringAsync(globalUrl);
        using var globalDeleted = await client.PostAsync(globalUrl + "/delete", EditForm(globalPage, ("confirmation", "season-1")));
        Assert.Equal(HttpStatusCode.Redirect, globalDeleted.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        Assert.Null(await store.ReadGlobalRecordAsync(projectId, "leaderboard", "season-1", CancellationToken.None));
        var audit = await store.ListAuditLogsAsync(projectId, 50, CancellationToken.None);
        Assert.Equal(2, audit.Count(row => row.GetProperty("action").GetString() == "record.delete"));
    }

    [SkippableFact]
    public async Task CreatesEditsAndDeletesPlayerAndGlobalRecordsWithoutCrossingTables()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        foreach (var (collection, key, initial, edited) in new[]
        {
            ("inventory", "steam:42", "{\"gold\":42}", "{\"gold\":43}"),
            ("leaderboard", "season-new", "{\"top\":[\"first\"]}", "{\"top\":[\"second\"]}")
        })
        {
            var url = $"/dashboard/projects/{projectId}/data/{collection}";
            var createPage = await client.GetStringAsync(url + "/new");
            using var created = await client.PostAsync(url + "/new", Form(("recordKey", key), ("payload", initial), ("__RequestVerificationToken", Csrf(createPage))));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            var recordUrl = url + "/records/" + Uri.EscapeDataString(key);
            Assert.Equal(recordUrl, created.Headers.Location!.ToString());
            var page = await client.GetStringAsync(recordUrl);
            Assert.Contains(collection == "inventory" ? "\"gold\": 42" : "\"first\"", WebUtility.HtmlDecode(page));
            using var saved = await client.PostAsync(recordUrl + "/save", EditForm(page, ("payload", edited)));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            var editedPage = await client.GetStringAsync(recordUrl);
            Assert.Equal("2", Hidden(editedPage, "expectedVersion"));
            Assert.Contains(edited.Contains("gold") ? "\"gold\": 43" : "\"second\"", WebUtility.HtmlDecode(editedPage));
            using var deleted = await client.PostAsync(recordUrl + "/delete", EditForm(editedPage, ("confirmation", key), ("payload", edited)));
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(recordUrl)).StatusCode);
            await using var scope = factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            if (collection == "leaderboard")
            {
                Assert.Null(await store.ReadGlobalRecordAsync(projectId, collection, key, CancellationToken.None));
                Assert.Null(await store.ReadRecordAsync(projectId, collection, key, CancellationToken.None));
            }
            else
            {
                var tombstone = await store.ReadRecordAsync(projectId, collection, key, CancellationToken.None);
                Assert.True(tombstone!.Value.GetProperty("deleted").GetBoolean());
                Assert.Equal(3, tombstone.Value.GetProperty("version").GetInt64());
            }
        }
        await using var auditScope = factory.Services.CreateAsyncScope();
        var logs = await auditScope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ListAuditLogsAsync(projectId, 50, CancellationToken.None);
        foreach (var action in new[] { "record.create", "record.update", "record.delete" })
            Assert.Equal(2, logs.Count(row => row.GetProperty("action").GetString() == action));
    }

    [SkippableFact]
    public async Task StaleEditAndDeletePreserveEnteredContentAndCannotReplaceConcurrentSave()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        foreach (var (collection, key, first, stale) in new[]
        {
            ("inventory", "player-1", "{\"gold\":55}", "{\"gold\":999}"),
            ("leaderboard", "season-1", "{\"top\":[\"winner\"]}", "{\"top\":[\"stale\"]}")
        })
        {
            var url = $"/dashboard/projects/{projectId}/data/{collection}/records/{key}";
            var page = await client.GetStringAsync(url);
            using var saved = await client.PostAsync(url + "/save", EditForm(page, ("payload", first)));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            using var conflict = await client.PostAsync(url + "/save", EditForm(page, ("payload", stale)));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var errorPage = WebUtility.HtmlDecode(await conflict.Content.ReadAsStringAsync());
            Assert.Contains("Conflict:", errorPage);
            Assert.Contains(stale, errorPage);
            Assert.Contains(Hidden(page, "snapshotToken"), errorPage);
            using var staleDelete = await client.PostAsync(url + "/delete", EditForm(page, ("confirmation", key), ("payload", stale)));
            Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
            var latest = WebUtility.HtmlDecode(await client.GetStringAsync(url));
            Assert.Contains(collection == "inventory" ? "\"gold\": 55" : "\"winner\"", latest);
        }
    }

    [SkippableFact]
    public async Task StaleSnapshotRejectsGameUpdatesEvenWhenTheyReuseVersionOne()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        foreach (var (collection, key) in new[] { ("inventory", "player-1"), ("leaderboard", "season-1") })
        {
            var url = $"/dashboard/projects/{projectId}/data/{collection}/records/{key}";
            var page = await client.GetStringAsync(url);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var data = scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Application.NetworkStorage.INetworkStorageDataPlane>();
                await data.WriteRecordAsync(SboxNetworkStorage.Server.Hosting.NetworkStorageServices.LocalOwnerUserId,
                    projectId, collection, key, StoreSeeding.ToJson(new { gold = 77 }), CancellationToken.None);
                await data.WriteRecordAsync(SboxNetworkStorage.Server.Hosting.NetworkStorageServices.LocalOwnerUserId,
                    projectId, collection, key, StoreSeeding.ToJson(new { gold = 88 }), CancellationToken.None);
            }
            using var conflict = await client.PostAsync(url + "/save", EditForm(page, ("payload", "{\"gold\":999}")));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var deletion = await client.PostAsync(url + "/delete", EditForm(page, ("confirmation", key)));
            Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
            var latest = await client.GetStringAsync(url);
            Assert.Equal("1", Hidden(latest, "expectedVersion"));
            Assert.Contains("\"gold\": 88", WebUtility.HtmlDecode(latest));
        }
    }

    [SkippableFact]
    public async Task CreateChecksMissingAtomicallyAndSchemaErrorsKeepTheKeyAndJson()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        var collectionUrl = $"/dashboard/projects/{projectId}/data/inventory";
        var page = await client.GetStringAsync(collectionUrl + "/new");
        foreach (var json in new[] { "{\"gold\":\"not-a-number\"}", "{}", "{\"gold\":-1}", "{\"gold\":1,\"unexpected\":true}", "{broken" })
        {
            using var invalid = await client.PostAsync(collectionUrl + "/new", Form(("recordKey", "new-player"), ("payload", json), ("__RequestVerificationToken", Csrf(page))));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var errorPage = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
            Assert.Contains("new-player", errorPage);
            Assert.Contains(json, errorPage);
            Assert.Null(await ReadPlayerRecordAsync(projectId, "new-player"));
        }
        using var created = await client.PostAsync(collectionUrl + "/new", Form(("recordKey", "new-player"), ("payload", "{\"gold\":7}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var duplicate = await client.PostAsync(collectionUrl + "/new", Form(("recordKey", "new-player"), ("payload", "{\"gold\":9}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(7, (await ReadPlayerRecordAsync(projectId, "new-player"))!.Value.GetProperty("gold").GetInt32());
        var editPage = await client.GetStringAsync(collectionUrl + "/records/new-player");
        using var invalidEdit = await client.PostAsync(collectionUrl + "/records/new-player/save", EditForm(editPage, ("payload", "{\"gold\":false}")));
        Assert.Equal(HttpStatusCode.BadRequest, invalidEdit.StatusCode);
        Assert.Contains("{\"gold\":false}", WebUtility.HtmlDecode(await invalidEdit.Content.ReadAsStringAsync()));
        Assert.Equal(7, (await ReadPlayerRecordAsync(projectId, "new-player"))!.Value.GetProperty("gold").GetInt32());
    }

    [SkippableFact]
    public async Task SavesRequireCsrfAndTrustedMatchingSnapshotsAndEnforceInputLimits()
    {
        var projectId = await SeedAsync();
        using var client = await LoggedInClientAsync(factory);
        var collectionUrl = $"/dashboard/projects/{projectId}/data/inventory";
        var page = await client.GetStringAsync(collectionUrl + "/records/player-1");
        using var forgedEdit = await client.PostAsync(collectionUrl + "/records/player-1/save", Form(("payload", "{\"gold\":999}"), ("expectedVersion", "1"), ("snapshotToken", Hidden(page, "snapshotToken"))));
        using var forgedCreate = await client.PostAsync(collectionUrl + "/new", Form(("recordKey", "new-player"), ("payload", "{\"gold\":999}")));
        Assert.Equal(HttpStatusCode.BadRequest, forgedEdit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forgedCreate.StatusCode);
        using var missingSnapshot = await client.PostAsync(collectionUrl + "/records/player-1/save", Form(("payload", "{\"gold\":999}"), ("expectedVersion", "1"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, missingSnapshot.StatusCode);
        using var tampered = await client.PostAsync(collectionUrl + "/records/player-2/save", EditForm(page, ("payload", "{\"gold\":999}")));
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var badKey = await client.PostAsync(collectionUrl + "/new", Form(("recordKey", "invalid.key"), ("payload", "{\"gold\":1}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
        using var oversized = await client.PostAsync(collectionUrl + "/records/player-1/save", EditForm(page, ("payload", "{\"gold\":1,\"text\":\"" + new string('x', 65537) + "\"}")));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("byte limit", await oversized.Content.ReadAsStringAsync());
        var globalUrl = $"/dashboard/projects/{projectId}/data/leaderboard/new";
        using var badGlobalKey = await client.PostAsync(globalUrl, Form(("recordKey", "steam:42"), ("payload", "{}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, badGlobalKey.StatusCode);
        Assert.Equal(10, (await ReadPlayerRecordAsync(projectId, "player-1"))!.Value.GetProperty("gold").GetInt32());
        Assert.Equal(20, (await ReadPlayerRecordAsync(projectId, "player-2"))!.Value.GetProperty("gold").GetInt32());
    }

    [SkippableFact]
    public async Task NestedAndLegacySchemasAreEnforcedAndUnsupportedConstraintsFailClosed()
    {
        var projectId = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            await store.SeedCollectionAsync(projectId, "nested", "nested", "private", StoreSeeding.ToJson("""
                {"collectionType":"player","schema":{"type":"object","required":["profile","tags"],"properties":{
                  "profile":{"type":"object","required":["name"],"additionalProperties":false,"properties":{"name":{"type":"string","minLength":2,"maxLength":8}}},
                  "tags":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","enum":["a","b"]}}
                }}}
                """));
            await store.SeedCollectionAsync(projectId, "legacy", "legacy", "private", StoreSeeding.ToJson("""
                {"collectionType":"player","schema":{"type":"object","gold":"number"}}
                """));
            await store.SeedCollectionAsync(projectId, "unsupported", "unsupported", "private", StoreSeeding.ToJson("""
                {"collectionType":"player","schema":{"type":"object","properties":{"name":{"type":"string","pattern":"^a"}}}}
                """));
        }
        using var client = await LoggedInClientAsync(factory);
        var nestedUrl = $"/dashboard/projects/{projectId}/data/nested/new";
        var page = await client.GetStringAsync(nestedUrl);
        foreach (var payload in new[]
        {
            "{\"profile\":{},\"tags\":[\"a\"]}",
            "{\"profile\":{\"name\":\"x\"},\"tags\":[\"a\"]}",
            "{\"profile\":{\"name\":\"valid\",\"extra\":1},\"tags\":[\"a\"]}",
            "{\"profile\":{\"name\":\"valid\"},\"tags\":[]}",
            "{\"profile\":{\"name\":\"valid\"},\"tags\":[\"c\"]}"
        })
        {
            using var invalid = await client.PostAsync(nestedUrl, Form(("recordKey", "key"), ("payload", payload), ("__RequestVerificationToken", Csrf(page))));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains(payload, WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync()));
        }
        using var valid = await client.PostAsync(nestedUrl, Form(("recordKey", "key"), ("payload", "{\"profile\":{\"name\":\"valid\"},\"tags\":[\"a\"]}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        var legacyUrl = $"/dashboard/projects/{projectId}/data/legacy/new";
        using var badLegacy = await client.PostAsync(legacyUrl, Form(("recordKey", "key"), ("payload", "{\"gold\":\"wrong\"}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, badLegacy.StatusCode);
        using var goodLegacy = await client.PostAsync(legacyUrl, Form(("recordKey", "key"), ("payload", "{\"gold\":5}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, goodLegacy.StatusCode);
        using var unsupported = await client.PostAsync($"/dashboard/projects/{projectId}/data/unsupported/new",
            Form(("recordKey", "key"), ("payload", "{\"name\":\"b\"}"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Contains("not supported", await unsupported.Content.ReadAsStringAsync());
    }

    private static string Hidden(string page, string name)
    {
        var match = Regex.Match(page, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, $"Missing form field {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent EditForm(string page, params (string Key, string Value)[] values)
        => Form(values.Concat(new[]
        {
            ("expectedVersion", Hidden(page, "expectedVersion")),
            ("snapshotToken", Hidden(page, "snapshotToken")),
            ("__RequestVerificationToken", Csrf(page))
        }).ToArray());

    private async Task<string> SeedAsync()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Browser Game");
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.SeedCollectionAsync(project.ProjectId, "inventory", "inventory", "private", new
        {
            collectionType = "player",
            schema = new
            {
                type = "object", required = new[] { "gold" }, additionalProperties = false,
                properties = new { gold = new { type = "integer", minimum = 0 } }
            }
        });
        await store.SeedCollectionAsync(project.ProjectId, "leaderboard", "leaderboard", "public", new { collectionType = "global" });
        foreach (var index in new[] { 3, 1, 2 })
            await store.UpsertRecordAsync(project.ProjectId, "inventory", $"player-{index}", StoreSeeding.ToJson(new { gold = index * 10 }), false, 1, CancellationToken.None);
        await store.UpsertRecordAsync(project.ProjectId, "inventory", "player-9", JsonDocument.Parse("null").RootElement, true, 1, CancellationToken.None);
        await store.UpsertGlobalRecordAsync(project.ProjectId, "leaderboard", "season-1", StoreSeeding.ToJson(new { top = new[] { "a", "b" } }), 1, CancellationToken.None);
        await store.UpsertGlobalRecordAsync(project.ProjectId, "leaderboard", "season-2", StoreSeeding.ToJson(new { top = Array.Empty<string>() }), 1, CancellationToken.None);
        return project.ProjectId;
    }

    private async Task<JsonElement?> ReadPlayerRecordAsync(string projectId, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ReadRecordAsync(projectId, "inventory", key, CancellationToken.None);
        return row is { } value ? RecordRow.ExtractPayload(value) : null;
    }
}

public sealed class SqliteOwnerDataBrowserTests : OwnerDataBrowserTests<SqliteHostFactory>
{
}

public sealed class PostgresOwnerDataBrowserTests : OwnerDataBrowserTests<PostgresHostFactory>
{
}
