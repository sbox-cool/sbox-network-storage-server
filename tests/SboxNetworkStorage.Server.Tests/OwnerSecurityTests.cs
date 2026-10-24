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
        // Public RFC 6238 test vector (base32 of "12345678901234567890"), not a credential.
        const string rfc6238Vector = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("287082", OwnerTotp.Code(rfc6238Vector, 1));
        Assert.Equal(1L, OwnerTotp.Match(rfc6238Vector, "287082", -1, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Null(OwnerTotp.Match(rfc6238Vector, "287082", 1, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Null(OwnerTotp.Match(rfc6238Vector, "12345x", -1, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public async Task EnrollmentConfirmsEncryptsInvalidatesAndRecoveryIsSingleUse()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        var page = await client.GetStringAsync("/dashboard/security");
        var secret = Regex.Match(page, "<pre>([A-Z2-7]{32})</pre>").Groups[1].Value;
        // Phones open the otpauth link straight in an authenticator app, with the parameters the server checks codes with.
        Assert.Contains($"href=\"otpauth://totp/sbox-ns:{Uri.EscapeDataString(OwnerHttp.Username)}?secret={secret}&issuer=sbox-ns&algorithm=SHA1&digits=6&period=30\"",
            WebUtility.HtmlDecode(page));
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

    [Fact]
    public async Task PasswordChangeNeedsCurrentPasswordAndSecondFactorAndEndsSessions()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var enrolling = await SignInAsync(factory, OwnerHttp.Password, string.Empty);
        var codes = await EnrollAsync(enrolling);
        using var client = await SignInAsync(factory, OwnerHttp.Password, codes[0]);
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        var before = (await accounts.GetAsync(default))!;
        var page = await PageAsync(client, "/dashboard/security");

        using var withoutToken = await Send(client, HttpMethod.Post, "/dashboard/security/password", OwnerHttp.Form(
            ("currentPassword", OwnerHttp.Password), ("secondFactor", codes[1]), ("newPassword", NewPassword), ("confirmPassword", NewPassword)));
        Assert.True(withoutToken.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        using var wrongPassword = await Send(client, HttpMethod.Post, "/dashboard/security/password",
            PasswordForm(page, "not-the-current-password", codes[1], NewPassword, NewPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Contains("Enter your current password", await wrongPassword.Content.ReadAsStringAsync());
        using var wrongFactor = await Send(client, HttpMethod.Post, "/dashboard/security/password",
            PasswordForm(page, OwnerHttp.Password, "0123456789abcdef0123456789abcdef", NewPassword, NewPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrongFactor.StatusCode);
        using var mismatch = await Send(client, HttpMethod.Post, "/dashboard/security/password",
            PasswordForm(page, OwnerHttp.Password, codes[1], NewPassword, "different-replacement-password"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var unchanged = (await accounts.GetAsync(default))!;
        Assert.Equal(before.PasswordHash, unchanged.PasswordHash);
        Assert.Equal(before.SecurityStamp, unchanged.SecurityStamp);
        Assert.Equal(before.RecoveryHashes, unchanged.RecoveryHashes);

        using var changed = await Send(client, HttpMethod.Post, "/dashboard/security/password",
            PasswordForm(page, OwnerHttp.Password, codes[1], NewPassword, NewPassword));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Contains("/login", changed.Headers.Location?.OriginalString ?? string.Empty);
        var after = (await accounts.GetAsync(default))!;
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.RecoveryHashes!.Count() - 1, after.RecoveryHashes!.Count());
        using var oldPassword = await SignInAsync(factory, OwnerHttp.Password, string.Empty, HttpStatusCode.Unauthorized);
        using var newPasswordClient = await SignInAsync(factory, NewPassword, codes[2]);
        var persisted = (await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().ReadWorkspaceObjectAsync(OwnerAccountService.AccountPath, default))!;
        Assert.DoesNotContain(NewPassword, persisted);
        Assert.DoesNotContain(codes[1], persisted);

        using var stale = await Send(client, HttpMethod.Get, "/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Contains("/login", stale.Headers.Location?.OriginalString ?? string.Empty);
        using var replayedCode = await SignInAsync(factory, NewPassword, codes[1], HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RecoveryCodeRegenerationReplacesEveryCodeAndRequiresReauthentication()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var enrolling = await SignInAsync(factory, OwnerHttp.Password, string.Empty);
        var codes = await EnrollAsync(enrolling);
        using var client = await SignInAsync(factory, OwnerHttp.Password, codes[0]);
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        var before = (await accounts.GetAsync(default))!;
        var page = await PageAsync(client, "/dashboard/security");

        using var wrongPassword = await Send(client, HttpMethod.Post, "/dashboard/security/recovery-codes",
            ReauthForm(page, "not-the-current-password", codes[1]));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        using var replayed = await Send(client, HttpMethod.Post, "/dashboard/security/recovery-codes",
            ReauthForm(page, OwnerHttp.Password, codes[0]));
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(before.RecoveryHashes, (await accounts.GetAsync(default))!.RecoveryHashes);

        using var regenerated = await Send(client, HttpMethod.Post, "/dashboard/security/recovery-codes",
            ReauthForm(page, OwnerHttp.Password, codes[1]));
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        var recoveryHtml = await regenerated.Content.ReadAsStringAsync();
        Assert.Contains("data-copy-previous", recoveryHtml);
        Assert.Contains("data-download-previous", recoveryHtml);
        var fresh = RecoveryCodes(recoveryHtml);
        Assert.Equal(10, fresh.Length);
        Assert.Empty(fresh.Intersect(codes));
        var after = (await accounts.GetAsync(default))!;
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(10, after.RecoveryHashes!.Count());

        using var stale = await Send(client, HttpMethod.Get, "/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Contains("/login", stale.Headers.Location?.OriginalString ?? string.Empty);
        using var oldCode = await SignInAsync(factory, OwnerHttp.Password, codes[2], HttpStatusCode.Unauthorized);
        using var newCode = await SignInAsync(factory, OwnerHttp.Password, fresh[0]);
    }

    [Fact]
    public async Task DisablingTwoFactorRequiresReauthenticationAndAllowsPasswordOnlySignIn()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var enrolling = await SignInAsync(factory, OwnerHttp.Password, string.Empty);
        var codes = await EnrollAsync(enrolling);
        using var client = await SignInAsync(factory, OwnerHttp.Password, codes[0]);
        await using var scope = factory.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
        var before = (await accounts.GetAsync(default))!;
        var page = await PageAsync(client, "/dashboard/security");

        using var wrongPassword = await Send(client, HttpMethod.Post, "/dashboard/security/2fa/disable",
            ReauthForm(page, "not-the-current-password", codes[1]));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Equal(before.TotpSecret, (await accounts.GetAsync(default))!.TotpSecret);

        using var disabled = await Send(client, HttpMethod.Post, "/dashboard/security/2fa/disable",
            ReauthForm(page, OwnerHttp.Password, codes[1]));
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.Contains("/login", disabled.Headers.Location?.OriginalString ?? string.Empty);
        var after = (await accounts.GetAsync(default))!;
        Assert.Null(after.TotpSecret);
        Assert.Empty(after.RecoveryHashes ?? Enumerable.Empty<string>());
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);

        using var passwordOnly = await SignInAsync(factory, OwnerHttp.Password, string.Empty);
        var securityPage = await PageAsync(passwordOnly, "/dashboard/security");
        Assert.DoesNotContain("name=\"secondFactor\"", securityPage);
        Assert.DoesNotContain("/dashboard/security/recovery-codes", securityPage);
        Assert.DoesNotContain("/dashboard/security/2fa/disable", securityPage);
        using var notEnabled = await Send(passwordOnly, HttpMethod.Post, "/dashboard/security/2fa/disable",
            ReauthForm(securityPage, OwnerHttp.Password, string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, notEnabled.StatusCode);
        using var changed = await Send(passwordOnly, HttpMethod.Post, "/dashboard/security/password",
            PasswordForm(securityPage, OwnerHttp.Password, string.Empty, NewPassword, NewPassword));
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        using var newPasswordClient = await SignInAsync(factory, NewPassword, string.Empty);
    }

    private const string NewPassword = "replacement-owner-password";
    private static int peerCount;

    /// <summary>
    /// The owner-login limiter allows 10 requests per client address per minute, GETs included, so every request here
    /// gets its own peer address. Cookies stay with the client.
    /// </summary>
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(SelfHostFactory.PeerHeader, $"127.0.0.{Interlocked.Increment(ref peerCount) % 254 + 1}");
        return client.SendAsync(request);
    }

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        using var response = await Send(client, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpClient> SignInAsync(SelfHostFactory factory, string password, string secondFactor,
        HttpStatusCode expected = HttpStatusCode.Redirect)
    {
        var client = OwnerHttp.Client(factory);
        var page = await PageAsync(client, "/login");
        using var response = await Send(client, HttpMethod.Post, "/login", OwnerHttp.Form(("username", OwnerHttp.Username),
            ("password", password), ("secondFactor", secondFactor), ("__RequestVerificationToken", OwnerHttp.Csrf(page))));
        Assert.Equal(expected, response.StatusCode);
        return client;
    }

    private static async Task<string[]> EnrollAsync(HttpClient client)
    {
        var page = await PageAsync(client, "/dashboard/security");
        var secret = Regex.Match(page, "<pre>([A-Z2-7]{32})</pre>").Groups[1].Value;
        var enrollment = WebUtility.HtmlDecode(Regex.Match(page, "name=\"enrollment\" value=\"([^\"]+)\"").Groups[1].Value);
        var code = OwnerTotp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        using var enrolled = await Send(client, HttpMethod.Post, "/dashboard/security/enroll", OwnerHttp.Form(("password", OwnerHttp.Password),
            ("enrollment", enrollment), ("code", code), ("__RequestVerificationToken", OwnerHttp.Csrf(page))));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var codes = RecoveryCodes(await enrolled.Content.ReadAsStringAsync());
        Assert.Equal(10, codes.Length);
        return codes;
    }

    /// <summary>The recovery codes rendered in the response: 32 hex characters, one per line.</summary>
    private static string[] RecoveryCodes(string html)
        => Regex.Matches(html, "<pre>([^<]+)</pre>")
            .SelectMany(match => WebUtility.HtmlDecode(match.Groups[1].Value)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(line => Regex.IsMatch(line, "^[A-F0-9]{32}$"))
            .ToArray();

    private static FormUrlEncodedContent PasswordForm(string page, string current, string factor, string next, string confirm)
        => OwnerHttp.Form(("currentPassword", current), ("secondFactor", factor), ("newPassword", next), ("confirmPassword", confirm),
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)));

    private static FormUrlEncodedContent ReauthForm(string page, string current, string factor)
        => OwnerHttp.Form(("currentPassword", current), ("secondFactor", factor), ("__RequestVerificationToken", OwnerHttp.Csrf(page)));

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

    internal static EffectiveConfig Override(EffectiveConfig config, params (string Key, object Value)[] overrides)
    {
        var values = config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var item in overrides) values[item.Key] = values[item.Key] with { Value = item.Value };
        return new EffectiveConfig { ConfigDirectory = config.ConfigDirectory, DataDirectory = config.DataDirectory, Layout = config.Layout,
            Values = values, LoadedFiles = config.LoadedFiles, Issues = [] };
    }
}
