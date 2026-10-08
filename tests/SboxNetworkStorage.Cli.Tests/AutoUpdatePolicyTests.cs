using System.Net;
using System.Text;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>Gating rules of <c>sbox-ns update --auto</c> and the channel-aware release feed.</summary>
public sealed class AutoUpdatePolicyTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly string EmptyFolder = Path.Combine(Path.GetTempPath(), "sbox-ns-policy-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("03:00-05:00", "02:59", false)]
    [InlineData("03:00-05:00", "03:00", true)]
    [InlineData("03:00-05:00", "04:59", true)]
    [InlineData("03:00-05:00", "05:00", false)]
    [InlineData("22:00-02:00", "21:59", false)]
    [InlineData("22:00-02:00", "22:00", true)]
    [InlineData("22:00-02:00", "00:00", true)]
    [InlineData("22:00-02:00", "01:59", true)]
    [InlineData("22:00-02:00", "02:00", false)]
    [InlineData("00:00-24:00", "00:00", true)]
    [InlineData("00:00-24:00", "23:59", true)]
    public void Window_start_is_inclusive_end_exclusive_and_wraps_midnight(string window, string time, bool open)
    {
        Assert.True(UpdateWindow.TryParse(window, out var parsed));
        var now = Day + TimeSpan.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(open, parsed.Contains(now));
        Assert.Equal(open, AutoUpdatePolicy.EvaluateSchedule(Settings(window: window), now).Install);
    }

    [Theory]
    [InlineData("")]
    [InlineData("03:00")]
    [InlineData("3:00-5:00")]
    [InlineData("03:00-03:00")]
    [InlineData("24:00-05:00")]
    [InlineData("03:60-05:00")]
    [InlineData("03:00-24:01")]
    [InlineData("03:00-05:00-07:00")]
    public void Invalid_windows_are_rejected_by_config_validation(string window)
    {
        Assert.False(UpdateWindow.TryParse(window, out _));
        var config = ConfigLoader.Load(EmptyFolder, EmptyFolder,
            new Dictionary<string, string> { ["updates.window"] = window }, _ => null);
        Assert.Contains(config.Issues, issue => issue.Message.Contains("updates.window", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_installed_without_the_opt_in_even_inside_the_window()
    {
        var decision = AutoUpdatePolicy.EvaluateSchedule(Settings(autoInstall: false, window: "00:00-24:00"), Day);

        Assert.False(decision.Install);
        Assert.Contains("auto_install", decision.Reason);
    }

    [Theory]
    [InlineData("stable", -1, 24 * 3600, true)]
    [InlineData("stable", -1, 24 * 3600 - 1, false)]
    [InlineData("canary", -1, 0, true)]
    [InlineData("canary", 6, 6 * 3600 - 1, false)]
    [InlineData("stable", 0, 0, true)]
    public void Release_age_is_measured_from_promotion_with_channel_defaults(string channel, int configuredAge, int ageSeconds, bool install)
    {
        var settings = AutoUpdateSettings.FromConfig(Config(new() { ["updates.channel"] = channel, ["updates.min_release_age_hours"] = configuredAge.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var release = Release("1.1.0", promotedAt: Day.AddSeconds(-ageSeconds), publishedAt: Day.AddYears(-1));

        var decision = AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", release, failedVersion: null, Day);

        Assert.Equal(install, decision.Install);
    }

    [Fact]
    public void Release_age_falls_back_to_published_at_and_skips_when_unknown()
    {
        var settings = Settings();
        Assert.True(AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", Release("1.1.0", publishedAt: Day.AddHours(-25)), null, Day).Install);
        Assert.False(AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", Release("1.1.0", publishedAt: Day.AddHours(-23)), null, Day).Install);
        Assert.False(AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", Release("1.1.0"), null, Day).Install);
    }

    [Fact]
    public void A_held_channel_installs_only_its_pinned_version_and_never_downgrades()
    {
        var settings = Settings();
        var heldOlder = Release("1.1.0", promotedAt: Day.AddDays(-3), held: true);
        var skipped = AutoUpdatePolicy.EvaluateRelease(settings, "1.2.0", heldOlder, null, Day);
        Assert.False(skipped.Install);
        Assert.Contains("held at 1.1.0", skipped.Reason);

        var heldNewer = Release("1.2.1", promotedAt: Day.AddDays(-3), held: true);
        var installed = AutoUpdatePolicy.EvaluateRelease(settings, "1.2.0", heldNewer, null, Day);
        Assert.True(installed.Install);
        Assert.Contains("1.2.1", installed.Reason);
    }

    [Fact]
    public void A_version_that_already_failed_is_not_retried_but_a_newer_one_is()
    {
        var settings = Settings();
        Assert.False(AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", Release("1.1.0", promotedAt: Day.AddDays(-2)), "1.1.0", Day).Install);
        Assert.True(AutoUpdatePolicy.EvaluateRelease(settings, "1.0.0", Release("1.1.1", promotedAt: Day.AddDays(-2)), "1.1.0", Day).Install);
    }

    [Fact]
    public void Instances_sharing_a_binary_must_agree_and_all_opt_in()
    {
        var stable = Settings();
        var combined = AutoUpdatePolicy.Combine([("a", stable), ("b", stable with { MinReleaseAgeHours = 48 })]);
        Assert.True(combined.AutoInstall);
        Assert.Equal(48, combined.MinReleaseAgeHours);

        Assert.False(AutoUpdatePolicy.Combine([("a", stable), ("b", stable with { AutoInstall = false })]).AutoInstall);
        Assert.Throws<InvalidOperationException>(() => AutoUpdatePolicy.Combine([("a", stable), ("b", stable with { Channel = "canary" })]));
        UpdateWindow.TryParse("01:00-02:00", out var other);
        Assert.Throws<InvalidOperationException>(() => AutoUpdatePolicy.Combine([("a", stable), ("b", stable with { Window = other })]));
    }

    [Fact]
    public async Task Feed_is_requested_for_the_configured_channel_and_parses_hold_and_promotion()
    {
        var handler = new RecordingHandler(_ => Json("""{"version":"1.4.0","channel":"canary","promotedAt":"2026-10-08T12:00:00Z","held":true,"publishedAt":"2026-10-07T00:00:00Z"}"""));
        using var http = new HttpClient(handler);
        var feed = new ReleaseFeed(http, Config(new() { ["updates.channel"] = "canary" }));

        var release = await feed.GetFromFeedAsync(CancellationToken.None);

        Assert.Equal("https://sboxcool.com/api/network-storage/releases/latest?channel=canary", Assert.Single(handler.Requests));
        Assert.Equal("1.4.0", release.Version);
        Assert.True(release.Held);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), release.PromotedAt);
    }

    [Fact]
    public async Task Unreachable_feed_falls_back_to_the_newest_github_stable_release_for_notices_only()
    {
        var handler = new RecordingHandler(url => url.Contains("sboxcool.com", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("""{"tag_name":"v1.3.0","prerelease":false,"html_url":"https://example.invalid/r","assets":[]}"""));
        using var http = new HttpClient(handler);
        var feed = new ReleaseFeed(http, Config(new() { ["updates.channel"] = "canary" }));

        var release = await feed.GetReleaseAsync(null, CancellationToken.None);

        Assert.Equal("1.3.0", release.Version);
        Assert.Equal("https://api.github.com/repos/sbox-cool/sbox-network-storage-server/releases/latest", handler.Requests[^1]);
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetFromFeedAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"version":"1.4.0","channel":"canary"}""")]
    [InlineData("""{"version":"1.4.0-rc.1","channel":"stable"}""")]
    [InlineData("""{"version":"1.4.0","prerelease":true}""")]
    public async Task Feed_rejects_wrong_channels_and_prerelease_candidates(string body)
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(body)));
        var feed = new ReleaseFeed(http, Config(new()));

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.GetFromFeedAsync(CancellationToken.None));
    }

    private static AutoUpdateSettings Settings(bool autoInstall = true, string window = "03:00-05:00")
    {
        Assert.True(UpdateWindow.TryParse(window, out var parsed));
        return new AutoUpdateSettings(autoInstall, AutoUpdateSettings.StableChannel, parsed, 24);
    }

    private static ReleaseInfo Release(string version, DateTimeOffset? promotedAt = null, DateTimeOffset? publishedAt = null, bool held = false)
        => new(version, null, false, false, null, publishedAt, PromotedAt: promotedAt, Held: held);

    private static EffectiveConfig Config(Dictionary<string, string> overrides)
    {
        var config = ConfigLoader.Load(EmptyFolder, EmptyFolder, overrides, _ => null);
        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        return config;
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }
}
