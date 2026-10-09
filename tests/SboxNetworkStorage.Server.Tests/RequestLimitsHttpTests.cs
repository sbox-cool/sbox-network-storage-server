using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Request body limits, authentication before body reads, per-client rate limits,
/// usage metering of authenticated traffic only, and the shared failure throttles,
/// all over the live route pipeline.
/// </summary>
public abstract class RequestLimitsHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string PeerHeader = "X-Test-Peer";
    private const string ChunkedHeader = "X-Test-Chunked";
    private const long OneMiB = 1024 * 1024;

    private readonly TFactory factory;

    protected RequestLimitsHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    // ── Body limits ──

    [SkippableFact]
    public async Task UnauthenticatedStreamedBodyToUnmappedToolsPath_IsNotReadPastOneMiB()
    {
        using var host = Limited(_ => { });
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 64 * OneMiB, declaredLength: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tools/x") { Content = content };
        request.Headers.Add(ChunkedHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge, response.StatusCode.ToString());
        Assert.True(host.BodyBytesRead <= OneMiB + 1, $"{host.BodyBytesRead} body bytes were read");
    }

    [SkippableFact]
    public async Task DeclaredGigabyteBodyToUnmappedToolsPath_IsRejectedWithoutReading()
    {
        using var host = Limited(_ => { });
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 8 * OneMiB, declaredLength: 1L << 30);
        using var response = await client.PostAsync("/api/tools/x", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYLOAD_TOO_LARGE", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, host.BodyBytesRead);
    }

    [SkippableFact]
    public async Task DataPlaneWrite_OfThreeHundredKiB_Returns413Envelope()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            $"/v3/storage/{project.ProjectId}/players/76561198000000001?apiKey={project.SecretKey}",
            Json(300 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYLOAD_TOO_LARGE", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("for this route", body.RootElement.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task ChunkedBodyOverTheLimit_IsCutOffAtTheLimitWhileTheHandlerReads()
    {
        var project = await factory.CreateProjectAsync();
        using var host = Limited(o => o.ManagementBodyBytes = 256 * 1024);
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 8 * OneMiB, declaredLength: null);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = content };
        request.Headers.Add("x-api-key", project.SecretKey);
        request.Headers.Add(ChunkedHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYLOAD_TOO_LARGE", body.RootElement.GetProperty("error").GetString());
        Assert.True(host.BodyBytesRead <= 256 * 1024 + 1, $"{host.BodyBytesRead} body bytes were read");
    }

    [SkippableFact]
    public async Task OperatorCanRaiseTheDataPlaneLimit()
    {
        var project = await factory.CreateProjectAsync();
        using var raised = Limited(o => o.DataPlaneBodyBytes = 512 * 1024);
        using var client = raised.CreateClient();
        using var response = await client.PostAsync(
            $"/v3/storage/{project.ProjectId}/players/76561198000000001?apiKey={project.SecretKey}",
            Json(300 * 1024));

        // Not rejected for the route's size; the record itself is still capped separately.
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("for this route", text);
    }

    [SkippableFact]
    public async Task ManagementBodyAboveEightMiB_Returns413_AndSixMiBIsAccepted()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        using var tooLarge = ManagementPut(project, Json(9 * (int)OneMiB));
        using var rejected = await client.SendAsync(tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);

        using var ok = ManagementPut(project, Json(6 * (int)OneMiB));
        using var accepted = await client.SendAsync(ok);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, accepted.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, accepted.StatusCode);
    }

    [SkippableFact]
    public async Task ManagementMutationWithInvalidKey_Returns401WithoutReadingTheBody()
    {
        var project = await factory.CreateProjectAsync();
        using var host = Limited(_ => { });
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 6 * OneMiB, declaredLength: null);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v3/manage/{project.ProjectId}/sync") { Content = content };
        request.Headers.Add("x-api-key", "sbox_sk_not_a_real_key");
        request.Headers.Add(ChunkedHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.BodyBytesRead);
    }

    [SkippableFact]
    public async Task ManagementMutationWithoutKey_Returns401WithoutReadingTheBody()
    {
        var project = await factory.CreateProjectAsync();
        using var host = Limited(_ => { });
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 6 * OneMiB, declaredLength: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/endpoints") { Content = content };
        request.Headers.Add(ChunkedHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.BodyBytesRead);
    }

    [SkippableFact]
    public async Task HeartbeatWithoutKey_Returns401WithoutReadingTheBody()
    {
        var project = await factory.CreateProjectAsync();
        using var host = Limited(_ => { });
        using var client = host.CreateClient();
        using var content = new GeneratedContent(total: 100 * 1024, declaredLength: null);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/storage/{project.ProjectId}/stats/heartbeat?steamId=76561198000000001") { Content = content };
        request.Headers.Add(ChunkedHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.BodyBytesRead);
    }

    // ── Rate limits ──

    [SkippableFact]
    public async Task ExhaustedBucket_Returns429WithRetryAfter_AndAnotherClientIsUnaffected()
    {
        using var limited = Limited(o => o.Game = Bucket(burst: 3));
        using var client = limited.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.SendAsync(Get("/v3/storage", peer: "203.0.113.5"));
            Assert.Equal(HttpStatusCode.NotFound, ok.StatusCode);
        }

        using var limitedResponse = await client.SendAsync(Get("/v3/storage", peer: "203.0.113.5"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
        Assert.True(limitedResponse.Headers.RetryAfter?.Delta?.TotalSeconds > 0);
        using var body = JsonDocument.Parse(await limitedResponse.Content.ReadAsStringAsync());
        Assert.Equal("RATE_LIMITED", body.RootElement.GetProperty("error").GetString());

        using var other = await client.SendAsync(Get("/v3/storage", peer: "203.0.113.6"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [SkippableFact]
    public async Task ManagementRoutes_HaveTheirOwnBucket()
    {
        using var limited = Limited(o => o.Management = Bucket(burst: 2));
        using var client = limited.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var unauthorized = await client.SendAsync(Get("/v3/manage/p1/settings", peer: "203.0.113.5"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, unauthorized.StatusCode);
        }

        using var limitedResponse = await client.SendAsync(Get("/v3/manage/p1/settings", peer: "203.0.113.5"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);

        using var game = await client.SendAsync(Get("/v3/storage", peer: "203.0.113.5"));
        Assert.Equal(HttpStatusCode.NotFound, game.StatusCode);
    }

    [SkippableFact]
    public async Task SpoofedForwardingHeadersFromARemotePeer_DoNotChangeTheBucket()
    {
        using var limited = Limited(o =>
        {
            o.Game = Bucket(burst: 3);
            o.TrustConnectingIpHeader = true;
        });
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var request = Get("/v3/storage", peer: "203.0.113.5");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            request.Headers.Add("CF-Connecting-IP", $"192.0.2.{i + 1}");
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound,
             HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }

    [SkippableFact]
    public async Task ForwardedFor_IsHonouredOnlyFromLoopback()
    {
        using var limited = Limited(o => o.Game = Bucket(burst: 1));
        using var client = limited.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            using var request = Get("/v3/storage", peer: "127.0.0.1");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task TunnelMode_SeparatesClientsBehindTheLocalConnector()
    {
        using var limited = Limited(o =>
        {
            o.Game = Bucket(burst: 2);
            o.TrustConnectingIpHeader = true;
        });
        using var client = limited.CreateClient();

        async Task<HttpStatusCode> Send(string clientAddress)
        {
            using var request = Get("/v3/storage", peer: "127.0.0.1");
            request.Headers.Add("CF-Connecting-IP", clientAddress);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await Send("192.0.2.1"));
        Assert.Equal(HttpStatusCode.NotFound, await Send("192.0.2.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Send("192.0.2.1"));
        Assert.Equal(HttpStatusCode.NotFound, await Send("192.0.2.2"));
    }

    [SkippableFact]
    public async Task ConnectingIpHeader_IsIgnoredOutsideTunnelMode()
    {
        using var limited = Limited(o => o.Game = Bucket(burst: 2));
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = Get("/v3/storage", peer: "127.0.0.1");
            request.Headers.Add("CF-Connecting-IP", $"192.0.2.{i + 1}");
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], statuses);
    }

    // ── Usage metering ──

    [SkippableFact]
    public async Task UnauthenticatedRequestsToAProjectPath_CreateNoUsageRows()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var missing = await client.GetAsync($"/v3/{project.ProjectId}/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var wrongKey = await client.GetAsync($"/v3/storage/{project.ProjectId}/players/76561198000000001?apiKey=wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        }

        var tracker = factory.Services.GetRequiredService<NetworkStorageUsageTracker>();
        await tracker.FlushAsync(force: true, CancellationToken.None);
        Assert.Null(await ReadMonthlyUsageAsync(project.ProjectId));
    }

    [SkippableFact]
    public async Task AuthenticatedHeartbeat_IsMeteredOnce()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/v3/storage/{project.ProjectId}/stats/heartbeat?apiKey={project.PublicKey}&steamId=76561198000000001", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tracker = factory.Services.GetRequiredService<NetworkStorageUsageTracker>();
        await tracker.FlushAsync(force: true, CancellationToken.None);
        var monthly = await ReadMonthlyUsageAsync(project.ProjectId);
        Assert.NotNull(monthly);
        Assert.Equal(1, monthly!.Value.GetProperty("requests").GetInt64());
    }

    // ── Failure throttles ──

    [SkippableFact]
    public async Task HeartbeatsWithRotatingSteamIdsAndAWrongKey_ShareOneThrottle()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            using var failed = await client.PostAsJsonAsync(
                $"/v3/storage/{project.ProjectId}/stats/heartbeat?apiKey=wrong&steamId=7656119800000{i:D4}", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var blocked = await client.PostAsJsonAsync(
            $"/v3/storage/{project.ProjectId}/stats/heartbeat?apiKey=wrong&steamId=76561198999999999", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        // The throttle is per (client address, project): another project is not affected.
        var other = await factory.CreateProjectAsync();
        using var otherProject = await client.PostAsJsonAsync(
            $"/v3/storage/{other.ProjectId}/stats/heartbeat?apiKey={other.PublicKey}&steamId=76561198000000001", new { });
        Assert.Equal(HttpStatusCode.OK, otherProject.StatusCode);
    }

    // ── Response headers ──

    [SkippableFact]
    public async Task NoResponseCarriesAnXSboxcoolHeader()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var requests = new List<HttpRequestMessage>
        {
            Get("/v3/storage"),
            Get("/v3/unknown-unmatched-path"),
            Get($"/v3/storage/{project.ProjectId}/players/76561198000000001?apiKey={project.SecretKey}"),
            Get($"/v3/storage/{project.ProjectId}/players/76561198000000001?apiKey=wrong"),
            Get($"/v3/manage/{project.ProjectId}/settings", key: project.SecretKey),
            Get($"/v3/endpoints/{project.ProjectId}/missing?apiKey={project.PublicKey}"),
            Get($"/v3/queries/{project.ProjectId}/missing?apiKey={project.PublicKey}"),
            Get($"/v3/security-config/{project.ProjectId}"),
            Get("/dashboard"),
            Get("/health"),
            Get("/api/tools/x"),
            new(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = JsonContent.Create(new { packageIdent = "x" }) },
            new(HttpMethod.Post, $"/v3/storage/{project.ProjectId}/stats/heartbeat?apiKey={project.PublicKey}&steamId=76561198000000001")
            {
                Content = JsonContent.Create(new { }),
            },
            new(HttpMethod.Post, $"/v3/auth-sessions/{project.ProjectId}/create?apiKey={project.PublicKey}") { Content = JsonContent.Create(new { }) },
        };

        foreach (var request in requests)
        {
            using (request)
            {
                using var response = await client.SendAsync(request);
                var names = response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key));
                Assert.DoesNotContain(names, name => name.StartsWith("X-Sboxcool-", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    // ── helpers ──

    private LimitedHost Limited(Action<RequestLimitOptions> configure)
        => new(factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<BodyReadCounter>();
            services.AddTransient<IStartupFilter, TestTransportFilter>();
            services.Configure(configure);
        })));

    /// <summary>A derived host plus the count of request body bytes its pipeline consumed from the transport.</summary>
    private sealed class LimitedHost(SelfHostFactory host) : IDisposable
    {
        public long BodyBytesRead => Interlocked.Read(ref host.Services.GetRequiredService<BodyReadCounter>().Total);
        public HttpClient CreateClient() => host.CreateClient();
        public IServiceProvider Services => host.Services;
        public void Dispose() => host.Dispose();
    }

    private sealed class BodyReadCounter
    {
        public long Total;
    }

    /// <summary>A bucket that never refills inside a test.</summary>
    private static RateBucketOptions Bucket(int burst)
        => new() { Burst = burst, Refill = 1, Period = TimeSpan.FromHours(1) };

    private static HttpRequestMessage Get(string path, string? peer = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (peer is not null) request.Headers.Add(PeerHeader, peer);
        if (key is not null) request.Headers.Add("x-api-key", key);
        return request;
    }

    private static HttpRequestMessage ManagementPut(SelfHostProject project, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/v3/manage/{project.ProjectId}/sync") { Content = content };
        request.Headers.Add("x-api-key", project.SecretKey);
        return request;
    }

    private static StringContent Json(int bytes)
        => new("{\"blob\":\"" + new string('a', bytes - 12) + "\"}", Encoding.UTF8, "application/json");

    private async Task<JsonElement?> ReadMonthlyUsageAsync(string projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        return await store.ReadProjectUsageMonthlyAsync(projectId, DateTimeOffset.UtcNow.ToString("yyyy-MM"), CancellationToken.None);
    }

    /// <summary>
    /// Stands in for transport details the test server lacks: the peer address comes from a header,
    /// a header can hide the declared body length (as for a chunked upload), and the bytes the
    /// pipeline reads from the body are counted.
    /// </summary>
    private sealed class TestTransportFilter(BodyReadCounter counter) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(PeerHeader, out var peer) && IPAddress.TryParse(peer, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                if (context.Request.Headers.ContainsKey(ChunkedHeader))
                {
                    context.Request.Headers.Remove("Content-Length");
                }

                context.Request.Body = new CountingStream(context.Request.Body, counter);
                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class CountingStream(Stream inner, BodyReadCounter counter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref counter.Total, read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref counter.Total, read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Streams filler bytes in 64 KiB writes and counts those the server consumed, so a test can
    /// show how much of the body was read. Without a declared length the body is chunked.
    /// </summary>
    private sealed class GeneratedContent(long total, long? declaredLength) : HttpContent
    {
        private long produced;

        private long Produced => Interlocked.Read(ref produced);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'a');
            while (Produced < total)
            {
                var size = (int)Math.Min(chunk.Length, total - Produced);
                await stream.WriteAsync(chunk.AsMemory(0, size));
                Interlocked.Add(ref produced, size);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength ?? 0;
            return declaredLength is not null;
        }
    }
}

public sealed class SqliteRequestLimitsHttpTests(SqliteHostFactory factory)
    : RequestLimitsHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresRequestLimitsHttpTests(PostgresHostFactory factory)
    : RequestLimitsHttpTests<PostgresHostFactory>(factory);
