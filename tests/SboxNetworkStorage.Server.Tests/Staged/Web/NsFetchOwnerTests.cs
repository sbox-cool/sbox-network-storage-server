using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Owner dashboard script-fetch settings: the host allow-list, allow-all-public, and the blocked-calls panel.</summary>
public sealed class NsFetchOwnerTests
{
    private const string BlockedHost = "blocked-host.example.net";
    private const string PrivateAddress = "10.1.2.3";
    private static readonly Regex AllowAllPublicInput = new("<input[^>]*name=\"fetchAllowAllPublic\"[^>]*>");

    private static async Task<(SqliteHostFactory Factory, HttpClient Owner, SelfHostProject Project, string Url)> SetupAsync()
    {
        var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Script fetch");
        return (factory, owner, project, $"/dashboard/projects/{project.ProjectId}");
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    private static async Task<JsonElement> StoredProjectAsync(SqliteHostFactory factory, SelfHostProject project)
    {
        var store = factory.Services.GetRequiredService<INetworkStorageStore>();
        return (await store.ReadProjectAsync(project.ProjectId, CancellationToken.None))!.Value;
    }

    private static string[] StoredHosts(JsonElement project) =>
        project.TryGetProperty("fetchAllowedHosts", out var hosts) && hosts.ValueKind == JsonValueKind.Array
            ? hosts.EnumerateArray().Select(host => host.GetString()!).ToArray()
            : Array.Empty<string>();

    private static bool AllowAllPublicChecked(string page)
    {
        var input = AllowAllPublicInput.Match(page);
        Assert.True(input.Success, "The fetchAllowAllPublic checkbox is missing from the project page.");
        return input.Value.Contains(" checked", StringComparison.Ordinal);
    }

    private static int CountOf(string page, string text) => Regex.Matches(page, Regex.Escape(text)).Count;

    private static async Task CaptureBlockedAsync(SqliteHostFactory factory, string projectId, string host, string reason)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EndpointErrorReporter>()
            .CaptureFetchBlockedAsync(projectId, "prices", "GET", null, host, reason, CancellationToken.None);
    }

    [Fact]
    public async Task AllowListIsNormalizedDedupedAndSavedFromProjectSettings()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        using (var saved = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"),
            ("fetchAllowedHosts", "API.example.com.\n*.Example.org\n\napi.example.com")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        Assert.Equal(new[] { "api.example.com", "*.example.org" }, StoredHosts(await StoredProjectAsync(factory, project)));
        var textarea = Regex.Match(await owner.GetStringAsync(url), "<textarea name=\"fetchAllowedHosts\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
        Assert.True(textarea.Success, "The fetchAllowedHosts textarea is missing from the project page.");
        Assert.Equal("api.example.com\n*.example.org", System.Net.WebUtility.HtmlDecode(textarea.Groups[1].Value).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AllowAllPublicIsSavedAndShownAsChecked()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        Assert.False(AllowAllPublicChecked(await owner.GetStringAsync(url)));

        using (var on = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"), ("fetchAllowAllPublic", "true")))
            Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        Assert.True((await StoredProjectAsync(factory, project)).GetProperty("fetchAllowAllPublic").GetBoolean());
        Assert.True(AllowAllPublicChecked(await owner.GetStringAsync(url)));

        using (var off = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch")))
            Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        Assert.False((await StoredProjectAsync(factory, project)).TryGetProperty("fetchAllowAllPublic", out var stored)
            && stored.ValueKind == JsonValueKind.True);
        Assert.False(AllowAllPublicChecked(await owner.GetStringAsync(url)));
    }

    [Fact]
    public async Task InvalidAllowListIsRefusedAndNothingIsWritten()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        using (var saved = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"), ("fetchAllowedHosts", "api.example.com")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using var invalid = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"),
            ("fetchAllowedHosts", "https://evil.example.net\nother.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("is not a valid host", body);
        Assert.Contains("https://evil.example.net", body);

        Assert.Equal(new[] { "api.example.com" }, StoredHosts(await StoredProjectAsync(factory, project)));
    }

    [Fact]
    public async Task AllowListOverTheLimitIsRefused()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        var tooMany = string.Join("\n", Enumerable.Range(0, FetchHostRules.MaxEntries + 1).Select(index => $"h{index}.example.com"));
        using var refused = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"), ("fetchAllowedHosts", tooMany));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains($"Keep the fetch allow-list to {FetchHostRules.MaxEntries} hosts or fewer.", await refused.Content.ReadAsStringAsync());
        Assert.Empty(StoredHosts(await StoredProjectAsync(factory, project)));
    }

    [Fact]
    public async Task AllowThisHostAddsOneExactHostWithoutDuplicates()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        using (var first = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch-allow-host"), ("host", "API.Example.com")))
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using (var again = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch-allow-host"), ("host", "api.example.com")))
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);

        Assert.Equal(new[] { "api.example.com" }, StoredHosts(await StoredProjectAsync(factory, project)));
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("")]
    public async Task AllowThisHostRefusesWildcardsAndEmptyHosts(string entry)
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        using var refused = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch-allow-host"), ("host", entry));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Only an exact host can be allowed from a blocked call.", await refused.Content.ReadAsStringAsync());
        Assert.Empty(StoredHosts(await StoredProjectAsync(factory, project)));
    }

    [Fact]
    public async Task BlockedCallsListRefusedHostsAndOfferAllowOnlyForAllowListRefusals()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        Assert.Contains("No blocked calls in the recent window.", await owner.GetStringAsync(url));

        await CaptureBlockedAsync(factory, project.ProjectId, BlockedHost, "host_not_allowed");
        await CaptureBlockedAsync(factory, project.ProjectId, PrivateAddress, "private_address");

        var page = await owner.GetStringAsync(url);
        Assert.Contains("Recently blocked calls", page);
        Assert.Contains($"<code>{BlockedHost}</code>", page);
        Assert.Contains($"<code>{PrivateAddress}</code>", page);
        Assert.Contains("<code>prices</code>", page);
        Assert.Contains("<td>host_not_allowed</td>", page);
        Assert.Contains("<td>private_address</td>", page);
        Assert.Equal(1, CountOf(page, "name=\"tab\" value=\"fetch-allow-host\""));
        Assert.Contains($"name=\"host\" value=\"{BlockedHost}\"", page);
        Assert.DoesNotContain($"name=\"host\" value=\"{PrivateAddress}\"", page);
    }

