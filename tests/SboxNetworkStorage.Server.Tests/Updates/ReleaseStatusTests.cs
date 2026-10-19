using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Updates.ReleaseStatus;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests.Updates;

/// <summary>Release status card: the schema 1 parser, the cached fetch, the settings gate and the dashboard rendering.</summary>
public sealed class ReleaseStatusTests
{
    private const string Url = "https://example.test/release-status.json";

    private const string Full = """
        {
          "schema": 1,
          "generatedAt": "2026-05-10T12:00:00Z",
          "library": {
            "published": { "revision": 423774, "version": "1.1.0", "commit": "1111111111111111111111111111111111111111", "publishedAt": "2026-05-01T09:00:00Z", "url": "https://sbox.game/sboxcool/network-storage" },
            "main": { "version": "1.2.0", "commit": "2222222222222222222222222222222222222222", "committedAt": "2026-05-09T08:00:00Z",
                      "ci": { "conclusion": "success", "runUrl": "https://github.com/sbox-cool/sbox-network-storage/actions/runs/1", "engine": "26.10.13" } },
            "pending": [ { "number": 37, "title": "Add leaderboards", "mergedAt": "2026-05-02T10:00:00Z", "url": "https://github.com/sbox-cool/sbox-network-storage/pull/37" } ],
            "pendingSince": "2026-05-02T10:00:00Z",
            "installMain": { "zipUrl": "https://github.com/sbox-cool/sbox-network-storage/archive/refs/heads/main.zip", "docsUrl": "https://github.com/sbox-cool/sbox-network-storage#install-from-main" }
          },
          "engine": { "latest": { "version": "26.10.13", "date": "2026-05-06T00:00:00Z", "url": "https://github.com/Facepunch/sbox-public/releases/tag/26.10.13" }, "libraryBehind": true, "daysBehind": 4 },
          "server": {
            "latestRelease": { "version": "0.5.4", "publishedAt": "2026-05-03T00:00:00Z", "url": "https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.5.4" },
            "main": { "commit": "3333333333333333333333333333333333333333", "committedAt": "2026-05-08T00:00:00Z" },
            "pending": [],
            "pendingSince": null
          }
        }
        """;

