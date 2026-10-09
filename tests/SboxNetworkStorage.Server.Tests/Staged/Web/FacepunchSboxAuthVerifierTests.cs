using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
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
            new SboxAuthFailureThrottle(Bounded(), TimeProvider.System),
            new SboxTokenCache(Bounded(), TimeProvider.System));

        var result = await verifier.CheckAsync(
            new SboxAuthCheck(
                HostToken: "token-1",
                HostSteamId: "76561197960287930",
                ClientSteamId: null,
                ClientToken: null,
                ProxySignature: null,
                ApiKey: "api-key",
                ProjectId: "project-1",
                EndpointSlug: "endpoint-1",
                ClientIp: "198.51.100.1"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("76561197960287930", result.SteamId);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://public.facepunch.com/sbox/auth/token", handler.RequestUri?.ToString());
        Assert.Equal("{\"steamid\":\"76561197960287930\",\"token\":\"token-1\"}", handler.Body);
    }

    private const string SteamId = "76561197960287930";

    private static MemoryCache Bounded(int limit = 100) => new(new MemoryCacheOptions { SizeLimit = limit });

    private static SboxAuthCheck Check(string token, string steamId, string ip = "198.51.100.1", string project = "project-1")
        => new(token, steamId, null, null, null, "api-key", project, "endpoint-1", ip);

    private static FacepunchSboxAuthVerifier Verifier(
        CountingHandler handler, SboxAuthFailureThrottle throttle, SboxTokenCache tokens)
        => new(new HttpClient(handler), NullLogger<FacepunchSboxAuthVerifier>.Instance, throttle, tokens);

    [Fact]
    public async Task FailuresAreThrottledAcrossVerifierInstances_WithoutCallingFacepunch()
    {
        using var handler = new CountingHandler(HttpStatusCode.Forbidden, "{}");
        var throttle = new SboxAuthFailureThrottle(Bounded(), TimeProvider.System);
        var tokens = new SboxTokenCache(Bounded(), TimeProvider.System);

        // A fresh transient verifier per request, as the typed HTTP client produces them.
        for (var i = 0; i < 10; i++)
        {
            var failed = await Verifier(handler, throttle, tokens).CheckAsync(Check("bad", SteamId), CancellationToken.None);
            Assert.False(failed.Ok);
        }

        var calls = handler.Calls;
        var blocked = await Verifier(handler, throttle, tokens).CheckAsync(Check("bad", SteamId), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Contains("Too many failed", blocked.Error);
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task RotatingSteamIds_ShareOneThrottleBucket()
    {
        using var handler = new CountingHandler(HttpStatusCode.Forbidden, "{}");
        var throttle = new SboxAuthFailureThrottle(Bounded(), TimeProvider.System);
        var tokens = new SboxTokenCache(Bounded(), TimeProvider.System);

        for (var i = 0; i < 10; i++)
        {
            await Verifier(handler, throttle, tokens).CheckAsync(Check("bad", $"7656119796028{i:D4}"), CancellationToken.None);
        }

        var calls = handler.Calls;
        var blocked = await Verifier(handler, throttle, tokens).CheckAsync(Check("bad", "76561197960280000"), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Equal(calls, handler.Calls);

        // A different address is not affected.
        var other = await Verifier(handler, throttle, tokens).CheckAsync(Check("bad", SteamId, ip: "198.51.100.2"), CancellationToken.None);
        Assert.False(other.Ok);
        Assert.Equal(calls + 1, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulVerificationIsCachedForSixtySeconds()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK, $"{{\"Status\":\"ok\",\"SteamId\":{SteamId}}}");
        var time = new ManualTime();
        var verifier = Verifier(handler, new SboxAuthFailureThrottle(Bounded(), time), new SboxTokenCache(Bounded(), time));

        Assert.True((await verifier.CheckAsync(Check("token-1", SteamId), CancellationToken.None)).Ok);
        Assert.True((await verifier.CheckAsync(Check("token-1", SteamId), CancellationToken.None)).Ok);
        Assert.Equal(1, handler.Calls);

        // The cache key covers the token and the Steam id.
        Assert.True((await verifier.CheckAsync(Check("token-2", SteamId), CancellationToken.None)).Ok);
        Assert.Equal(2, handler.Calls);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await verifier.CheckAsync(Check("token-1", SteamId), CancellationToken.None)).Ok);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public void ThrottleCacheStaysWithinItsSizeLimit()
    {
        using var cache = Bounded(limit: 50);
        var throttle = new SboxAuthFailureThrottle(cache, TimeProvider.System);
        for (var i = 0; i < 500; i++)
        {
            throttle.RecordFailure($"203.0.113.{i % 250}", $"project-{i}");
        }

        Assert.True(cache.Count <= 50, $"cache holds {cache.Count} entries");
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
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
