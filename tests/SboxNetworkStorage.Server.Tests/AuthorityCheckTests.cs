using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;
using static SboxNetworkStorage.Application.NetworkStorage.AuthorityAnalyzer;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Hosting profile plus the advisory authority check (never on the request path).</summary>
public sealed class AuthorityCheckTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private const string UnguardedWrite = """{"steps":[{"type":"read","collection":"c"},{"type":"write","collection":"c","key":"k"}]}""";
    private const string GuardedWrite = """{"steps":[{"type":"condition","check":{"field":"x","op":">","value":0}},{"type":"write","collection":"c","key":"k"}]}""";

    private static IReadOnlyList<AuthorityFinding> Run(string? profile, CollectionFact[]? collections = null, EndpointFact[]? endpoints = null, bool secretUsed = false)
        => Analyze(profile, collections ?? [], endpoints ?? [], secretUsed);

    [Theory]
    [InlineData("player-hosted", "player-hosted", "Player-hosted")]
    [InlineData("dedicated", "dedicated", "Dedicated")]
    [InlineData("hybrid", "hybrid", "Hybrid")]
    [InlineData(null, "unset", "Not set")]
    [InlineData("garbage", "unset", "Not set")]
    public void Unknown_or_missing_profiles_read_as_unset(string? stored, string normalized, string display)
    {
        Assert.Equal(normalized, WorkspaceProjectProfiles.NormalizeHostingProfile(stored));
        Assert.Equal(display, WorkspaceProjectProfiles.DisplayName(stored));
    }

    [Fact]
    public void Public_collections_and_unguarded_writes_are_flagged_and_guarded_ones_are_not()
    {
        var findings = Run("hybrid",
            [new("wallet", "public"), new("miners", "endpoint")],
            [new("mine", true, false, Json(UnguardedWrite)), new("safe", true, false, Json(GuardedWrite)), new("off", false, false, Json(UnguardedWrite))]);

        Assert.Equal(["direct-write-collection:collection wallet", "unvalidated-writes:endpoint mine"],
            findings.Select(f => $"{f.Id}:{f.Target}").Order().ToArray());
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Fix)));
    }

    [Theory]
    [InlineData("player-hosted", true)]
    [InlineData("unset", false)]
    [InlineData("dedicated", false)]
    [InlineData("hybrid", false)]
    public void Secret_key_findings_only_apply_to_player_hosted_projects(string profile, bool flagged)
    {
        var ids = Run(profile, endpoints: [new("admin", true, true, null)], secretUsed: true).Select(f => f.Id).Order().ToArray();

        Assert.Equal(flagged ? ["secret-key-on-data-plane", "unreachable-secret-endpoint"] : [], ids);
    }

    [Fact]
    public async Task Dashboard_create_stores_the_profile_and_the_overview_lists_findings()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);

        var home = await owner.GetStringAsync("/dashboard");
        using var created = await owner.PostAsync("/dashboard/projects",
            Form(("name", "Authority game"), ("hostingProfile", "hybrid"), ("__RequestVerificationToken", Csrf(home))));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var url = created.Headers.Location!.ToString();

        var clean = Decode(await owner.GetStringAsync(url));
        Assert.Contains("Hosting profile: <strong>Hybrid</strong>", clean);
        Assert.Contains("No client-writable game data found", clean);

        const string wallet = "id: wallet\nname: wallet\ncollectionType: per-steamid\naccessMode: public\nschema:\n  type: object\n  properties:\n    coins: { type: number, default: 5 }\n";
        const string mine = "id: mine\nslug: mine\nmethod: POST\nenabled: true\nsteps:\n  - id: add\n    type: write\n    collection: wallet\n    key: \"{{steamId}}\"\n    ops:\n      - { op: inc, path: coins, value: 1 }\nresponse:\n  status: 200\n  body: { ok: true }\n";
        foreach (var (kind, source) in new[] { ("collection", wallet), ("endpoint", mine) })
        {
            var page = await owner.GetStringAsync($"{url}/resources/{kind}");
            using var saved = await owner.PostAsync($"{url}/resources/{kind}", Form(("definition", source), ("__RequestVerificationToken", Csrf(page))));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        }

        var flagged = Decode(await owner.GetStringAsync(url));
        Assert.Contains("<code>collection wallet</code>", flagged);
        Assert.Contains("<code>endpoint mine</code>", flagged);
        Assert.DoesNotContain("No client-writable game data found", flagged);

        // The hub card reflects the stored profile, and the settings form changes it.
        Assert.Contains("Hybrid", Decode(await owner.GetStringAsync("/dashboard")));
        var settings = await owner.GetStringAsync(url);
        using var saveProfile = await owner.PostAsync(url + "/settings",
            Form(("tab", "project"), ("name", "Authority game"), ("description", ""), ("hostingProfile", "dedicated"), ("__RequestVerificationToken", Csrf(settings))));
        Assert.Equal(HttpStatusCode.Redirect, saveProfile.StatusCode);
        Assert.Contains("Hosting profile: <strong>Dedicated</strong>", Decode(await owner.GetStringAsync(url)));
    }

    [Fact]
    public async Task Projects_created_without_a_profile_read_as_not_set_and_settings_ignore_bad_values()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("No profile");
        var url = $"/dashboard/projects/{project.ProjectId}";

        Assert.Contains("Hosting profile: <strong>Not set</strong>", Decode(await owner.GetStringAsync(url)));

        var page = await owner.GetStringAsync(url);
        await owner.PostAsync(url + "/settings",
            Form(("tab", "project"), ("name", "No profile"), ("description", ""), ("hostingProfile", "<script>"), ("__RequestVerificationToken", Csrf(page))));
        Assert.Contains("Hosting profile: <strong>Not set</strong>", Decode(await owner.GetStringAsync(url)));

        await using var scope = factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, default);
        Assert.Null(access!.Project.HostingProfile);
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);
}
