using System.Net;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Project hub: single-link cards with facts, header dialogs, ?q= filter and TempData flashes.</summary>
public sealed class OwnerProjectHubTests
{
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    private static int CardLinks(string html)
        => Regex.Matches(html, "<a class=\"card project-card\"").Count;

    [Fact]
    public async Task OwnerDialogAndToastAssetsAreServed()
    {
        using var factory = new SqliteHostFactory();
        using var anonymous = Client(factory);
        foreach (var path in new[] { "/owner-assets/dialog.js", "/owner-assets/toast.js" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        }
        var dialog = await anonymous.GetStringAsync("/owner-assets/dialog.js");
        Assert.Contains("OwnerDialog", dialog);
        Assert.DoesNotContain("window.confirm", dialog);
    }

    [Fact]
    public async Task SettingsPostShowsOneShotFlash()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Hub flashes");
        var url = $"/dashboard/projects/{project.ProjectId}";

        using var saved = await PostFormAsync(owner, url, url + "/settings",
            ("tab", "project"), ("name", "Hub flashes"), ("description", "hub"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var once = await owner.GetStringAsync(saved.Headers.Location);
        Assert.Contains("Settings saved", once);
        Assert.Contains("data-kind=\"success\"", once);
        var twice = await owner.GetStringAsync(saved.Headers.Location);
        Assert.DoesNotContain("Settings saved", twice);
    }

    [Fact]
    public async Task NewProjectCardIsASingleLinkWithEmptyFacts()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Hub card");

        var html = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        Assert.Equal(1, CardLinks(html));
        Assert.DoesNotContain("Open project", html);
        Assert.Contains("Not synced", html);
        Assert.Contains("No requests yet", html);
        Assert.Contains("Not set", html);
        Assert.Contains($"href=\"/dashboard/projects/{project.ProjectId}\"", html);
    }

    [Fact]
    public async Task CreateProjectViaDialogRedirectsToProjectWithFlash()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        using var created = await PostFormAsync(owner, "/dashboard", "/dashboard/projects", ("name", "Hub project"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith("/dashboard/projects/", created.Headers.Location!.ToString());
        var page = WebUtility.HtmlDecode(await owner.GetStringAsync(created.Headers.Location));
        Assert.Contains("Project Hub project created.", page);
        var again = WebUtility.HtmlDecode(await owner.GetStringAsync(created.Headers.Location));
        Assert.DoesNotContain("Project Hub project created.", again);
    }

    [Fact]
    public async Task CreateProjectErrorReopensDialogWithEnteredValues()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        using var rejected = await PostFormAsync(owner, "/dashboard", "/dashboard/projects", ("name", ""));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var html = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
        Assert.Contains("data-open-dialog=\"create-project\"", html);
        Assert.Contains("Project name is required", html);
    }

    [Fact]
    public async Task DialogQueryRendersDialogOpenForNoJs()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        var html = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard?dialog=import"));
        Assert.Contains("data-open-dialog=\"import\"", html);
        Assert.Matches(new Regex("<dialog class=\"owner-dialog\" id=\"import\"[^>]*open"), html);
    }

    [Fact]
    public async Task ImportErrorReopensImportDialog()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        using var rejected = await PostFormAsync(owner, "/dashboard", "/dashboard/import");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var html = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync());
        Assert.Contains("data-open-dialog=\"import\"", html);
        Assert.Contains("64 MiB", html);
    }

    [Fact]
    public async Task ExportWithSecretsStillRequiresPassword()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        using var refused = await PostFormAsync(owner, "/dashboard", "/dashboard/export", ("includeSecrets", "true"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains("data-open-dialog=\"export\"", html);
    }

    [Fact]
    public async Task ProjectFilterMatchesNameOrId()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        for (var i = 0; i < 8; i++) await factory.CreateProjectAsync($"Hub filter {i}");
        var other = await factory.CreateProjectAsync("Unrelated zebra");

        var filtered = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard?q=hub+filter"));
        Assert.Equal(8, CardLinks(filtered));
        Assert.DoesNotContain("Unrelated zebra", filtered);
        Assert.Contains("id=\"project-filter\"", filtered);
        Assert.Contains("data-enhance=\"true\"", filtered);

        var byId = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard?q=" + other.ProjectId));
        Assert.Equal(1, CardLinks(byId));
        Assert.Contains("Unrelated zebra", byId);

        var none = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard?q=zzz-no-such-project"));
        Assert.Equal(0, CardLinks(none));
        Assert.Contains("No projects match", none);
    }

    [Fact]
    public async Task FreshInstallShowsEmptyStateWithDialogActions()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        var html = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        Assert.Contains("No projects yet", html);
        Assert.Contains("data-dialog=\"create-project\"", html);
        Assert.Contains("data-dialog=\"import\"", html);
        Assert.Contains("data-dialog=\"export\"", html);
        Assert.DoesNotContain("project-grid", html);
    }

    [Fact]
    public async Task RevokeWithoutConfirmedShowsServerConfirmPage()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Hub confirm");
        var url = $"/dashboard/projects/{project.ProjectId}";

        var page = WebUtility.HtmlDecode(await owner.GetStringAsync(url));
        var key = Regex.Match(page, "keys/revoke[\\s\\S]*?name=\"key\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEqual("", key);

        using var confirm = await PostFormAsync(owner, url, url + "/keys/revoke",
            ("key", key), ("confirmTitle", "Revoke API key"), ("confirmMessage", "Revoke?"));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var body = WebUtility.HtmlDecode(await confirm.Content.ReadAsStringAsync());
        Assert.Contains("Revoke API key", body);
        Assert.Contains("name=\"confirmed\" value=\"true\"", body);

        Assert.Contains("Game client", WebUtility.HtmlDecode(await owner.GetStringAsync(url)));
    }

    [Fact]
    public async Task RevokeWithConfirmedRevokesAndFlashes()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Hub confirm go");
        var url = $"/dashboard/projects/{project.ProjectId}";

        var page = WebUtility.HtmlDecode(await owner.GetStringAsync(url));
        var key = Regex.Match(page, "keys/revoke[\\s\\S]*?name=\"key\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        using var revoked = await PostFormAsync(owner, url, url + "/keys/revoke",
            ("key", key), ("confirmed", "true"));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        var once = await owner.GetStringAsync(revoked.Headers.Location);
        Assert.Contains("revoked", once);
        Assert.DoesNotContain($"value=\"{key}\"", WebUtility.HtmlDecode(await owner.GetStringAsync(url)));
    }
}
