using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class OwnerManagementTests
{
    [Fact]
    public async Task OwnerIsPersistedHashedAndResetInvalidatesCredentials()
    {
        using var factory = new SqliteHostFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        var account = await accounts.CreateAsync("local-owner", "initial-owner-password", CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var persisted = await store.ReadWorkspaceObjectAsync(OwnerAccountService.AccountPath, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.DoesNotContain("initial-owner-password", persisted);
        Assert.NotEqual("initial-owner-password", account.PasswordHash);
        Assert.NotNull(await accounts.AuthenticateAsync("LOCAL-OWNER", "initial-owner-password", CancellationToken.None));
        Assert.Null(await accounts.AuthenticateAsync("local-owner", "incorrect-password", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.CreateAsync("second", "second-owner-password", CancellationToken.None));
        var changed = await accounts.ResetPasswordAsync("replacement-owner-password", CancellationToken.None);
        Assert.NotEqual(account.SecurityStamp, changed.SecurityStamp);
        Assert.Equal(account.Username, changed.Username);
        Assert.Null(await accounts.AuthenticateAsync("local-owner", "initial-owner-password", CancellationToken.None));
        await using var secondScope = factory.Services.CreateAsyncScope();
        Assert.NotNull(await secondScope.ServiceProvider.GetRequiredService<OwnerAccountService>()
            .AuthenticateAsync("local-owner", "replacement-owner-password", CancellationToken.None));
    }

    [Fact]
    public async Task LoginDashboardLogoutAndPasswordResetRequireValidSessionAndCsrf()
    {
        using var factory = new SqliteHostFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        await accounts.CreateAsync("owner", "initial-owner-password", CancellationToken.None);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonymous = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("http://localhost/login", anonymous.Headers.Location!.ToString());
        var noCsrf = await client.PostAsync("/login", Form(("username", "owner"), ("password", "initial-owner-password")));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var loginPage = await client.GetStringAsync("/login");
        var loggedIn = await client.PostAsync("/login", Form(("username", "owner"), ("password", "initial-owner-password"), ("__RequestVerificationToken", Csrf(loginPage))));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        Assert.Equal("/dashboard", loggedIn.Headers.Location!.ToString());
        var dashboard = await client.GetStringAsync("/dashboard");
        Assert.Contains("Create project", dashboard);
        Assert.DoesNotContain("<script", dashboard);
        Assert.DoesNotContain("style=", dashboard);
        var rejectedMutation = await client.PostAsync("/dashboard/projects", Form(("name", "Game")));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedMutation.StatusCode);
        var created = await client.PostAsync("/dashboard/projects", Form(("name", "Game"), ("requireSboxAuth", "true"), ("__RequestVerificationToken", Csrf(dashboard))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var project = await client.GetStringAsync(created.Headers.Location);
        Assert.Contains("Synced resources", project);
        Assert.Contains("API keys", project);
        var logout = await client.PostAsync("/logout", Form(("__RequestVerificationToken", Csrf(project))));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard")).StatusCode);
        loginPage = await client.GetStringAsync("/login");
        await client.PostAsync("/login", Form(("username", "owner"), ("password", "initial-owner-password"), ("__RequestVerificationToken", Csrf(loginPage))));
        await accounts.ResetPasswordAsync("replacement-owner-password", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup?token=" + factory.Services.GetRequiredService<OwnerSetupToken>().Value)).StatusCode);
    }

    [Fact]
    public void SetupCapabilityRejectsMalformedAndOtherServerTokens()
    {
        var token = new OwnerSetupToken();
        Assert.True(token.IsValid(token.Value));
        Assert.False(token.IsValid(new OwnerSetupToken().Value));
        Assert.False(token.IsValid(null));
        Assert.False(token.IsValid("invalid"));
        Assert.False(token.IsValid(new string('Z', 64)));
    }

    [Fact]
    public async Task FirstRunSetupRequiresLoopbackAndCorrectToken()
    {
        using var factory = new SqliteHostFactory();
        var token = factory.Services.GetRequiredService<OwnerSetupToken>().Value;
        var local = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Path = "/setup";
            context.Request.QueryString = new QueryString("?token=" + token);
        });
        Assert.Equal(StatusCodes.Status200OK, local.Response.StatusCode);
        var remote = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = "GET";
            context.Request.Path = "/setup";
            context.Request.QueryString = new QueryString("?token=" + token);
        });
        Assert.Equal(StatusCodes.Status404NotFound, remote.Response.StatusCode);
        var invalid = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Path = "/setup";
            context.Request.QueryString = new QueryString("?token=" + new string('0', 64));
        });
        Assert.Equal(StatusCodes.Status404NotFound, invalid.Response.StatusCode);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        => new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static string Csrf(string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "Expected an antiforgery token in the Razor form.");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }
}
