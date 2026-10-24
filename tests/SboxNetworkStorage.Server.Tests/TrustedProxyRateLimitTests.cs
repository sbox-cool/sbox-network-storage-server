using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class TrustedProxyRateLimitTests
{
    [Fact]
    public async Task TrustedProxyPreservesDistinctClientRateLimitIdentities()
    {
        using var factory = new ConfiguredSqliteHostFactory(("server.trusted_proxies", "203.0.113.0/24"));
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<RequestLimitOptions>(options => options.Game = new RateBucketOptions
            {
                Burst = 1,
                Refill = 1,
                Period = TimeSpan.FromHours(1)
            })));
        using var client = host.CreateClient();

        async Task<HttpStatusCode> SendAsync(string clientAddress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v3/storage");
            request.Headers.Add(SelfHostFactory.PeerHeader, "203.0.113.5");
            request.Headers.Add("X-Forwarded-For", clientAddress);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await SendAsync("198.51.100.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync("198.51.100.7"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync("198.51.100.8"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync("198.51.100.8"));
    }
}
