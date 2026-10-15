using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Monitoring;
using SboxNetworkStorage.Server.Owner;

namespace SboxNetworkStorage.Server.Demo;

/// <summary>Generated monitoring examples, never measurements of the public demo host.</summary>
internal static class DemoMonitoring
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow.AddDays(-3);
    private static readonly SlowInfo Disk = new(48 * GiB, 80 * GiB, 2 * GiB, 6 * GiB, DateTimeOffset.UtcNow.AddHours(-2));

    internal static OwnerServerModel Server() => new(
        "Sample server", ".NET 10 (sample)", "Linux / arm64 (sample)", StartedAt,
        "SQLite (sample)", "3.50 (sample)", 1.2, "/srv/sbox-ns/demo-data (sample)", "sample host", 4, 8 * GiB,
        3, Sample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 500), Disk,
        new OwnerUpdateHealth("0.0.0 (sample)", "Watching", "Sample data: no real update is being watched.", null));

    internal static object Stats(int interval, long after)
    {
        interval = Math.Clamp(interval, 250, 60_000);
        var seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 500;
        var count = after >= seq ? 0 : (int)Math.Min(240, seq - Math.Max(after, seq - 240));
        var samples = new HostSample[count];
        for (var i = 0; i < samples.Length; i++) samples[i] = Sample(seq - count + 1 + i);
        return new { interval, samples, slow = new { Disk.DataFree, Disk.DataTotal, Disk.DataSize, Disk.BackupSize,
            newestBackup = Disk.NewestBackup?.ToUnixTimeMilliseconds() }, uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            sampleData = true };
    }

    internal static object History(string? range)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (range == "24h")
        {
            var rollups = new MinuteRollup[1440];
            for (var i = 0; i < rollups.Length; i++)
            {
                var sample = Sample((now - (1439 - i) * 60_000L) / 500);
                rollups[i] = new(sample.At, sample.Cpu, sample.MemoryPercent, sample.ProcessMemory,
                    sample.NetRx, sample.NetTx, sample.Requests * 120, sample.Errors * 120, sample.P95);
            }
            return new { range = "24h", rollups, sampleData = true };
        }
        var samples = new HostSample[1800];
        for (var i = 0; i < samples.Length; i++) samples[i] = Sample(now / 500 - 1799 + i);
        return new { range = "15m", samples, sampleData = true };
    }

    private static HostSample Sample(long seq)
    {
        var wave = Math.Sin(seq % 1200 * Math.PI / 120);
        return new(seq, seq * 500, 0.5, Math.Round(28 + 12 * wave, 1), Math.Round(4 + 2 * wave, 1), 0.72,
            3 * GiB, 8 * GiB, 192 * 1024 * 1024, 64 * 1024 * 1024,
            48_000 + 12_000 * wave, 128_000 + 32_000 * wave, 18, 21, 42, 0, 0, 8, 24 + 4 * wave, 12);
    }

    internal static OwnerAlertsModel Alerts(IReadOnlyList<OwnerProjectOption> projects)
    {
        ChannelInfo[] channels = [
            new("demo-email", "Operator email (sample)", "smtp", "generic", AlertSeverity.Warning, null, true, "demo", null),
            new("demo-discord", "Operations Discord (sample)", "discord", "generic", AlertSeverity.Info, null, true, "demo", null),
            new("demo-webhook", "Incident webhook (sample)", "webhook", "generic", AlertSeverity.Error, null, true, "demo", null),
        ];
        AlertRule[] rules = [
            new("demo-cpu", "High CPU (sample)", "cpu.percent", ">", 90, 120, AlertSeverity.Warning, ["demo-email"], true),
            new("demo-disk", "Low free disk (sample)", "disk.data.free.gb", "<", 5, 60, AlertSeverity.Error, ["demo-discord"], true),
            new("demo-errors", "Request errors (sample)", "http.error_rate.percent", ">", 5, 300, AlertSeverity.Error, ["demo-webhook"], true),
        ];
        MonitoringEvent[] history = [
            new(DateTimeOffset.UtcNow.AddMinutes(-12), "alert", "High CPU resolved (sample)", "CPU returned below 90%. Generated example; no notification was sent."),
            new(DateTimeOffset.UtcNow.AddMinutes(-18), "alert", "High CPU firing (sample)", "CPU exceeded 90% for two minutes. Generated example; no notification was sent."),
            new(DateTimeOffset.UtcNow.AddHours(-1), "change", "Rule added: Request errors (sample)", "5xx error rate > 5% over five minutes."),
        ];
        var statuses = new Dictionary<string, RuleStatus>
        {
            ["demo-cpu"] = new("ok", DateTimeOffset.UtcNow.AddMinutes(-12), 28),
            ["demo-disk"] = new("ok", StartedAt, 48),
            ["demo-errors"] = new("ok", StartedAt, 0),
        };
        OwnerProviderView[] providers = [
            new("cloudflare", "Cloudflare Email (sample)", "Third party", "send, forward",
                "Outbound alerts through Cloudflare Email Sending. Generated example; nothing is configured.",
                true, null, true, "The API token is valid (sample).", "alerts@mail.example.com",
                ["Email Sending subdomain 'mail'", "Console channel 'Cloudflare Email'"], false),
            new("sboxns", "sboxns.com email (sample)", "First party", "send, forward",
                "Forwarding through your sboxns.com name. Generated example; the service side is out of scope.",
                false, "Needs a tunnel or DNS identity first (sample).", false, null, null, [], false),
        ];
        return new(channels, rules, statuses, history, projects, null, null, false, Providers: providers);
    }
}