    private static string PendingItems(int count, string title = "Change") =>
        string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{ "number": {{i}}, "title": "{{title}} {{i}}", "mergedAt": "2026-05-02T10:00:00Z", "url": "https://github.com/sbox-cool/sbox-network-storage/pull/{{i}}" }"""));

    private static string WithLibraryPending(string items) =>
        Full.Replace("\"pending\": [ { \"number\": 37, \"title\": \"Add leaderboards\", \"mergedAt\": \"2026-05-02T10:00:00Z\", \"url\": \"https://github.com/sbox-cool/sbox-network-storage/pull/37\" } ]",
            $"\"pending\": [ {items} ]");

    private static EffectiveConfig Config(params (string Key, string Value)[] overrides) => ConfigLoader.Load(
        Path.Combine(Path.GetTempPath(), "sbox-ns-release-tests-" + Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), "sbox-ns-release-tests-" + Guid.NewGuid().ToString("N")),
        overrides.ToDictionary(o => o.Key, o => o.Value), _ => null);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(respond(request));
        }
    }

    private static ReleaseStatusService Service(HttpMessageHandler handler, bool enabled = true)
        => new(new ReleaseStatusSettings(enabled, new Uri(Url)), TimeProvider.System, NullLogger<ReleaseStatusService>.Instance, handler);

    private static async Task<string> DashboardAsync(SqliteHostFactory factory, ReleaseStatusService service)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(service)));
        using var owner = await LoggedInClientAsync(host);
        return await owner.GetStringAsync("/dashboard");
    }

    private static async Task<SqliteHostFactory> OwnerFactoryAsync()
    {
        var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        return factory;
    }

    // Parser

    [Fact]
    public void Parses_the_full_schema_1_document()
    {
        var document = ReleaseStatusParser.Parse(Full);

        Assert.Equal(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero), document.GeneratedAt);
        Assert.Equal(423774, document.Library.Published.Revision);
        Assert.Equal("1.1.0", document.Library.Published.Version);
        Assert.Equal("1.2.0", document.Library.Main.Version);
        Assert.Equal("success", document.Library.Main.Ci.Conclusion);
        Assert.Equal("26.10.13", document.Library.Main.Ci.Engine);
        var pending = Assert.Single(document.Library.Pending);
        Assert.Equal(37, pending.Number);
        Assert.Equal("Add leaderboards", pending.Title);
        Assert.Equal("https://github.com/sbox-cool/sbox-network-storage/pull/37", pending.Url!.AbsoluteUri);
        Assert.Equal("26.10.13", document.Engine.Latest.Version);
        Assert.True(document.Engine.LibraryBehind);
        Assert.Equal(4, document.Engine.DaysBehind);
        Assert.Equal("0.5.4", document.Server.LatestRelease.Version);
        Assert.Empty(document.Server.Pending);
        Assert.Null(document.Server.PendingSince);
    }

    [Fact]
    public void Missing_fields_become_null_and_sub_records_are_never_null()
    {
        var document = ReleaseStatusParser.Parse("""{ "schema": 1, "generatedAt": "2026-05-10T12:00:00Z" }""");

        Assert.NotNull(document.Library);
        Assert.Null(document.Library.Published.Version);
        Assert.Null(document.Library.Main.Ci.Conclusion);
        Assert.Null(document.Library.InstallMain.ZipUrl);
        Assert.Empty(document.Library.Pending);
        Assert.Null(document.Engine.LibraryBehind);
        Assert.Null(document.Engine.DaysBehind);
        Assert.Null(document.Server.LatestRelease.Url);
    }

    [Fact]
    public void Unknown_fields_are_ignored_so_additive_changes_stay_compatible()
    {
        var json = Full.Replace("\"schema\": 1,", "\"schema\": 1, \"futureField\": { \"anything\": true },");

        var document = ReleaseStatusParser.Parse(json);

        Assert.Equal("1.2.0", document.Library.Main.Version);
    }

    [Fact]
    public void A_schema_other_than_1_is_rejected()
    {
        var json = Full.Replace("\"schema\": 1", "\"schema\": 2");

        Assert.Throws<FormatException>(() => ReleaseStatusParser.Parse(json));
    }

    [Fact]
    public void A_document_without_a_schema_is_rejected()
    {
        Assert.Throws<FormatException>(() => ReleaseStatusParser.Parse("""{ "generatedAt": "2026-05-10T12:00:00Z" }"""));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => ReleaseStatusParser.Parse("{ not json"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/release")]
    [InlineData("data:text/html,hello")]
    public void Non_http_urls_are_dropped(string url)
    {
        var json = Full.Replace("\"url\": \"https://sbox.game/sboxcool/network-storage\"", $"\"url\": \"{url}\"");

        var document = ReleaseStatusParser.Parse(json);

        Assert.Null(document.Library.Published.Url);
    }

    [Fact]
    public void Pending_lists_are_capped_at_fifty_items()
    {
        var document = ReleaseStatusParser.Parse(WithLibraryPending(PendingItems(60)));

        Assert.Equal(ReleaseStatusParser.MaxPending, document.Library.Pending.Count);
        Assert.Equal(50, ReleaseStatusParser.MaxPending);
    }

    // Settings gate

    [Theory]
    [InlineData("true", "true", "false", true)]
    [InlineData("false", "true", "false", false)]
    [InlineData("true", "false", "false", false)]
    [InlineData("true", "true", "true", false)]
    public void The_fetch_is_gated_by_update_checks_the_card_switch_and_demo_mode(string check, string card, string demo, bool expected)
    {
        var config = Config(("updates.check", check), ("adminpanel.release_status", card), ("adminpanel.demo_read_only", demo));

        Assert.Equal(expected, ReleaseStatusSettings.From(config).Enabled);
    }

    [Fact]
    public void A_non_https_release_status_url_is_a_configuration_issue()
    {
        var config = Config(("adminpanel.release_status_url", "http://example.test/release-status.json"));

        Assert.False(config.IsValid);
        Assert.Contains(config.Issues, issue => issue.Message.Contains("release status URL", StringComparison.Ordinal));
    }

    // Service

    [Fact]
    public async Task A_disabled_service_never_requests_the_document()
    {
        var handler = new StubHandler(_ => Json(Full));
        using var service = Service(handler, enabled: false);

        await service.RefreshAsync(CancellationToken.None);
        service.RequestRefresh();

        Assert.Equal(0, handler.Requests);
        Assert.Null(service.Current);
        Assert.Null(service.LastAttemptAt);
    }

    [Fact]
    public async Task A_successful_refresh_stores_the_document_and_its_read_time()
    {
        var handler = new StubHandler(_ => Json(Full));
        using var service = Service(handler);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, handler.Requests);
        Assert.Equal("1.2.0", service.Current!.Document.Library.Main.Version);
        Assert.False(service.LastAttemptFailed);
        Assert.NotNull(service.LastAttemptAt);
    }

    [Fact]
    public async Task A_failed_refresh_with_no_copy_records_the_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var service = Service(handler);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.Current);
        Assert.True(service.LastAttemptFailed);
        Assert.NotNull(service.LastAttemptAt);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_copy()
    {
        var fail = false;
        var handler = new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(Full));
        using var service = Service(handler);
        await service.RefreshAsync(CancellationToken.None);
        var good = service.Current;

        fail = true;
        await service.RefreshAsync(CancellationToken.None);

        Assert.Same(good, service.Current);
        Assert.True(service.LastAttemptFailed);
    }

    [Fact]
    public async Task A_schema_mismatch_is_a_failure_not_an_exception()
    {
        var handler = new StubHandler(_ => Json(Full.Replace("\"schema\": 1", "\"schema\": 9")));
        using var service = Service(handler);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.Current);
        Assert.True(service.LastAttemptFailed);
    }

    [Fact]
    public async Task A_response_over_the_size_cap_is_rejected()
    {
        var padding = new string('x', ReleaseStatusService.MaxResponseBytes + 1);
        var handler = new StubHandler(_ => Json($$"""{ "schema": 1, "padding": "{{padding}}" }"""));
        using var service = Service(handler);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.Current);
        Assert.True(service.LastAttemptFailed);
    }

    // Card state

    [Fact]
    public void The_card_is_checking_before_the_first_attempt_and_unavailable_after_a_failure()
    {
        var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

        var checking = new OwnerReleaseStatusCard(null, null, null, false, now, "0.5.3");
        var unavailable = new OwnerReleaseStatusCard(null, null, now, true, now, "0.5.3");

        Assert.Equal(ReleaseCardState.Checking, checking.State);
        Assert.Equal(ReleaseCardState.Unavailable, unavailable.State);
        Assert.False(unavailable.Stale);
    }

    [Fact]
    public void A_copy_with_a_failed_latest_attempt_is_stale()
    {
        var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        var document = ReleaseStatusParser.Parse(Full);

        var card = new OwnerReleaseStatusCard(document, now, now, true, now, "0.5.3");

        Assert.Equal(ReleaseCardState.Available, card.State);
        Assert.True(card.Stale);
    }

    [Theory]
    [InlineData("0.5.3", "0.5.4", ServerReleaseState.UpdateAvailable)]
    [InlineData("0.5.4", "0.5.4", ServerReleaseState.UpToDate)]
    [InlineData("0.5.5-dev", "0.5.4", ServerReleaseState.AheadOfRelease)]
    [InlineData("0.5.3", null, ServerReleaseState.Unknown)]
    public void The_server_state_compares_the_running_build_with_the_latest_release(string running, string? latest, ServerReleaseState expected)
    {
        var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        var document = ReleaseStatusParser.Parse(latest is null
            ? Full.Replace("\"version\": \"0.5.4\"", "\"version\": null")
            : Full);

        var card = new OwnerReleaseStatusCard(document, now, now, false, now, running);

        Assert.Equal(expected, card.ServerState);
    }

    // Dashboard rendering

    [Fact]
    public async Task The_card_is_hidden_when_the_fetch_is_off()
    {
        using var factory = await OwnerFactoryAsync();

        using var host = factory.WithWebHostBuilder(_ => { });
        using var owner = await LoggedInClientAsync(host);
        var html = await owner.GetStringAsync("/dashboard");

        Assert.DoesNotContain("Library and release status", html);
    }

    [Fact]
    public async Task The_card_is_hidden_when_the_service_is_disabled()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => Json(Full)), enabled: false);

        var html = await DashboardAsync(factory, service);

        Assert.DoesNotContain("Library and release status", html);
    }

    [Fact]
    public async Task The_card_shows_checking_before_the_first_attempt()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => Json(Full)));

        var html = await DashboardAsync(factory, service);

        Assert.Contains("Checking the library release feed.", html);
    }

    [Fact]
    public async Task The_card_shows_unavailable_when_the_feed_cannot_be_read()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.Contains("Release status is unavailable.", html);
    }

    [Fact]
    public async Task The_card_renders_the_library_server_and_engine_facts()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => Json(Full)));
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.Contains("sbox.game release", html);
        Assert.Contains("revision 423774", html);
        Assert.Contains("1.2.0", html);
        Assert.Contains("222222", html);
        Assert.Contains("View CI run", html);
        Assert.Contains("#37", html);
        Assert.Contains("Add leaderboards", html);
        Assert.Contains("Oldest merged 2026-05-02", html);
        Assert.Contains("Wait for the sbox.game release.", html);
        Assert.Contains("Install from main now.", html);
        Assert.Contains("Download main.zip", html);
        Assert.Contains("Latest s&amp;box release: 26.10.13", html);
        Assert.Contains("The sbox.game library release is 4 days behind the latest s&amp;box release.", html);
        Assert.Contains("No changes on main since the latest release.", html);
        Assert.Contains("Feed generated 2026-05-10 12:00 UTC.", html);
        Assert.DoesNotContain("<details class=\"release-more\"", html);
    }

    [Fact]
    public async Task Changes_beyond_the_first_five_sit_in_a_details_block()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => Json(WithLibraryPending(PendingItems(7)))));
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.Contains("<details class=\"release-more\"", html);
        Assert.Contains("Show 2 more", html);
        Assert.Contains("Change 7", html);
    }

    [Fact]
    public async Task Feed_text_is_html_encoded_and_never_runs_as_markup()
    {
        using var factory = await OwnerFactoryAsync();
        var hostile = WithLibraryPending("""{ "number": 9, "title": "<script>alert(1)</script>", "mergedAt": "2026-05-02T10:00:00Z", "url": "javascript:alert(2)" }""");
        using var service = Service(new StubHandler(_ => Json(hostile)));
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("javascript:alert(2)", html);
    }

    [Fact]
    public async Task A_stale_copy_says_so()
    {
        using var factory = await OwnerFactoryAsync();
        var fail = false;
        using var service = Service(new StubHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(Full)));
        await service.RefreshAsync(CancellationToken.None);
        fail = true;
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.Contains("The latest check failed.", html);
        Assert.Contains("Library and release status", html);
    }

    [Fact]
    public async Task An_empty_library_queue_says_nothing_is_waiting()
    {
        using var factory = await OwnerFactoryAsync();
        using var service = Service(new StubHandler(_ => Json(WithLibraryPending(""))));
        await service.RefreshAsync(CancellationToken.None);

        var html = await DashboardAsync(factory, service);

        Assert.Contains("No merged changes since the sbox.game release.", html);
        Assert.DoesNotContain("Install from main now.", html);
    }
}
