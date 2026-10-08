using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class OwnerSecurityTests
{
    [Fact]
    public void TotpMatchesRfc6238AndRejectsReplay()
    {
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("287082", OwnerTotp.Code(secret, 1));
        Assert.Equal(1L, OwnerTotp.Match(secret, "287082", -1, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Null(OwnerTotp.Match(secret, "287082", 1, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Null(OwnerTotp.Match(secret, "12345x", -1, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public async Task EnrollmentConfirmsEncryptsInvalidatesAndRecoveryIsSingleUse()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        var page = await client.GetStringAsync("/dashboard/security");
        var secret = Regex.Match(page, "<pre>([A-Z2-7]{32})</pre>").Groups[1].Value;
        var enrollment = WebUtility.HtmlDecode(Regex.Match(page, "name=\"enrollment\" value=\"([^\"]+)\"").Groups[1].Value);
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        var before = (await accounts.GetAsync(default))!;
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.EnrollAsync(before.SecurityStamp, enrollment, "invalid", default));
        Assert.Null((await accounts.GetAsync(default))!.TotpSecret);
        var code = OwnerTotp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        using var enrolled = await client.PostAsync("/dashboard/security/enroll", OwnerHttp.Form(("password", OwnerHttp.Password),
            ("enrollment", enrollment), ("code", code), ("__RequestVerificationToken", OwnerHttp.Csrf(page))));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var html = await enrolled.Content.ReadAsStringAsync();
        var recovery = Regex.Match(html, "<pre>([A-F0-9]{32})").Groups[1].Value;
        Assert.Equal(32, recovery.Length);
        var after = (await accounts.GetAsync(default))!;
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        var persisted = (await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ReadWorkspaceObjectAsync(OwnerAccountService.AccountPath, default))!;
        Assert.DoesNotContain(secret, persisted);
        Assert.DoesNotContain(recovery, persisted);
        Assert.False(await accounts.VerifySecondFactorAsync(after.SecurityStamp, code, default));
        Assert.True(await accounts.VerifySecondFactorAsync(after.SecurityStamp, recovery, default));
        Assert.False(await accounts.VerifySecondFactorAsync(after.SecurityStamp, recovery, default));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard")).StatusCode);
        using var login = OwnerHttp.Client(factory);
        var loginPage = await login.GetStringAsync("/login");
        using var rejected = await login.PostAsync("/login", OwnerHttp.Form(("username", OwnerHttp.Username), ("password", OwnerHttp.Password),
            ("secondFactor", "invalid"), ("__RequestVerificationToken", OwnerHttp.Csrf(loginPage))));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        loginPage = await login.GetStringAsync("/login");
        var freshCode = OwnerTotp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1);
        using var accepted = await login.PostAsync("/login", OwnerHttp.Form(("username", OwnerHttp.Username), ("password", OwnerHttp.Password),
            ("secondFactor", freshCode), ("__RequestVerificationToken", OwnerHttp.Csrf(loginPage))));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await accounts.ResetAuthenticatorAsync(default);
        Assert.Null((await accounts.GetAsync(default))!.TotpSecret);
        Assert.NotEqual(after.SecurityStamp, (await accounts.GetAsync(default))!.SecurityStamp);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "192.0.2.0/24")]
    public async Task PerimeterDeniesEveryOwnerSurfaceButNotHealth(bool enabled, string ips)
    {
        using var factory = new SqliteHostFactory();
        var config = Override(factory.Config, ("adminpanel.enabled", enabled), ("adminpanel.allowed_ips", ips));
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(new OwnerAccessPolicy(config))));
        using var client = OwnerHttp.Client(host);
        foreach (var path in new[] { "/login", "/setup", "/login/link", "/dashboard", "/dashboard/security" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { "/login", "/setup", "/login/link", "/logout", "/dashboard/security/enroll" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, OwnerHttp.Form())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v3/server-info")).StatusCode);
    }

    [Theory]
    [InlineData("{\"success\":true,\"action\":\"owner_login\",\"hostname\":\"example.sboxns.com\"}", 200, true)]
    [InlineData("{\"success\":false}", 200, false)]
    [InlineData("{\"success\":true,\"action\":\"wrong\",\"hostname\":\"example.sboxns.com\"}", 200, false)]
    [InlineData("{\"success\":true,\"action\":\"owner_login\",\"hostname\":\"attacker.example\"}", 200, false)]
    [InlineData("not json", 200, false)]
    [InlineData("{}", 503, false)]
    public async Task TurnstileChecksProviderSuccessActionHostnameAndFailsClosed(string body, int status, bool expected)
    {
        using var factory = new SqliteHostFactory();
        var config = Override(factory.Config, ("adminpanel.turnstile.enabled", true), ("adminpanel.turnstile.secret", "test-secret"),
            ("adminpanel.turnstile.hostname", "example.sboxns.com"));
        using var client = new HttpClient(new Provider(body, status));
        var service = new OwnerTurnstile(config, client);
        Assert.Equal(expected, await service.VerifyAsync("test-token", "owner_login", IPAddress.Loopback, default));
        Assert.False(await service.VerifyAsync(null, "owner_login", IPAddress.Loopback, default));
    }

    [Fact]
    public async Task TurnstileNetworkFailureDeniesAndDisabledInstallDoesNotCallProvider()
    {
        using var factory = new SqliteHostFactory();
        using var client = new HttpClient(new Provider(null, 0));
        var enabled = new OwnerTurnstile(Override(factory.Config, ("adminpanel.turnstile.enabled", true)), client);
        Assert.False(await enabled.VerifyAsync("token", "owner_login", null, default));
        Assert.True(await new OwnerTurnstile(factory.Config, client).VerifyAsync(null, "owner_login", null, default));
    }

    private sealed class Provider(string? body, int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (body is null) throw new HttpRequestException("provider unavailable");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static EffectiveConfig Override(EffectiveConfig config, params (string Key, object Value)[] overrides)
    {
        var values = config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var item in overrides) values[item.Key] = values[item.Key] with { Value = item.Value };
        return new EffectiveConfig { ConfigDirectory = config.ConfigDirectory, DataDirectory = config.DataDirectory,
            Values = values, LoadedFiles = config.LoadedFiles, Issues = [] };
    }
}