    [Fact]
    public async Task AllowingTheHostDropsItsRowButKeepsPrivateRefusals()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        await CaptureBlockedAsync(factory, project.ProjectId, BlockedHost, "host_not_allowed");
        await CaptureBlockedAsync(factory, project.ProjectId, PrivateAddress, "private_address");

        using (var allowed = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch-allow-host"), ("host", BlockedHost)))
            Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);

        var page = await owner.GetStringAsync(url);
        Assert.DoesNotContain($"<code>{BlockedHost}</code>", page);
        Assert.Contains($"<code>{PrivateAddress}</code>", page);
        Assert.Equal(0, CountOf(page, "value=\"fetch-allow-host\""));
    }

    [Fact]
    public async Task AllowAllPublicDropsAllowListRefusalsButKeepsPrivateRefusals()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;
        await CaptureBlockedAsync(factory, project.ProjectId, BlockedHost, "host_not_allowed");
        await CaptureBlockedAsync(factory, project.ProjectId, PrivateAddress, "private_address");

        using (var on = await PostFormAsync(owner, url, url + "/settings", ("tab", "fetch"), ("fetchAllowAllPublic", "true")))
            Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);

        var page = await owner.GetStringAsync(url);
        Assert.DoesNotContain($"<code>{BlockedHost}</code>", page);
        Assert.Contains($"<code>{PrivateAddress}</code>", page);
        Assert.Equal(0, CountOf(page, "value=\"fetch-allow-host\""));
    }
}

/// <summary>The reporter's ns.fetch blocked-call capture: the archive row shape, the per-host throttle, and tolerance of sink failures.</summary>
public sealed class NsFetchReporterTests
{
    private const string ProjectId = "6c22075ca036481e";
    private const string Slug = "prices";
    private const string Host = "evil.example.net";

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (EndpointErrorReporter Reporter, FakeArchive Archive, FakeAlertSink Alerts) Build(
        TimeProvider? clock = null, bool throwOnCapture = false, bool throwOnNotify = false)
    {
        var archive = new FakeArchive(throwOnCapture);
        var alerts = new FakeAlertSink(throwOnNotify);
        var throttle = new EndpointConflictReportThrottle(clock ?? TimeProvider.System);
        var reporter = new EndpointErrorReporter(archive, alerts, throttle, NullLogger<EndpointErrorReporter>.Instance);
        return (reporter, archive, alerts);
    }

    [Fact]
    public async Task BlockedCall_ArchivesHostAndReasonTagsAndAlerts()
    {
        var (reporter, archive, alerts) = Build();

        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "POST", null, Host, "host_not_allowed", CancellationToken.None);

