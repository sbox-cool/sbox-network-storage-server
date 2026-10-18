using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Webhook delivery outbox: enqueue never sends, Discord 429/401/404 handling, one claim per row across restarts.</summary>
public sealed class WebhookOutboxTests
{
    private const string ProjectId = "proj_outbox";
    private const string Hook = "https://discord.com/api/webhooks/123/token-secret";
    private static readonly Dictionary<string, object?> Payload = new() { ["content"] = "hi" };
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EnqueueReturnsWithoutCallingDiscord()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, Payload, Ct);
        Assert.Equal(0, handler.Calls);

        var row = Assert.Single(await store.ClaimDueAsync(NowMs(time), NowMs(time) + 1000, 10, Ct));
        Assert.Equal(Hook, row.TargetUrl);
        Assert.Contains("\"content\":\"hi\"", row.PayloadJson);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DiscordRateLimitIsRetriedOnceAfterRetryAfter()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(call => call == 1
            ? new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"message\":\"You are being rate limited.\",\"retry_after\":0.5,\"global\":false}"),
            }
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var dispatcher = Dispatcher(store, handler, time);
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, Payload, Ct);

        Assert.Equal(1, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);

        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(2, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404: webhook not found")]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401: webhook unauthorized")]
    public async Task DeadWebhookDisablesTheTargetAndSuppressesRows(HttpStatusCode status, string reason)
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(call => call == 1 ? new HttpResponseMessage(status) : new HttpResponseMessage(HttpStatusCode.NoContent));
        var dispatcher = Dispatcher(store, handler, time);
        var writer = new WebhookOutboxWriter(store, time);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);

        // The first row disables the target; the second, claimed in the same batch, is suppressed without an HTTP call.
        Assert.Equal(2, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);
        var target = Assert.IsType<WebhookTargetState>(await store.ReadTargetAsync(ProjectId, Hook, Ct));
        Assert.NotNull(target.DisabledAtUnixMs);
        Assert.Equal(reason, target.DisabledReason);
        Assert.DoesNotContain("token-secret", target.DisabledReason);

        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        Assert.Equal(1, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);

        // The owner re-enables the target on the Webhooks page: the next enqueued row is delivered.
        await store.WriteTargetAsync(target with
        {
            ConsecutiveFailures = 0,
            FailingSinceUnixMs = null,
            DisabledAtUnixMs = null,
            DisabledReason = null,
            UpdatedAtUnixMs = NowMs(time),
        }, Ct);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        Assert.Equal(1, await dispatcher.DrainOnceAsync(10, Ct));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DeliveredRowIsNotResentAfterWorkerRestart()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, Payload, Ct);

        Assert.Equal(1, await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct));

        // A new dispatcher instance stands in for a restarted worker: the row is terminal, so nothing is claimed or sent.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RowInterruptedMidSendIsFailedAndNeverResent()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, Payload, Ct);

        // The worker claimed the row and died before it recorded an outcome.
        Assert.Single(await store.ClaimDueAsync(NowMs(time), NowMs(time) + 60_000, 10, Ct));
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await store.ReapExpiredLeasesAsync(NowMs(time), Ct));
        Assert.Equal(0, await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct));
        Assert.Equal(0, handler.Calls);
    }

    private static WebhookOutboxDispatcher Dispatcher(IWebhookOutboxStore store, ScriptedHandler handler, TimeProvider time)
        => new(store, new FixedClientFactory(handler), new WebhookRateLimitGate(), time, NullLogger<WebhookOutboxDispatcher>.Instance);

    private static long NowMs(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(Interlocked.Increment(ref _calls)));
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
