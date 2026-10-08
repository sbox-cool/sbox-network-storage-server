using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The owner data browser: authenticated, read-only listing of per-player and global records with paging,
/// key search, pretty JSON detail and JSON download; deleting requires antiforgery, confirmation and is audited.
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

        using var unconfirmed = await client.PostAsync(recordUrl + "/delete", Form(("confirmation", "player-2"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.NotNull(await ReadPlayerRecordAsync(projectId, "player-1"));

        using var deleted = await client.PostAsync(recordUrl + "/delete", Form(("confirmation", "player-1"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Equal($"/dashboard/projects/{projectId}/data/inventory", deleted.Headers.Location!.ToString());
        Assert.Null(await ReadPlayerRecordAsync(projectId, "player-1"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(recordUrl)).StatusCode);

        var globalUrl = $"/dashboard/projects/{projectId}/data/leaderboard/records/season-1";
        using var globalDeleted = await client.PostAsync(globalUrl + "/delete", Form(("confirmation", "season-1"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, globalDeleted.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        Assert.Null(await store.ReadGlobalRecordAsync(projectId, "leaderboard", "season-1", CancellationToken.None));
        var audit = await store.ListAuditLogsAsync(projectId, 50, CancellationToken.None);
        Assert.Equal(2, audit.Count(row => row.GetProperty("action").GetString() == "record.delete"));
    }

    private async Task<string> SeedAsync()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Browser Game");
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.SeedCollectionAsync(project.ProjectId, "inventory", "inventory", "private", new { collectionType = "player" });
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
