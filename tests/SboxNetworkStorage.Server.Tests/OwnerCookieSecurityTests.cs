using System.Net;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Owner cookies are HttpOnly; the session cookie is SameSite=Lax (links from Discord/email keep the owner signed
/// in; antiforgery guards every POST) and the antiforgery cookie SameSite=Strict. Over HTTPS both are <c>__Host-</c> cookies (Secure, Path=/,
/// no Domain) so a sibling subdomain cannot toss replacements; plain HTTP keeps the unprefixed names.
/// </summary>
public sealed class OwnerCookieSecurityTests : IDisposable
{
    private readonly SqliteHostFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task HttpsCookiesUseHostPrefixWithoutDomain()
    {
        await CreateOwnerAsync(factory);
        using var client = Client(factory, "https://localhost");
        using var loginPage = await client.GetAsync("/login");
        var csrfCookie = Assert.Single(SetCookies(loginPage), cookie => cookie.Contains("sbox-ns-csrf", StringComparison.Ordinal));
        AssertHostCookie(csrfCookie, "__Host-" + OwnerHostingExtensions.CsrfCookieName, "strict");

        using var login = await client.PostAsync("/login", Form(("username", Username), ("password", Password),
            ("__RequestVerificationToken", Csrf(await loginPage.Content.ReadAsStringAsync()))));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var sessionCookie = Assert.Single(SetCookies(login), cookie => cookie.Contains("sbox-ns-owner", StringComparison.Ordinal));
        AssertHostCookie(sessionCookie, "__Host-" + OwnerHostingExtensions.AuthCookieName, "lax");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.DoesNotContain(SetCookies(loginPage).Concat(SetCookies(login)), cookie => cookie.Contains("domain=", StringComparison.OrdinalIgnoreCase));

        // A cookie tossed under the unprefixed name (e.g. from a sibling subdomain) is ignored over HTTPS.
        var session = sessionCookie[..sessionCookie.IndexOf(';')]["__Host-".Length..];
        using var bare = Client(factory, "https://localhost");
        Assert.Equal(HttpStatusCode.Redirect, (await SendWithCookieAsync(bare, session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCookieAsync(bare, "__Host-" + session)).StatusCode);
    }

    [Fact]
    public async Task PlainHttpKeepsUnprefixedHttpOnlyCookies()
    {
        await CreateOwnerAsync(factory);
        using var client = Client(factory);
        using var loginPage = await client.GetAsync("/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var csrfCookie = Assert.Single(SetCookies(loginPage), cookie => cookie.Contains("sbox-ns-csrf", StringComparison.Ordinal));
        Assert.StartsWith(OwnerHostingExtensions.CsrfCookieName + "=", csrfCookie);
        using var login = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(html))));
        var sessionCookie = Assert.Single(SetCookies(login), cookie => cookie.Contains("sbox-ns-owner", StringComparison.Ordinal));
        Assert.StartsWith(OwnerHostingExtensions.AuthCookieName + "=", sessionCookie);
        var attributes = sessionCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList();
        Assert.Contains(attributes, attribute => attribute.Equals("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute => attribute.Equals("samesite=lax", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, attribute => attribute.Equals("secure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnlyPlainHttpFromARemoteAddressShowsTheHttpsWarning()
    {
        Assert.Contains("served over plain HTTP", await LoginPageAsync(IPAddress.Parse("192.0.2.1"), "http"));
        Assert.DoesNotContain("served over plain HTTP", await LoginPageAsync(IPAddress.Parse("192.0.2.1"), "https"));
        Assert.DoesNotContain("served over plain HTTP", await LoginPageAsync(IPAddress.Loopback, "http"));
        Assert.DoesNotContain("served over plain HTTP", await LoginPageAsync(IPAddress.IPv6Loopback, "http"));
    }

    private async Task<string> LoginPageAsync(IPAddress remote, string scheme)
    {
        var context = await factory.Server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = remote;
            request.Request.Scheme = scheme;
            request.Request.Method = "GET";
            request.Request.Path = "/login";
        });
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static void AssertHostCookie(string cookie, string expectedName, string sameSite)
    {
        Assert.StartsWith(expectedName + "=", cookie);
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList();
        Assert.Contains(attributes, attribute => attribute.Equals("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute => attribute.Equals("path=/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute => attribute.Equals("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute => attribute.Equals("samesite=" + sameSite, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static Task<HttpResponseMessage> SendWithCookieAsync(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }
}