        var captured = Assert.Single(archive.Captured);
        var alerted = Assert.Single(alerts.Notified);
        Assert.Equal(captured.Id, alerted.Id);
        Assert.Equal(502, captured.StatusCode);
        Assert.Equal("EndpointFetch.blocked", captured.Classification);
        Assert.Equal("ns.fetch blocked host evil.example.net (host_not_allowed)", captured.Message);
        Assert.Equal("POST", captured.Method);
        Assert.Equal(ProjectId, captured.ProjectId);
        Assert.Null(captured.SteamId);
        Assert.Contains($"/v3/endpoints/{ProjectId}/{Slug}", captured.Path);
        Assert.Contains("endpoint-shadow", captured.Tags!);
        Assert.Contains("fetch-host:evil.example.net", captured.Tags!);
        Assert.Contains("fetch-reason:host_not_allowed", captured.Tags!);
        Assert.Null(captured.StackTrace);
    }

    [Fact]
    public async Task RepeatedHostAndReason_IsThrottledUntilTheWindowElapses()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (reporter, archive, alerts) = Build(clock);

        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);
        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);
        Assert.Single(archive.Captured);
        Assert.Single(alerts.Notified);

        clock.Advance(EndpointConflictReportThrottle.Window - TimeSpan.FromSeconds(1));
        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);
        Assert.Single(archive.Captured);

        clock.Advance(TimeSpan.FromSeconds(2));
        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);
        Assert.Equal(2, archive.Captured.Count);
        Assert.Equal(2, alerts.Notified.Count);
    }

    [Fact]
    public async Task DifferentHostOrReason_IsReportedSeparately()
    {
        var (reporter, archive, alerts) = Build(new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);
        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, "other.example.net", "host_not_allowed", CancellationToken.None);
        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "private_address", CancellationToken.None);

        Assert.Equal(3, archive.Captured.Count);
        Assert.Equal(3, alerts.Notified.Count);
    }

    [Fact]
    public async Task ArchiveFailure_DoesNotThrowAndStillAlerts()
    {
        var (reporter, archive, alerts) = Build(throwOnCapture: true);

        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);

        Assert.Empty(archive.Captured);
        Assert.Single(alerts.Notified);
    }

    [Fact]
    public async Task AlertFailure_DoesNotPreventArchive()
    {
        var (reporter, archive, alerts) = Build(throwOnNotify: true);

        await reporter.CaptureFetchBlockedAsync(ProjectId, Slug, "GET", null, Host, "host_not_allowed", CancellationToken.None);

        Assert.Single(archive.Captured);
        Assert.Empty(alerts.Notified);
    }

    // ── Fakes ──

    private sealed class FakeArchive(bool throwOnCapture = false) : IErrorArchive
    {
        public List<CapturedErrorDto> Captured { get; } = [];

        public Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            if (throwOnCapture) throw new InvalidOperationException("archive down");
            Captured.Add(capturedError);
            return Task.FromResult(capturedError);
        }

        public Task<IReadOnlyList<CapturedErrorDto>> ListRecentAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CapturedErrorDto>>(Captured);

        public Task<InternalErrorArchiveResult> ListAsync(InternalErrorArchiveQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new InternalErrorArchiveResult(Captured, false, "fake"));

        public Task<CapturedErrorDto?> GetAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(Captured.FirstOrDefault(e => e.Id == id));

        public Task<bool> MarkResolvedAsync(string id, long resolvedByUserId, string? note, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> MarkAlertDeliveredAsync(string id, bool delivered, int statusCode, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class FakeAlertSink(bool throwOnNotify = false) : IExceptionAlertSink
    {
        public List<CapturedErrorDto> Notified { get; } = [];

        public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            if (throwOnNotify) throw new InvalidOperationException("discord down");
            Notified.Add(capturedError);
            return Task.CompletedTask;
        }
    }
}

/// <summary>The shipped ns.d.ts declares the ns.fetch surface the host serves, and no ns.secrets API.</summary>
public sealed class NsFetchTypingsTests
{
    private static readonly Regex InterfaceBlock = new(@"^interface (\w+)(?: extends \w+)? \{\r?\n(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex MemberLine = new(@"^  (\w+)\??:", RegexOptions.Multiline);

    private static string BlockBody(string declarations, string name) =>
        InterfaceBlock.Matches(declarations).Single(block => block.Groups[1].Value == name).Groups[2].Value;

    private static string[] MemberNames(string body) =>
        MemberLine.Matches(body).Select(member => member.Groups[1].Value).ToArray();

    [Fact]
    public void NsDeclaresFetchReturningANullableResponse()
    {
        var ns = BlockBody(EndpointTypings.StaticDeclarations(), "Ns");
        Assert.Contains("  fetch(url: string, options?: FetchOptions): FetchResponse | null;", ns, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchOptionsMembersMatchTheHostOptions()
    {
        var members = MemberNames(BlockBody(EndpointTypings.StaticDeclarations(), "FetchOptions"));
        Assert.Equal(new[] { "method", "headers", "body", "timeoutMs" }, members);
    }

    [Fact]
    public void FetchResponseMembersMatchTheHostResponse()
    {
        var members = MemberNames(BlockBody(EndpointTypings.StaticDeclarations(), "FetchResponse"));
        Assert.Equal(new[] { "status", "ok", "headers", "body", "json" }, members);
    }

    [Fact]
    public void FetchErrorExtendsErrorAndCarriesTheCode()
    {
        var declarations = EndpointTypings.StaticDeclarations();
        Assert.Matches(@"(?m)^interface FetchError extends Error \{", declarations);
        Assert.Equal(new[] { "code" }, MemberNames(BlockBody(declarations, "FetchError")));
    }

    [Fact]
    public void SecretsApiIsReadOnlyGet()
    {
        var declarations = EndpointTypings.StaticDeclarations();
        Assert.Matches(@"(?m)^  secrets: NsSecrets;\r?$", BlockBody(declarations, "Ns"));
        var methods = Regex.Matches(BlockBody(declarations, "NsSecrets"), @"^  (\w+)\(", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "get" }, methods);
    }
}
