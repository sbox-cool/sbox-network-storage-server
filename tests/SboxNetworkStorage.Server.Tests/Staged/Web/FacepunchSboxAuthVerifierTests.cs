using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class FacepunchSboxAuthVerifierTests
{
    [Fact]
    public async Task CheckAsync_PostsDirectAuthToPublicFacepunchEndpoint()
    {
        using var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Status\":\"ok\",\"SteamId\":76561197960287930}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var verifier = new FacepunchSboxAuthVerifier(
            http,
            NullLogger<FacepunchSboxAuthVerifier>.Instance,
            TimeProvider.System);

        var result = await verifier.CheckAsync(
            new SboxAuthCheck(
                HostToken: "token-1",
                HostSteamId: "76561197960287930",
                ClientSteamId: null,
                ClientToken: null,
                ProxySignature: null,
                ApiKey: "api-key",
                ProjectId: "project-1",
                EndpointSlug: "endpoint-1"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("76561197960287930", result.SteamId);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://public.facepunch.com/sbox/auth/token", handler.RequestUri?.ToString());
        Assert.Equal("{\"steamid\":\"76561197960287930\",\"token\":\"token-1\"}", handler.Body);
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
