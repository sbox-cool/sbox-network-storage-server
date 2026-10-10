using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Owner credentials only cross the network encrypted: plain HTTP from another machine is refused (with the ways
/// forward) unless <c>adminpanel.allow_insecure_http</c> is on, while loopback, HTTPS and a TLS proxy on loopback work.
/// Logout ends copies of the session cookie, and owner login attempts are capped across all addresses.
/// </summary>
public sealed class OwnerTransportSecurityTests : IDisposable
{
    private const string Remote = "192.0.2.1";
    private readonly SqliteHostFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task RemotePlainHttpRefusesPasswordsAndLoginLinks()
    {
        await CreateOwnerAsync(factory);
        using var client = Client(factory);
        var csrf = Csrf(await client.GetStringAsync("/login"));

        using var page = await SendAsync(client, HttpMethod.Get, "/login", Remote);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("type=\"password\"", html);
        Assert.Contains("ssh -L", html);
        Assert.Contains("sbox-ns tunnel enable", html);
        Assert.Contains("adminpanel.allow_insecure_http", html);

        using var login = await SendAsync(client, HttpMethod.Post, "/login", Remote,
            Form(("username", Username), ("password", Password), ("__RequestVerificationToken", csrf)));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.DoesNotContain(SetCookies(login), cookie => cookie.StartsWith(OwnerHostingExtensions.AuthCookieName + "=", StringComparison.Ordinal));

        // The link is neither shown nor used up, so the owner can still open it through a tunnel.
        var token = await MintLoginLinkAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/login/link?token=" + token, Remote)).StatusCode);
        using var link = await SendAsync(client, HttpMethod.Post, "/login/link", Remote,
            Form(("token", token), ("__RequestVerificationToken", csrf)));
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<OwnerLoginLinkService>().IsValidAsync(token, default));

        // The same browser over loopback (for example an SSH port forward) signs in.
        using var local = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", csrf)));
        Assert.Equal(HttpStatusCode.Redirect, local.StatusCode);
    }

    [Fact]
    public async Task AllowInsecureHttpAcceptsRemotePlainHttpLogin()
    {
        await CreateOwnerAsync(factory);
        var config = Override(factory.Config, ("adminpanel.allow_insecure_http", true));
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(config)));
        using var client = Client(host);
        client.DefaultRequestHeaders.Add(SelfHostFactory.PeerHeader, Remote);
        var page = await client.GetStringAsync("/login");
        Assert.Contains("served over plain HTTP", page);
        using var login = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData("http", false)]
    public async Task ProxyOnLoopbackCountsAsHttpsOnlyWhenItForwardsHttps(string forwardedProto, bool accepted)
    {
        await CreateOwnerAsync(factory);
        // No cookie container: it would withhold the Secure cookies on an http:// test address.
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("http://localhost") });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", Remote);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);

        using var page = await client.GetAsync("/login");
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, page.StatusCode);
        if (!accepted) return;
        using var login = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(await page.Content.ReadAsStringAsync())))
        };
        login.Headers.Add("Cookie", string.Join("; ", SetCookies(page).Select(cookie => cookie[..cookie.IndexOf(';')])));
        using var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(SetCookies(response), cookie => cookie.StartsWith("__Host-" + OwnerHostingExtensions.AuthCookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteHttpsLoginIsAccepted()
    {
        await CreateOwnerAsync(factory);
        using var client = Client(factory, "https://localhost");
        client.DefaultRequestHeaders.Add(SelfHostFactory.PeerHeader, Remote);
        var page = await client.GetStringAsync("/login");
        using var login = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    [Fact]
    public async Task LogoutEndsACopiedSessionCookie()
    {
        await CreateOwnerAsync(factory);
        using var client = Client(factory);
        var page = await client.GetStringAsync("/login");
        using var login = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(page))));
        var session = SetCookies(login).Single(cookie => cookie.StartsWith(OwnerHostingExtensions.AuthCookieName + "=", StringComparison.Ordinal));
        session = session[..session.IndexOf(';')];

        using var copy = Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await GetDashboardWithCookieAsync(copy, session)).StatusCode);

        var dashboard = await client.GetStringAsync("/dashboard");
        using var logout = await client.PostAsync("/logout", Form(("__RequestVerificationToken", Csrf(dashboard))));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        using var afterLogout = await GetDashboardWithCookieAsync(copy, session);
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.StartsWith("/login", afterLogout.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task LoginAttemptsAreCappedAcrossAllAddresses()
    {
        using var client = Client(factory);
        var addresses = OwnerLoginLimits.AllClientsPerMinute / OwnerLoginLimits.PerClientPerMinute;
        for (var address = 1; address <= addresses; address++)
        {
            for (var attempt = 0; attempt < OwnerLoginLimits.PerClientPerMinute; attempt++)
            {
                using var allowed = await SendAsync(client, HttpMethod.Get, "/login", $"192.0.2.{address}");
                Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
            }
        }

        // A fresh address has its own allowance left but the shared one is used up.
        using var capped = await SendAsync(client, HttpMethod.Get, "/login", "198.51.100.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
        // Other owner pages and game routes are not affected.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/health")).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string peer, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(SelfHostFactory.PeerHeader, peer);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetDashboardWithCookieAsync(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static EffectiveConfig Override(EffectiveConfig config, params (string Key, object Value)[] overrides)
    {
        var values = config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var item in overrides) values[item.Key] = values[item.Key] with { Value = item.Value };
        return new EffectiveConfig { ConfigDirectory = config.ConfigDirectory, DataDirectory = config.DataDirectory, Layout = config.Layout,
            Values = values, LoadedFiles = config.LoadedFiles, Issues = [] };
    }
}
