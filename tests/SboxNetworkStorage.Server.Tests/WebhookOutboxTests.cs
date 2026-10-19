using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Webhook delivery outbox: enqueue never sends, Discord 429/401/404 handling, one claim per row across restarts.</summary>
public sealed class WebhookOutboxTests
{
    private const string ProjectId = "proj_outbox";
    private const string Hook = "https://discord.com/api/webhooks/123/token-secret";
    private const string OtherHook = "https://discord.com/api/webhooks/456/token-other";
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

    [Fact]
    public async Task EnqueueRefusesAtPendingCapPerTargetWhileOtherTargetsStillQueue()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var writer = new WebhookOutboxWriter(store, time);
        for (var i = 0; i < WebhookOutboxWriter.MaxPendingPerTarget; i++)
            Assert.Equal(WebhookEnqueueOutcome.Queued, await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct));

        Assert.Equal(WebhookEnqueueOutcome.QueueFull, await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct));
        Assert.Equal(WebhookEnqueueOutcome.Queued, await writer.EnqueueAsync(ProjectId, OtherHook, Payload, Ct));
        Assert.Equal(WebhookEnqueueOutcome.Queued, await writer.EnqueueAsync("proj_other", Hook, Payload, Ct));

        var rows = await store.ClaimDueAsync(NowMs(time), NowMs(time) + 60_000, 10_000, Ct);
        Assert.Equal(WebhookOutboxWriter.MaxPendingPerTarget, rows.Count(r => r.ProjectId == ProjectId && r.TargetUrl == Hook));
        Assert.Equal(WebhookOutboxWriter.MaxPendingPerTarget + 2, rows.Count);
    }

    [Fact]
    public async Task DeliveredRowFreesAPendingSlot()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var writer = new WebhookOutboxWriter(store, time);
        for (var i = 0; i < WebhookOutboxWriter.MaxPendingPerTarget; i++)
            await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        Assert.Equal(WebhookEnqueueOutcome.QueueFull, await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct));

        Assert.Equal(1, await Dispatcher(store, handler, time).DrainOnceAsync(1, Ct));
        Assert.Equal(WebhookEnqueueOutcome.Queued, await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct));
    }

    [Fact]
    public async Task PendingRowsOlderThanMaxAgeExpireAndAreNeverSent()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var writer = new WebhookOutboxWriter(store, time);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        time.Advance(WebhookOutboxWriter.MaxPendingAge + TimeSpan.FromMinutes(1));
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);

        var cutoff = NowMs(time) - (long)WebhookOutboxWriter.MaxPendingAge.TotalMilliseconds;
        Assert.Equal(1, await store.ExpirePendingAsync(cutoff, NowMs(time), Ct));

        // Only the fresh row is delivered; the expired one is failed and never sent late.
        Assert.Equal(1, await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancelPendingSuppressesQueuedRowsWithoutSending()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var writer = new WebhookOutboxWriter(store, time);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);
        await writer.EnqueueAsync(ProjectId, Hook, Payload, Ct);

        Assert.Equal(2, await store.CancelPendingAsync(ProjectId, "outbox disabled", NowMs(time), Ct));
        Assert.Equal(0, await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PruneRemovesOnlyIdleEnabledTargetsWithoutOutboxRows()
    {
        const string idleHook = "https://discord.com/api/webhooks/789/idle";
        const string disabledHook = "https://discord.com/api/webhooks/790/disabled";
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        await store.WriteTargetAsync(new WebhookTargetState(ProjectId, idleHook, 0, null, null, null, null, null, 1), Ct);
        await store.WriteTargetAsync(new WebhookTargetState(ProjectId, Hook, 0, null, null, null, null, null, 1), Ct);
        await store.WriteTargetAsync(new WebhookTargetState(ProjectId, disabledHook, 5, 1, 404, "x", 1, "HTTP 404: webhook not found", 1), Ct);
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, Payload, Ct);

        Assert.Equal(1, await store.PruneStaleTargetsAsync(2, Ct));
        Assert.Null(await store.ReadTargetAsync(ProjectId, idleHook, Ct));
        Assert.NotNull(await store.ReadTargetAsync(ProjectId, Hook, Ct));
        Assert.NotNull(await store.ReadTargetAsync(ProjectId, disabledHook, Ct));
    }

    [Fact]
    public async Task QueuedRowCarriesTruncatedEmbedText()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var payload = new Dictionary<string, object?>
        {
            ["embeds"] = new List<object?> { new Dictionary<string, object?> { ["title"] = new string('x', 1000) } },
        };
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, Hook, payload, Ct);

        var row = Assert.Single(await store.ClaimDueAsync(NowMs(time), NowMs(time) + 60_000, 10, Ct));
        Assert.DoesNotContain(new string('x', 257), row.PayloadJson);
        Assert.Contains(new string('x', 256), row.PayloadJson);
    }

    [Fact]
    public void EmbedTextIsCutToDiscordLimits()
    {
        var fields = Enumerable.Range(0, 30).Select(_ => (object?)new Dictionary<string, object?>
        {
            ["name"] = new string('n', 300),
            ["value"] = new string('v', 1500),
            ["inline"] = false,
        }).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["embeds"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["title"] = new string('t', 400),
                    ["description"] = new string('d', 5000),
                    ["fields"] = fields,
                    ["footer"] = new Dictionary<string, object?> { ["text"] = new string('f', 3000) },
                },
            },
        };

        var embed = (Dictionary<string, object?>)((List<object?>)DiscordEmbedLimits.Apply(payload)["embeds"]!)[0]!;
        var title = Assert.IsType<string>(embed["title"]);
        var description = Assert.IsType<string>(embed["description"]);
        var footer = Assert.IsType<string>(((Dictionary<string, object?>)embed["footer"]!)["text"]);
        var outFields = Assert.IsType<List<Dictionary<string, object?>>>(embed["fields"]);

        Assert.Equal(256, title.Length);
        Assert.Equal(2048, footer.Length);
        Assert.True(outFields.Count <= 25);
        Assert.All(outFields, f =>
        {
            Assert.True(((string)f["name"]!).Length <= 256);
            Assert.True(((string)f["value"]!).Length <= 1024);
        });
        var total = title.Length + description.Length + footer.Length
            + outFields.Sum(f => ((string)f["name"]!).Length + ((string)f["value"]!).Length);
        Assert.True(total <= 6000);
        // The caller's payload is not modified.
        Assert.Equal(400, ((string)((Dictionary<string, object?>)((List<object?>)payload["embeds"]!)[0]!)["title"]!).Length);
    }

    [Fact]
    public void EmbedTruncationDoesNotSplitASurrogatePair()
    {
        var payload = new Dictionary<string, object?>
        {
            ["embeds"] = new List<object?> { new Dictionary<string, object?> { ["title"] = new string('a', 255) + "\U0001F600" } },
        };

        var title = (string)((Dictionary<string, object?>)((List<object?>)DiscordEmbedLimits.Apply(payload)["embeds"]!)[0]!)["title"]!;
        Assert.Equal(255, title.Length);
        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/tok", true)]
    [InlineData("https://discordapp.com/api/webhooks/1/tok", true)]
    [InlineData("http://discord.com/api/webhooks/1/tok", false)]
    [InlineData("https://user@discord.com/api/webhooks/1/tok", false)]
    [InlineData("https://discord.com:8443/api/webhooks/1/tok", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/tok", false)]
    [InlineData("https://discord.com/api/v10/channels/1/messages", false)]
    [InlineData("https://evil.example/api/webhooks/1/tok", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void OnlyDiscordWebhookUrlsAreAllowedAtSendTime(string? url, bool allowed)
        => Assert.Equal(allowed, DiscordWebhookUrl.IsAllowed(url));

    [Fact]
    public async Task DispatcherFailsRowWithNonDiscordTargetWithoutSending()
    {
        var time = new ManualTime();
        var store = new InMemoryWebhookOutboxStore();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await new WebhookOutboxWriter(store, time).EnqueueAsync(ProjectId, "https://evil.example/api/webhooks/1/tok", Payload, Ct);

        await Dispatcher(store, handler, time).DrainOnceAsync(10, Ct);

        Assert.Equal(0, handler.Calls);
        Assert.Empty(await store.ClaimDueAsync(NowMs(time), NowMs(time) + 60_000, 10, Ct));
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
