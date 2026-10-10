using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Operator alerting (Discord webhook + SMTP) must fire on captured errors
/// with correlation context, never leak secrets, and never fail the request
/// that triggered the capture. All sends are captured by fakes — no network.
/// </summary>
public sealed class OperatorAlertingTests
{
    private const string CorrelationId = "11111111-2222-3333-4444-555555555555";
    private const string WebhookUrl = "https://discord.com/api/webhooks/fake-token";

    private static CapturedErrorDto SampleError() => new(
        Id: Guid.NewGuid().ToString("D"),
        Timestamp: new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero),
        Source: "sbox-ns",
        Method: "POST",
        Path: "/v3/records/proj123/coll",
        StatusCode: 500,
        Classification: "InvalidOperationException",
        CorrelationId: CorrelationId,
        Message: "boom",
        StackTrace: "at Boom()",
        ProjectId: "proj123",
        SteamId: "76561198021524886");

    private static AlertOptions EnabledOptions(AlertOptions @base) => @base with
    {
        DiscordEnabled = true,
        DiscordWebhookUrl = WebhookUrl,
        SmtpEnabled = true,
        SmtpHost = "mail.example.com",
        SmtpFrom = "sbox-ns@example.com",
        SmtpTo = ["ops@example.com"],
    };

    private static AlertOptions DisabledOptions() => new(
        DiscordEnabled: false, DiscordWebhookUrl: string.Empty, DiscordUsername: "sbox-ns",
        SmtpEnabled: false, SmtpHost: string.Empty, SmtpPort: 587,
        SmtpUsername: string.Empty, SmtpPassword: string.Empty,
        SmtpFrom: string.Empty, SmtpTo: [], SmtpUseTls: true);

    [Fact]
    public async Task Fanout_SendsDiscordAndSmtpWithCorrelationContext()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp);

        await sink.NotifyAsync(SampleError(), CancellationToken.None);

        var (url, payloadJson) = Assert.Single(discord.Sent);
        Assert.Equal(WebhookUrl, url);
        using var payload = JsonDocument.Parse(payloadJson);
        var fields = payload.RootElement.GetProperty("embeds")[0].GetProperty("fields");
        Assert.Contains(fields.EnumerateArray(),
            f => f.GetProperty("name").GetString() == "Correlation ID"
                && f.GetProperty("value").GetString() == CorrelationId);
        Assert.Contains(fields.EnumerateArray(),
            f => f.GetProperty("name").GetString() == "Route"
                && f.GetProperty("value").GetString()!.Contains("/v3/records/proj123/coll"));
        Assert.Contains(fields.EnumerateArray(),
            f => f.GetProperty("name").GetString() == "Project"
                && f.GetProperty("value").GetString() == "`proj123`");

        var mail = Assert.Single(smtp.Sent);
        Assert.Contains(CorrelationId, mail.Subject);
        Assert.Contains("InvalidOperationException", mail.Subject);
        Assert.Contains(CorrelationId, mail.Body);
        Assert.Contains("proj123", mail.Body);
    }

    [Fact]
    public async Task Fanout_NeverPutsSecretsInPayloads()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var sink = BuildSink(EnabledOptions(DisabledOptions()) with { SmtpPassword = "s3cr3t-pw" }, discord, smtp);

        await sink.NotifyAsync(SampleError(), CancellationToken.None);

        Assert.DoesNotContain("webhooks", discord.Sent[0].PayloadJson);
        Assert.DoesNotContain("s3cr3t-pw", smtp.Sent[0].Body);
        Assert.DoesNotContain("s3cr3t-pw", smtp.Sent[0].Subject);
    }

    [Fact]
    public async Task Fanout_DisabledChannelsSendNothing()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var sink = BuildSink(DisabledOptions(), discord, smtp);

        await sink.NotifyAsync(SampleError(), CancellationToken.None);

        Assert.Empty(discord.Sent);
        Assert.Empty(smtp.Sent);
    }

    [Fact]
    public async Task Fanout_DiscordFailureStillSendsSmtpAndNeverThrows()
    {
        var discord = new FakeDiscordClient { Throw = new HttpRequestException("discord down") };
        var smtp = new FakeSmtpTransport();
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp);

        await sink.NotifyAsync(SampleError(), CancellationToken.None);

        Assert.Single(smtp.Sent);
    }

    [Fact]
    public async Task Fanout_SmtpFailureNeverThrows()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport { Throw = new InvalidOperationException("smtp down") };
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp);

        await sink.NotifyAsync(SampleError(), CancellationToken.None);

        Assert.Single(discord.Sent);
    }

    [Fact]
    public async Task Fanout_StorageErrorReachesBothChannels()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp);
        var error = new NetworkStorageError("proj1", "saves", "slot0", "record.write", "SAVE_NOT_CONFIRMED", "save went sideways");

        await ((INetworkStorageErrorAlertSink)sink).NotifyAsync(error, CancellationToken.None);

        Assert.Single(discord.Sent);
        var mail = Assert.Single(smtp.Sent);
        Assert.Contains("SAVE_NOT_CONFIRMED", mail.Subject);
        Assert.Contains("slot0", mail.Body);
    }

    [Fact]
    public async Task Throttle_RepeatsAreHeldBackAndCountedInTheNextAlertOfThatKind()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var time = new ManualTime();
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp, time);
        INetworkStorageErrorAlertSink storage = sink;

        // Same project, operation, code and collection; only the player differs.
        for (var i = 0; i < 50; i++)
            await storage.NotifyAsync(new NetworkStorageError("proj1", "saves", $"player{i}", "save.unconfirmed", "SAVE_NOT_CONFIRMED", "not confirmed"), CancellationToken.None);
        Assert.Single(discord.Sent);
        Assert.Single(smtp.Sent);

        time.Now += OperatorAlertSink.RepeatWindow;
        await storage.NotifyAsync(new NetworkStorageError("proj1", "saves", "late", "save.unconfirmed", "SAVE_NOT_CONFIRMED", "not confirmed"), CancellationToken.None);

        Assert.Equal(2, discord.Sent.Count);
        Assert.Contains("49 more like this were not sent", smtp.Sent[1].Body);
    }

    [Fact]
    public async Task Throttle_AtMostTheLimitPerMinuteAndTheNextAlertSaysHowManyWereDropped()
    {
        var discord = new FakeDiscordClient();
        var smtp = new FakeSmtpTransport();
        var time = new ManualTime();
        var sink = BuildSink(EnabledOptions(DisabledOptions()), discord, smtp, time);

        // Distinct kinds, as a caller varying the collection would produce.
        for (var i = 0; i < OperatorAlertSink.MaxPerMinute + 5; i++)
            await sink.NotifyAsync(SampleError() with { Path = $"/v3/records/proj123/c{i}" }, CancellationToken.None);
        Assert.Equal(OperatorAlertSink.MaxPerMinute, discord.Sent.Count);

        time.Now += TimeSpan.FromMinutes(1);
        await sink.NotifyAsync(SampleError() with { Path = "/v3/records/proj123/after" }, CancellationToken.None);

        Assert.Equal(OperatorAlertSink.MaxPerMinute + 1, smtp.Sent.Count);
        Assert.Contains("5 other alerts were not sent", smtp.Sent[^1].Body);
    }

    [Fact]
    public void DiscordPayload_ShowsCallerTextAsCodeAndPingsNobody()
    {
        var error = SampleError() with
        {
            Message = "reason=[log in again](https://evil.example) ``` @everyone",
            Path = "/v3/records/[x](https://evil.example)",
        };

        using var payload = DiscordAlertSender.BuildPayload(error, "sbox-ns");

        var root = payload.RootElement;
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var embed = root.GetProperty("embeds")[0];
        var description = embed.GetProperty("description").GetString()!;
        Assert.StartsWith("```\n", description);
        Assert.EndsWith("\n```", description);
        // The caller's own backticks cannot close the block early.
        Assert.Equal(2, description.Split("```").Length - 1);
        var route = embed.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Route").GetProperty("value").GetString()!;
        Assert.StartsWith("`", route);
        Assert.EndsWith("`", route);
    }

    [Fact]
    public void SettingDefinitions_AlertKeysRegisteredWithSecretsFlagged()
    {
        foreach (var key in new[]
        {
            "alerts.discord.enabled", "alerts.discord.webhook_url", "alerts.discord.webhook_url_file",
            "alerts.discord.username", "alerts.smtp.enabled", "alerts.smtp.host", "alerts.smtp.port",
            "alerts.smtp.username", "alerts.smtp.password", "alerts.smtp.password_file",
            "alerts.smtp.from", "alerts.smtp.to", "alerts.smtp.use_tls",
        })
        {
            var definition = SettingDefinitions.Find(key);
            Assert.NotNull(definition);
            Assert.Equal(SettingDefinitions.AlertsFile, definition.File);
        }

        // Secrets must be redacted by `config show` (driven by this flag).
        Assert.True(SettingDefinitions.Find("alerts.discord.webhook_url")!.Secret);
        Assert.True(SettingDefinitions.Find("alerts.smtp.password")!.Secret);
        Assert.False(SettingDefinitions.Find("alerts.smtp.host")!.Secret);

        // Alerting ships disabled; CLI works through the generic mechanism.
        Assert.False((bool)SettingDefinitions.Find("alerts.discord.enabled")!.DefaultValue);
        Assert.False((bool)SettingDefinitions.Find("alerts.smtp.enabled")!.DefaultValue);
    }

    [Fact]
    public void ConfigValidation_ReportsAlertProblemsWithFileAndKey()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "alerts.toml"),
                """
                [alerts.discord]
                enabled = true

                [alerts.smtp]
                enabled = true
                host = ""
                from = ""
                to = ""
                """);
            var config = ConfigLoader.Load(dir, Path.Combine(dir, "data"), environment: _ => null);

            Assert.False(config.IsValid);
            var messages = config.Issues.Select(i => i.ToString()).ToList();
            Assert.Contains(messages, m => m.Contains("alerts.toml") && m.Contains("alerts.discord.enabled"));
            Assert.Contains(messages, m => m.Contains("alerts.toml") && m.Contains("alerts.smtp.host"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigValidation_RejectsNonHttpsWebhookAndDoubleSecrets()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "alerts.toml"),
                """
                [alerts.discord]
                webhook_url = "http://example.com/hook"
                webhook_url_file = "secrets/hook"
                """);
            var config = ConfigLoader.Load(dir, Path.Combine(dir, "data"), environment: _ => null);

            Assert.False(config.IsValid);
            var messages = config.Issues.Select(i => i.ToString()).ToList();
            Assert.Contains(messages, m => m.Contains("alerts.discord.webhook_url") && m.Contains("https://"));
            Assert.Contains(messages, m => m.Contains("not both"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AlertOptions_BindsEnvAndSecretFile()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "secrets"));
            File.WriteAllText(Path.Combine(dir, "secrets", "discord_hook"), WebhookUrl + "\n");
            File.WriteAllText(Path.Combine(dir, "alerts.toml"),
                """
                [alerts.discord]
                enabled = true
                webhook_url_file = "secrets/discord_hook"

                [alerts.smtp]
                enabled = true
                host = "mail.example.com"
                from = "sbox-ns@example.com"
                to = "ops@example.com; oncall@example.com"
                """);
            var config = ConfigLoader.Load(
                dir, Path.Combine(dir, "data"),
                environment: name => name == "NS_ALERTS__SMTP__PASSWORD" ? "env-pw" : null);
            Assert.True(config.IsValid);

            var options = AlertOptions.Bind(config);

            Assert.Equal(WebhookUrl, options.DiscordWebhookUrl);
            Assert.Equal("env-pw", options.SmtpPassword);
            Assert.Equal(["ops@example.com", "oncall@example.com"], options.SmtpTo);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static OperatorAlertSink BuildSink(AlertOptions options, FakeDiscordClient discord, FakeSmtpTransport smtp, TimeProvider? time = null)
        => new(
            new LoggingExceptionAlertSink(NullLogger<LoggingExceptionAlertSink>.Instance),
            new LoggingNetworkStorageErrorAlertSink(NullLogger<LoggingNetworkStorageErrorAlertSink>.Instance),
            new DiscordAlertSender(options, discord, NullLogger<DiscordAlertSender>.Instance),
            new SmtpAlertSender(options, smtp, NullLogger<SmtpAlertSender>.Instance),
            time ?? TimeProvider.System);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sbox-ns-alert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeDiscordClient : IDiscordWebhookClient
    {
        public List<(string Url, string PayloadJson)> Sent { get; } = [];
        public Exception? Throw { private get; set; }

        public Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Sent.Add((webhookUrl, payload.RootElement.GetRawText()));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSmtpTransport : ISmtpTransport
    {
        public sealed record SentMail(string Host, int Port, bool UseTls, string From, IReadOnlyList<string> To, string Subject, string Body);

        public List<SentMail> Sent { get; } = [];
        public Exception? Throw { private get; set; }

        public Task SendAsync(
            string host, int port, bool useTls,
            string username, string password,
            string from, IReadOnlyList<string> to,
            string subject, string body,
            CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Sent.Add(new SentMail(host, port, useTls, from, [.. to], subject, body));
            return Task.CompletedTask;
        }
    }
}
