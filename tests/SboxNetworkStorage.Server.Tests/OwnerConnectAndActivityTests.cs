using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Owner dashboard: the Connect your game card, the runtime request log and Errors tab fed by real traffic,
/// save diagnostics, key creation without resubmission, and the login page on a server without an owner.
/// </summary>
public sealed class OwnerConnectAndActivityTests : IDisposable
{
    private readonly SqliteHostFactory factory = new();

    public void Dispose() => factory.Dispose();

    private async Task<(HttpClient Owner, SelfHostProject Project, string Url)> SetupAsync()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Connect");
        return (await LoggedInClientAsync(factory), project, $"/dashboard/projects/{project.ProjectId}");
    }

    private Task FlushActivityAsync()
        => factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);

    private async Task<IReadOnlyList<System.Text.Json.JsonElement>> RequestLogAsync(string projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ListStorageRequestLogAsync(projectId, 50, CancellationToken.None);
    }

    [Fact]
    public async Task RejectedGameRequestAppearsInLogsAndOnTheOverviewChecklist()
    {
        var (owner, project, url) = await SetupAsync();
        using var _ = owner;
        var path = $"/v3/storage/{project.ProjectId}/players/76561198000000001";
        using var game = factory.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.Add("x-api-key", "sbox_ns_not_a_real_key");
            using var rejected = await game.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        // A wrong project ID in the URL must not create rows for a project that does not exist.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v3/storage/no_such_project/players/76561198000000001"))
        {
            request.Headers.Add("x-api-key", "sbox_ns_not_a_real_key");
            (await game.SendAsync(request)).Dispose();
        }
        await FlushActivityAsync();

        var row = Assert.Single(await RequestLogAsync(project.ProjectId));
        Assert.Equal(401, row.GetProperty("status_code").GetInt32());
        Assert.Empty(await RequestLogAsync("no_such_project"));

        var logs = await owner.GetStringAsync(url + "/activity/logs");
        Assert.Contains(path, logs);
        Assert.Contains("<th scope=\"col\">Status</th>", logs);
        Assert.Contains("UNAUTHORIZED or SBOX_AUTH_FAILED", logs);
        Assert.DoesNotContain("status code", logs);

        var overview = await owner.GetStringAsync(url);
        Assert.Contains("Rejected requests in the last 24 hours</td><td>1</td>", overview);
        Assert.Contains("<td>The game reached this server</td><td>Done</td>", overview);
        Assert.Contains("answered 401", overview);
    }

    [Fact]
    public async Task ReportedErrorsAppearInTheErrorsTabWithMessageStackAndExplanation()
    {
        var (owner, project, url) = await SetupAsync();
        using var _ = owner;
        Assert.Contains("No errors recorded", await owner.GetStringAsync(url + "/activity/errors"));

        await factory.Services.GetRequiredService<INetworkStorageErrorAlertSink>().NotifyAsync(new NetworkStorageError(
            project.ProjectId, "players", "76561198000000001", "save.unconfirmed", "SAVE_NOT_CONFIRMED", "Save not confirmed by client"), CancellationToken.None);
        await factory.Services.GetRequiredService<IExceptionAlertSink>().NotifyAsync(new CapturedErrorDto(
            Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, "sbox-ns", "POST", $"/v3/endpoints/{project.ProjectId}/grant", 500,
            "InvalidOperationException", "corr", "Sequence contains no elements", "at Grant.Run() in Grant.cs:line 12",
            ProjectId: project.ProjectId), CancellationToken.None);
        await FlushActivityAsync();

        var errors = await owner.GetStringAsync(url + "/activity/errors");
        Assert.Contains("SAVE_NOT_CONFIRMED", errors);
        Assert.Contains("Save not confirmed by client", errors);
        Assert.Contains("The client could not confirm a save reached the server.", errors);
        Assert.Contains("Sequence contains no elements", errors);
        Assert.Contains("<summary>Stack trace</summary><pre>at Grant.Run() in Grant.cs:line 12</pre>", errors);
    }

    [Fact]
    public async Task InvalidDefinitionSaveShowsEachDiagnosticAndKeepsTheText()
    {
        var (owner, _, url) = await SetupAsync();
        using var __ = owner;
        const string definition = "sourceVersion: 1\nkind: endpoint\nid: broken\nname: Broken\nmethod: PUT\nenabled: true\nsteps:\n  - id: ok\n    type: object\n    fields:\n      ok: true\nresponse:\n  status: 200\n  body: \"{{ok}}\"\n";
        var page = await owner.GetStringAsync(url + "/resources/endpoint");
        using var saved = await owner.PostAsync(url + "/resources/endpoint", Form(("definition", definition), ("__RequestVerificationToken", Csrf(page))));

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        var html = await saved.Content.ReadAsStringAsync();
        Assert.Contains("Could not save this definition", html);
        Assert.Contains("Error INVALID_METHOD at /method: Endpoint method must be GET or POST.", html);
        Assert.Contains("method: PUT", html);
    }

    [Fact]
    public async Task DashboardProjectGetsAPublicKeyAndTheOverviewShowsTheRealConfigureLine()
    {
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var dashboard = await owner.GetStringAsync("/dashboard");
        using var created = await owner.PostAsync("/dashboard/projects", Form(("name", "Connect me"), ("createPublicKey", "true"),
            ("__RequestVerificationToken", Csrf(dashboard))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var projectId = created.Headers.Location!.ToString().Split('/').Last();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var keys = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
                .GetProjectKeysAsync(NetworkStorageServices.LocalOwnerUserId, projectId, CancellationToken.None);
            var key = Assert.Single(keys);
            Assert.Equal(("public", true), (key.KeyType, key.Enabled));
            var overview = await owner.GetStringAsync(created.Headers.Location);
            Assert.Contains(WebUtility.HtmlEncode($"NetworkStorage.Configure( \"{projectId}\", \"{key.Key}\", \"http://localhost\" );"), overview);
            Assert.Contains("<td>A public key exists</td><td>Done</td>", overview);
            Assert.Contains("<td>The game reached this server</td><td>Not yet</td>", overview);
            Assert.Contains("curl http://localhost/v3/server-info", overview);
            // Opened from localhost without server.public_url: the owner is told players cannot use this address.
            Assert.Contains("This base URL only works on this machine.", overview);
        }

        using var withoutKey = await owner.PostAsync("/dashboard/projects", Form(("name", "No key"), ("__RequestVerificationToken", Csrf(dashboard))));
        var bare = await owner.GetStringAsync(withoutKey.Headers.Location);
        Assert.Contains("This project has no enabled public key.", bare);
        Assert.Contains(WebUtility.HtmlEncode("\"<public-api-key>\""), bare);
        Assert.Contains("No API keys yet.", bare);
    }

    [Fact]
    public async Task CreatedKeyIsShownOnceAndARefreshCreatesNothing()
    {
        var (owner, project, url) = await SetupAsync();
        using var _ = owner;
        var page = await owner.GetStringAsync(url);
        using var created = await owner.PostAsync(url + "/keys", Form(("label", "Editor"), ("keyType", "secret"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var shown = await owner.GetStringAsync(created.Headers.Location);
        Assert.Contains("API key created. Copy it now.", shown);
        var again = await owner.GetStringAsync(created.Headers.Location);
        Assert.DoesNotContain("API key created. Copy it now.", again);

        await using var scope = factory.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .GetProjectKeysAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, CancellationToken.None);
        Assert.Single(keys, key => key.Label == "Editor");
    }

    [Fact]
    public async Task LoginPageWithoutAnOwnerShowsTheStepsInsteadOfAForm()
    {
        using var client = Client(factory);
        var page = await client.GetStringAsync("/login");
        Assert.Contains("sbox-ns admin login-link", page);
        Assert.DoesNotContain("name=\"password\"", page);

        await CreateOwnerAsync(factory);
        Assert.Contains("name=\"password\"", await client.GetStringAsync("/login"));
    }
}
