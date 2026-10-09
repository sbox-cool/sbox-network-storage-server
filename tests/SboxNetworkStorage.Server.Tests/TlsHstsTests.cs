using Microsoft.AspNetCore.Http;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class TlsHstsTests
{
    [Theory]
    [InlineData("acme", true, "https", true)]
    [InlineData("certificate", true, "https", true)]
    [InlineData("acme", false, "https", false)]
    [InlineData("off", true, "https", false)]
    [InlineData("acme", true, "http", false)]
    public async Task HstsSentOnlyOnHttpsWhenTlsEnabled(string mode, bool enabled, string scheme, bool expected)
    {
        using var factory = new SqliteHostFactory();
        var values = factory.Config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["tls.mode"] = values["tls.mode"] with { Value = mode };
        values["tls.hsts"] = values["tls.hsts"] with { Value = enabled };
        var config = new EffectiveConfig { ConfigDirectory = factory.Config.ConfigDirectory, DataDirectory = factory.Config.DataDirectory, Layout = factory.Config.Layout,
            Values = values, LoadedFiles = factory.Config.LoadedFiles, Issues = [] };
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        var middleware = new HstsMiddleware(_ => Task.CompletedTask, config);
        await middleware.InvokeAsync(context);
        Assert.Equal(expected, context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        if (expected)
            Assert.Equal(HstsMiddleware.HeaderValue, context.Response.Headers.StrictTransportSecurity.ToString());
    }
}
