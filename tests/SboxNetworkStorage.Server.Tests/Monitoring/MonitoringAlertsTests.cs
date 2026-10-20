using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Monitoring;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Server.Updates;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Alert rules (pending, firing, resolved, cooldown, request windows), channel routing and the webhook
/// channel (signatures, retries), plus the owner pages end to end. Every send goes to a fake.
/// </summary>
public sealed class MonitoringAlertsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sbox-ns-monitoring-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly FakeWebhooks _webhooks = new();

    public MonitoringAlertsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void GaugeRule_FiresAfterItsDuration_ResolvesOnce_AndCooldownSuppressesARefire()
    {
        var (store, engine, sampler) = Build();
        store.SaveChannel(Webhook("c1"), new ChannelSecrets(Url: "https://hooks.example/one"));
        store.SaveRule(new AlertRule("r1", "Update pending", "update.available", ">=", 1, ForSeconds: 60, AlertSeverity.Warning, [], true, CooldownMinutes: 30));

        engine.Evaluate(sampler, updateAvailable: true);
        Assert.Equal("pending", engine.Status("r1")!.State);
        _time.Now += TimeSpan.FromSeconds(59);
        engine.Evaluate(sampler, updateAvailable: true);
        Assert.Empty(_webhooks.Sent);

        _time.Now += TimeSpan.FromSeconds(1);
        engine.Evaluate(sampler, updateAvailable: true);
        Assert.Equal("firing", engine.Status("r1")!.State);
        Assert.Equal("firing", Assert.Single(_webhooks.Sent).Json.GetProperty("state").GetString());

        _time.Now += TimeSpan.FromSeconds(5);
        engine.Evaluate(sampler, updateAvailable: true);
        Assert.Single(_webhooks.Sent);

        engine.Evaluate(sampler, updateAvailable: false);
        Assert.Equal("ok", engine.Status("r1")!.State);
        Assert.Equal("resolved", _webhooks.Sent[^1].Json.GetProperty("state").GetString());

        // Fires again within the 30-minute cooldown: state changes, nobody is paged, and no resolve follows.
        _time.Now += TimeSpan.FromMinutes(1);
        engine.Evaluate(sampler, updateAvailable: true);
        _time.Now += TimeSpan.FromSeconds(60);
        engine.Evaluate(sampler, updateAvailable: true);
        Assert.Equal("firing", engine.Status("r1")!.State);
        engine.Evaluate(sampler, updateAvailable: false);
        Assert.Equal(2, _webhooks.Sent.Count);
    }

    [Fact]
    public void ErrorRateRule_NeedsTheMinimumRequestsInTheWindow()
    {
        var (store, engine, sampler, requests) = BuildWithRequests();
        store.SaveChannel(Webhook("c1"), new ChannelSecrets(Url: "https://hooks.example/one"));
        store.SaveRule(new AlertRule("r1", "Errors", "http.error_rate.percent", ">", 5, ForSeconds: 60, AlertSeverity.Error, [], true));

        sampler.Collect(); // baseline
        for (var i = 0; i < MonitoringMetrics.MinimumRequests - 1; i++) requests.Record(5, i < 10 ? 500 : 200);
        _time.Now += TimeSpan.FromSeconds(40);
        sampler.Collect();
        engine.Evaluate(sampler, updateAvailable: false);
        Assert.Equal("no data", engine.Status("r1")!.State);
        Assert.Empty(_webhooks.Sent);

        requests.Record(5, 200);
        _time.Now += TimeSpan.FromSeconds(10);
        sampler.Collect();
        engine.Evaluate(sampler, updateAvailable: false);

        Assert.Equal("firing", engine.Status("r1")!.State);
        Assert.Equal(50, engine.Status("r1")!.Value);
        Assert.Single(_webhooks.Sent);
    }

    [Fact]
    public async Task Dispatcher_RoutesBySeverityProjectScopeAndRuleChannels()
    {
        var (store, _, _) = Build();
        store.SaveChannel(Webhook("all"), new ChannelSecrets(Url: "https://hooks.example/all"));
        store.SaveChannel(Webhook("critical-only") with { MinSeverity = AlertSeverity.Critical }, new ChannelSecrets(Url: "https://hooks.example/critical"));
        store.SaveChannel(Webhook("project-a") with { ProjectId = "proj-a" }, new ChannelSecrets(Url: "https://hooks.example/project-a"));
        var dispatcher = Dispatcher(store);

        await dispatcher.SendAsync(Alert(AlertSeverity.Error, projectId: "proj-b"), null, CancellationToken.None);
        Assert.Equal(["https://hooks.example/all"], _webhooks.Sent.Select(s => s.Url));

        _webhooks.Sent.Clear();
        await dispatcher.SendAsync(Alert(AlertSeverity.Critical, projectId: "proj-a"), null, CancellationToken.None);
        Assert.Equal(["https://hooks.example/all", "https://hooks.example/critical", "https://hooks.example/project-a"], _webhooks.Sent.Select(s => s.Url).Order());

        _webhooks.Sent.Clear();
        await dispatcher.SendAsync(Alert(AlertSeverity.Critical, projectId: null), ["critical-only"], CancellationToken.None);
        Assert.Equal(["https://hooks.example/critical"], _webhooks.Sent.Select(s => s.Url));
    }

    [Fact]
    public void GenericWebhook_SignatureVerifiesWithTheSharedSecret()
    {
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var (_, body, headers) = WebhookChannel.Build(Alert(AlertSeverity.Warning, "proj-a"), "generic", "s3cret", at);

        var timestamp = headers.Single(h => h.Key == WebhookChannel.TimestampHeader).Value;
        var signature = headers.Single(h => h.Key == WebhookChannel.SignatureHeader).Value;
        Assert.Equal(at.ToUnixTimeSeconds().ToString(), timestamp);
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("s3cret"));
        var expected = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + Encoding.UTF8.GetString(body))));
        Assert.Equal("sha256=" + expected, signature);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(WebhookChannel.PayloadSchema, json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("warning", json.RootElement.GetProperty("severity").GetString());
        Assert.DoesNotContain("s3cret", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Webhook_RetriesServerErrorsButNotClientErrors()
    {
        var (store, _, _) = Build();
        store.SaveChannel(Webhook("c1"), new ChannelSecrets(Url: "https://hooks.example/one"));
        var dispatcher = Dispatcher(store);

        _webhooks.Statuses.Enqueue(503);
        _webhooks.Statuses.Enqueue(200);
        var ok = await dispatcher.SendAsync(Alert(AlertSeverity.Error, null), null, CancellationToken.None);
        Assert.True(Assert.Single(ok).Ok);
        Assert.Equal(2, _webhooks.Attempts);

        _webhooks.Attempts = 0;
        _webhooks.Statuses.Enqueue(400);
        var rejected = await dispatcher.SendAsync(Alert(AlertSeverity.Error, null), null, CancellationToken.None);
        var delivery = Assert.Single(rejected);
        Assert.False(delivery.Ok);
        Assert.Contains("400", delivery.Error);
        Assert.Equal(1, _webhooks.Attempts);
    }

    [Fact]
    public void DeletingAChannel_DropsItFromRulesAndRemovesItsSecret()
    {
        var (store, _, _) = Build();
        store.SaveChannel(Webhook("c1"), new ChannelSecrets(Url: "https://hooks.example/one"));
        store.SaveRule(new AlertRule("r1", "CPU", "cpu.percent", ">", 90, 120, AlertSeverity.Warning, ["c1"], true));
        Assert.True(store.HasSecret("c1"));

        Assert.True(store.DeleteChannel("c1"));

        Assert.Empty(Assert.Single(store.Rules).ChannelIds);
        Assert.False(store.HasSecret("c1"));
        // Survives a restart.
        var reloaded = new MonitoringStore(Config(), NullLogger<MonitoringStore>.Instance);
        Assert.Empty(reloaded.Channels);
        Assert.Empty(Assert.Single(reloaded.Rules).ChannelIds);
    }

    [Fact]
    public async Task OwnerPages_SaveAChannelAndRule_NeverRenderTheSecret_AndSendATest()
    {
        using var root = new SqliteHostFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IWebhookClient>(_webhooks)));
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        const string secretUrl = "https://hooks.example/T000/B000/very-secret-token";

        var page = await client.GetStringAsync("/dashboard/server/alerts");
        using (var saved = await client.PostAsync("/dashboard/server/alerts/channels", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("name", "Ops hook"), ("kind", "webhook"), ("preset", "slack"),
            ("minSeverity", "info"), ("projectId", ""), ("enabled", "true"), ("url", secretUrl))))
        {
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        }

        page = await client.GetStringAsync("/dashboard/server/alerts");
        Assert.Contains("Ops hook", page);
        Assert.DoesNotContain("very-secret-token", page);
        var channelId = Regex.Match(page, "channel=(c[0-9a-f]{12})").Groups[1].Value;
        Assert.DoesNotContain("very-secret-token", await client.GetStringAsync("/dashboard/server/alerts?channel=" + channelId));
        var settings = File.ReadAllText(Path.Combine(factory.Config.StateDirectory, "monitoring.json"));
        Assert.DoesNotContain("very-secret-token", settings);
        if (!OperatingSystem.IsWindows())
        {
            var secretFile = Path.Combine(factory.Config.SecretsDirectory, "monitoring", $"channel-{channelId}.json");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secretFile));
        }

        using (var rule = await client.PostAsync("/dashboard/server/alerts/rules", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("name", "Disk low"), ("metric", "disk.data.free.gb"),
            ("comparison", "<"), ("threshold", "5"), ("forSeconds", "300"), ("severity", "critical"), ("cooldownMinutes", "10"),
            ("channels", channelId), ("enabled", "true"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, rule.StatusCode);
        }

        using (var invalid = await client.PostAsync("/dashboard/server/alerts/rules", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("name", "Bad"), ("metric", "http.p95.ms"),
            ("comparison", ">"), ("threshold", "800"), ("forSeconds", "10"), ("severity", "warning"), ("enabled", "true"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("at least 60 seconds", await invalid.Content.ReadAsStringAsync());
        }

        using (var test = await client.PostAsync("/dashboard/server/alerts/channels/test", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("id", channelId))))
        {
            Assert.Contains("Test notification sent to Ops hook", await test.Content.ReadAsStringAsync());
        }

        var sent = Assert.Single(_webhooks.Sent);
        Assert.Equal(secretUrl, sent.Url);
        Assert.Contains("test", sent.Json.GetProperty("text").GetString());
        page = await client.GetStringAsync("/dashboard/server/alerts");
        Assert.Contains("Free disk space (data folder) &lt; 5 GB for 5 min", page);
        Assert.Contains("Test sent to Ops hook", page);
    }

    [Fact]
    public async Task ServerStats_ServesSamplesOnlyToTheOwner()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using (var anonymous = OwnerHttp.Client(factory))
        using (var denied = await anonymous.GetAsync("/dashboard/server/stats?viewer=x"))
        {
            Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        }

        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        var page = await client.GetStringAsync("/dashboard/server");
        Assert.Contains("data-server-live", page);

        JsonElement samples = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/dashboard/server/stats?viewer=t&interval=250&after=0"));
            Assert.Equal(250, json.RootElement.GetProperty("interval").GetInt32());
            samples = json.RootElement.GetProperty("samples").Clone();
            if (samples.GetArrayLength() > 0) break;
            await Task.Delay(100);
        }

        var sample = samples[samples.GetArrayLength() - 1];
        Assert.True(sample.GetProperty("processMemory").GetInt64() > 0);
        Assert.True(sample.GetProperty("seq").GetInt64() > 0);
    }

    [Fact]
    public async Task ServerStats_ReportsWriteQueueSettingsAndCounters()
    {
        using var factory = new SqliteHostFactory();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);

        using var json = JsonDocument.Parse(await client.GetStringAsync("/dashboard/server/stats?viewer=t&interval=250&after=0"));
        var queue = json.RootElement.GetProperty("writeQueue");
        Assert.True(queue.GetProperty("enabled").GetBoolean());
        Assert.Equal(64, queue.GetProperty("maxQueued").GetInt64());
        Assert.Equal(10000, queue.GetProperty("timeoutMs").GetInt64());
        Assert.Equal(0, queue.GetProperty("activeKeys").GetInt64());
        Assert.Equal(0, queue.GetProperty("queuedRequests").GetInt64());
        var counters = queue.GetProperty("counters");
        Assert.Equal(0, counters.GetProperty("rejected").GetInt64());
        Assert.Equal(0, counters.GetProperty("timedOut").GetInt64());

        var gate = json.RootElement.GetProperty("scriptGate");
        Assert.Equal(2000, gate.GetProperty("waitMs").GetInt64());
        Assert.Equal(2, gate.GetProperty("perProjectLimit").GetInt64());
        Assert.Equal(0, gate.GetProperty("activeSlots").GetInt64());
        Assert.Equal(0, gate.GetProperty("queuedRequests").GetInt64());
        Assert.Equal(0, gate.GetProperty("counters").GetProperty("timedOut").GetInt64());
    }

    private (MonitoringStore Store, AlertRuleEngine Engine, MetricsSampler Sampler) Build()
    {
        var (store, engine, sampler, _) = BuildWithRequests();
        return (store, engine, sampler);
    }

    private (MonitoringStore Store, AlertRuleEngine Engine, MetricsSampler Sampler, RequestMetrics Requests) BuildWithRequests()
    {
        var config = Config();
        var store = new MonitoringStore(config, NullLogger<MonitoringStore>.Instance);
        var engine = new AlertRuleEngine(store, new AlertThrottle(_time), Dispatcher(store), _time, NullLogger<AlertRuleEngine>.Instance);
        // A meter factory of our own: requests served by other test hosts in this process are not counted.
        var requests = new RequestMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var sampler = new MetricsSampler(new HostMetricsReader(), requests, store, engine, config, new UpdateNoticeState(), _time, NullLogger<MetricsSampler>.Instance);
        return (store, engine, sampler, requests);
    }

    private AlertDispatcher Dispatcher(MonitoringStore store) => new(
        new AlertOptions(false, "", "sbox-ns", false, "", 587, "", "", "", [], true),
        store, new NoDiscord(), new NoSmtp(), _webhooks, _time, NullLogger<AlertDispatcher>.Instance)
    {
        WebhookRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
    };

    private EffectiveConfig Config() => ConfigLoader.Load(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), environment: _ => null);

    private static ChannelDefinition Webhook(string id) => new(id, id, "webhook", "generic", AlertSeverity.Info, null, true);

    private AlertMessage Alert(AlertSeverity severity, string? projectId) => new(
        AlertMessage.RuleKind, severity, "Title", "Subject", "Message", [new AlertField("Rule", "r")], _time.GetUtcNow(), ProjectId: projectId);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWebhooks : IWebhookClient
    {
        public List<(string Url, JsonElement Json)> Sent { get; } = [];
        public Queue<int> Statuses { get; } = new();
        public int Attempts { get; set; }

        public Task<int> PostAsync(string url, string contentType, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Attempts++;
                var status = Statuses.Count > 0 ? Statuses.Dequeue() : 200;
                if (status == 200) Sent.Add((url, JsonDocument.Parse(body).RootElement.Clone()));
                return Task.FromResult(status);
            }
        }
    }

    private sealed class NoDiscord : IDiscordWebhookClient
    {
        public Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class NoSmtp : ISmtpTransport
    {
        public Task SendAsync(string host, int port, bool useTls, string username, string password, string from, IReadOnlyList<string> to,
            string subject, string body, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
