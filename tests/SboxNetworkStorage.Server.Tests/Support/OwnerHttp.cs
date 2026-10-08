using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>Browser-like helpers for the owner panel: forms, antiforgery tokens and a logged-in client.</summary>
public static class OwnerHttp
{
    public const string Username = "owner";
    public const string Password = "initial-owner-password";

    public static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        => new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    public static string Csrf(string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "Expected an antiforgery token in the Razor form.");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    public static async Task CreateOwnerAsync(SelfHostFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OwnerAccountService>().CreateAsync(Username, Password, CancellationToken.None);
    }

    public static HttpClient Client(SelfHostFactory factory, string baseAddress = "http://localhost")
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress) });

    public static async Task<HttpClient> LoggedInClientAsync(SelfHostFactory factory, string baseAddress = "http://localhost")
    {
        var client = Client(factory, baseAddress);
        var page = await client.GetStringAsync("/login");
        using var response = await client.PostAsync("/login", Form(("username", Username), ("password", Password), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<string> MintLoginLinkAsync(SelfHostFactory factory, int minutes = OwnerLoginLinkService.DefaultMinutes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var (token, _) = await scope.ServiceProvider.GetRequiredService<OwnerLoginLinkService>().CreateAsync(minutes, CancellationToken.None);
        return token;
    }
}
