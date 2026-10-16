using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Dry-run response model rules on typed history rows: stage mapping, saturation,
/// suppression, reviewed-state exclusion, lookback boundary, eligibility, and the
/// <c>wouldApply</c> size bound. No host or store is involved.
/// </summary>
public sealed class IntegrityResponseTests
{
    private const long NowMs = 1_800_000_000_000L;
    private const long DayMs = 86_400_000L;
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0, "flag", 0)]
    [InlineData(1, "rate-limit", 1)]
    [InlineData(2, "shadow", 2)]
    [InlineData(3, "block-writes", 3)]
    [InlineData(4, "block-all", 4)]
    [InlineData(5, "block-all", 4)]
    public void Each_prior_count_maps_to_its_stage_and_saturates_at_four(int prior, string stage, int counted)
    {
        var decision = IntegrityResponseEvaluator.Decide(Spaced(prior, 600), NowMs, Day, Window);

        Assert.Equal(ResponseEligibility.Eligible, decision.Eligibility);
        Assert.Equal(stage, decision.Stage);
        Assert.Equal(counted, decision.CountedFirings);
        Assert.False(decision.Approximate);
    }

    [Fact]
    public void Repeat_inside_the_window_of_the_older_flag_is_suppressed()
    {
        // Newer flag 20 s after the older one: inside the 60 s window, so only the older counts.
        var decision = IntegrityResponseEvaluator.Decide(new List<ResponseHistoryRow> { Row(100), Row(120) },
            NowMs, Day, Window);

        Assert.Equal(1, decision.CountedFirings);
        Assert.Equal("rate-limit", decision.Stage);
    }

    [Fact]
    public void Gap_exactly_equal_to_the_window_is_not_suppressed()
    {
        var decision = IntegrityResponseEvaluator.Decide(new List<ResponseHistoryRow> { Row(100), Row(160) },
            NowMs, Day, Window);

        Assert.Equal(2, decision.CountedFirings);
        Assert.Equal("shadow", decision.Stage);
    }

    [Fact]
    public void Reviewed_rows_are_excluded_from_the_count()
    {
        var rows = new List<ResponseHistoryRow>
        {
            Row(600, "confirmed"),
            Row(1200, "dismissed"),
            Row(1800, "reviewed-legitimate"),
            Row(2400, "open"),
        };

        var decision = IntegrityResponseEvaluator.Decide(rows, NowMs, Day, Window);

        Assert.Equal(2, decision.CountedFirings);
        Assert.Equal("shadow", decision.Stage);
    }

    [Fact]
    public void Reviewed_row_still_suppresses_a_newer_repeat_inside_the_window()
    {
        // The open flag is 20 s after the dismissed one: suppressed by it, and the dismissed row is not counted.
        var decision = IntegrityResponseEvaluator.Decide(new List<ResponseHistoryRow> { Row(100), Row(120, "dismissed") },
            NowMs, Day, Window);

        Assert.Equal(0, decision.CountedFirings);
        Assert.Equal("flag", decision.Stage);
    }

    [Fact]
    public void Lookback_boundary_is_inclusive()
    {
        var atBoundary = IntegrityResponseEvaluator.Decide(
            new List<ResponseHistoryRow> { new(NowMs - DayMs, "open") }, NowMs, Day, Window);
        var beyondBoundary = IntegrityResponseEvaluator.Decide(
            new List<ResponseHistoryRow> { new(NowMs - DayMs - 1, "open") }, NowMs, Day, Window);

        Assert.Equal(1, atBoundary.CountedFirings);
        Assert.Equal(0, beyondBoundary.CountedFirings);
        Assert.False(beyondBoundary.Approximate);
    }

    [Fact]
    public void Full_bounded_read_of_reviewed_rows_is_approximate()
    {
        // Sixteen dismissed rows, all inside the lookback: the read is full and the boundary was not reached.
        var decision = IntegrityResponseEvaluator.Decide(Spaced(16, 600, "dismissed"), NowMs, Day, Window);

        Assert.Equal(0, decision.CountedFirings);
        Assert.Equal("flag", decision.Stage);
        Assert.True(decision.Approximate);
    }

    [Fact]
    public void Full_bounded_read_that_reaches_the_boundary_is_not_approximate()
    {
        // Sixteen dismissed rows two hours apart: the read reaches rows beyond the 24 h boundary.
        var decision = IntegrityResponseEvaluator.Decide(Spaced(16, 7200, "dismissed"), NowMs, Day, Window);

        Assert.Equal(0, decision.CountedFirings);
        Assert.False(decision.Approximate);
    }

    [Fact]
    public void Saturation_takes_precedence_over_approximation()
    {
        var decision = IntegrityResponseEvaluator.Decide(Spaced(16, 600), NowMs, Day, Window);

        Assert.Equal(4, decision.CountedFirings);
        Assert.Equal("block-all", decision.Stage);
        Assert.False(decision.Approximate);
    }

    [Theory]
    [InlineData("anonymous", ResponseEligibility.Anonymous)]
    [InlineData("", ResponseEligibility.Blank)]
    [InlineData("   ", ResponseEligibility.Blank)]
    [InlineData("76561198000000001", ResponseEligibility.Eligible)]
    public void Anonymous_and_blank_identities_are_ineligible(string steamId, ResponseEligibility expected)
        => Assert.Equal(expected, IntegrityResponseEvaluator.EligibilityOf(steamId));

    [Theory]
    [InlineData("call-rate", 60)]
    [InlineData("value-delta", 60)]
    [InlineData("anti-rollback-conflict", 600)]
    [InlineData("direct-write-attempt", 600)]
    [InlineData("revision-tampering", 0)]
    public void Detector_windows_follow_the_design(string detector, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), IntegrityResponseEvaluator.WindowFor(detector, TimeSpan.Zero));

    [Fact]
    public void Configured_conflict_window_replaces_the_default_for_conflict_detectors()
        => Assert.Equal(TimeSpan.FromMinutes(5),
            IntegrityResponseEvaluator.WindowFor("anti-rollback-conflict", TimeSpan.FromMinutes(5)));

    [Fact]
    public void Oversized_evidence_drops_optional_fields_and_keeps_stage_and_detector()
    {
        var decision = new ResponseDecision(ResponseEligibility.Eligible, "block-all", 4, true);

        var evidence = IntegrityResponseEvaluator.BuildWouldApply(decision, "call-rate", new string('x', 5000), Window);

        Assert.True(Encoding.UTF8.GetByteCount(evidence.ToJsonString()) <= IntegrityResponseEvaluator.WouldApplyMaxBytes);
        Assert.Equal("block-all", evidence["stage"]!.GetValue<string>());
        Assert.Equal("call-rate", evidence["detector"]!.GetValue<string>());
        Assert.Null(evidence["hookSource"]);
    }

    [Fact]
    public void Small_evidence_keeps_every_field()
    {
        var decision = new ResponseDecision(ResponseEligibility.Eligible, "shadow", 2, true);

        var evidence = IntegrityResponseEvaluator.BuildWouldApply(decision, "call-rate", "endpoint.executed", Window);

        Assert.Equal("endpoint.executed", evidence["hookSource"]!.GetValue<string>());
        Assert.Equal(60, evidence["windowSeconds"]!.GetValue<long>());
        Assert.Equal(IntegrityResponseEvaluator.WouldApplySchemaVersion, evidence["schemaVersion"]!.GetValue<int>());
        Assert.Equal(2, evidence["priorCount"]!.GetValue<int>());
        Assert.True(evidence["approximate"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(null, null, "off", 24)]
    [InlineData("off", null, "off", 24)]
    [InlineData("enforce", null, "off", 24)]
    [InlineData("dry-run", null, "dry-run", 24)]
    [InlineData("dry-run", "0", "dry-run", 24)]
    [InlineData("dry-run", "169", "dry-run", 24)]
    [InlineData("dry-run", "abc", "dry-run", 24)]
    [InlineData("dry-run", "1", "dry-run", 1)]
    [InlineData("dry-run", "168", "dry-run", 168)]
    [InlineData("dry-run", "12", "dry-run", 12)]
    public void Response_settings_parse_to_a_mode_and_a_bounded_lookback(string? mode, string? lookback,
        string expectedMode, int expectedHours)
    {
        var saved = new Dictionary<string, string>();
        if (mode is not null) saved["response_mode"] = mode;
        if (lookback is not null) saved["response_lookback_hours"] = lookback;

        var settings = IntegritySettings.From(saved, Array.Empty<string>());

        Assert.Equal(expectedMode, settings.ResponseMode);
        Assert.Equal(TimeSpan.FromHours(expectedHours), settings.ResponseLookback);
    }

    private static ResponseHistoryRow Row(long ageSeconds, string state = "open")
        => new(NowMs - ageSeconds * 1000, state);

    private static List<ResponseHistoryRow> Spaced(int count, int stepSeconds, string state = "open")
        => Enumerable.Range(1, count).Select(i => Row(i * stepSeconds, state)).ToList();
}

/// <summary>
/// Dry-run response model against the real history store and the real integrity service,
/// run once per relational provider. Covers the store boundaries, the no-history-read off path,
/// and the non-effects of dry-run on alerts, blocks and flags.
/// </summary>
public abstract class IntegrityResponseStoreTests<TFactory>(TFactory factory) : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Player = "76561198000000001";
    private const string OtherPlayer = "76561198000000002";
    private const long TenMinutesMs = 600_000L;
    private const long DayMs = 86_400_000L;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    [SkippableFact]
    public async Task Dry_run_flags_match_off_baseline_and_carry_the_stage_in_order()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var off = await factory.CreateProjectAsync("Integrity off baseline");
        var dry = await factory.CreateProjectAsync("Integrity dry run");
        await InstallIntegrityAsync(factory, off.ProjectId, Settings("off"));
        await InstallIntegrityAsync(factory, dry.ProjectId, Settings("dry-run"));

        await RunCallsAsync(factory, [off.ProjectId, dry.ProjectId], calls: 6);

        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var offFlags = await CallRateFlagsAsync(store, off.ProjectId);
        var dryFlags = await CallRateFlagsAsync(store, dry.ProjectId);
        Assert.Equal(3, offFlags.Count);
        Assert.Equal(offFlags.Count, dryFlags.Count);
        Assert.All(offFlags, flag => Assert.False(Evidence(flag).TryGetProperty("wouldApply", out _)));
        Assert.Equal(offFlags.Select(State), dryFlags.Select(State));
        Assert.Equal(new[] { "flag", "rate-limit", "rate-limit" },
            dryFlags.Select(flag => Evidence(flag).GetProperty("wouldApply").GetProperty("stage").GetString()));
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData("enforce")]
    public async Task Off_path_never_reads_response_history(string? mode)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var probe = new ReadProbe();
        using var probed = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IModuleDataStore));
            services.Remove(original);
            services.AddSingleton<IModuleDataStore>(sp =>
                new ProbedModuleStore((IModuleDataStore)original.ImplementationFactory!(sp), probe));
        }));
        var project = await probed.CreateProjectAsync("Off reads nothing");
        await InstallIntegrityAsync(probed, project.ProjectId, Settings(mode));

        await RunCallsAsync(probed, [project.ProjectId], calls: 2);

        var inner = ((ProbedModuleStore)probed.Services.GetRequiredService<IModuleDataStore>()).Inner;
        var flags = await inner.ListFlagsAsync(project.ProjectId, null, null, null, null, 10, CancellationToken.None);
        Assert.Single(flags);
        Assert.Equal(0, probe.Calls);
        Assert.False(Evidence(flags[0]).TryGetProperty("wouldApply", out _));
    }

    [SkippableFact]
    public async Task Dry_run_seeded_history_sets_the_stage_on_the_next_flag_without_blocking()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Integrity seeded history");
        await InstallIntegrityAsync(factory, project.ProjectId, Settings("dry-run"));
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var nowMs = NowMs();
        for (var i = 1; i <= 4; i++)
            await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "open");

        // The first call stays under the one-per-minute limit; the second fires.
        await RunCallsAsync(factory, [project.ProjectId], calls: 2);

        var flags = await CallRateFlagsAsync(store, project.ProjectId, Player);
        Assert.Equal(5, flags.Count);
        var wouldApply = Evidence(flags[^1]).GetProperty("wouldApply");
        Assert.Equal("block-all", wouldApply.GetProperty("stage").GetString());
        Assert.Equal(4, wouldApply.GetProperty("priorCount").GetInt32());
        Assert.False(wouldApply.GetProperty("approximate").GetBoolean());
        Assert.Empty(await store.ListBlocksAsync(project.ProjectId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Flag_and_alert_sends_the_same_alerts_as_off()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var sink = new RecordingAlertSink();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IIntegrityAlertSink>(sink)));
        var off = await host.CreateProjectAsync("Alert off");
        var dry = await host.CreateProjectAsync("Alert dry run");
        await InstallIntegrityAsync(host, off.ProjectId, Settings("off", "flag-and-alert"));
        await InstallIntegrityAsync(host, dry.ProjectId, Settings("dry-run", "flag-and-alert"));

        await RunCallsAsync(host, [off.ProjectId, dry.ProjectId], calls: 6);

        var offAlerts = sink.CountFor(off.ProjectId);
        Assert.True(offAlerts > 0);
        Assert.Equal(offAlerts, sink.CountFor(dry.ProjectId));
    }

    [SkippableFact]
    public async Task Flag_and_block_writes_creates_only_the_existing_block()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IIntegrityAlertSink>(new RecordingAlertSink())));
        var off = await host.CreateProjectAsync("Block off");
        var dry = await host.CreateProjectAsync("Block dry run");
        await InstallIntegrityAsync(host, off.ProjectId, Settings("off", "flag-and-block-writes"));
        await InstallIntegrityAsync(host, dry.ProjectId, Settings("dry-run", "flag-and-block-writes"));

        await RunCallsAsync(host, [off.ProjectId, dry.ProjectId], calls: 4);

        var store = host.Services.GetRequiredService<IModuleDataStore>();
        Assert.Single(await store.ListBlocksAsync(off.ProjectId, CancellationToken.None));
        Assert.Single(await store.ListBlocksAsync(dry.ProjectId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Dry_run_five_firings_create_no_blocks_or_alerts()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var sink = new RecordingAlertSink();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IIntegrityAlertSink>(sink)));
        var project = await host.CreateProjectAsync("Dry run no effects");
        await InstallIntegrityAsync(host, project.ProjectId, Settings("dry-run"));

        await RunCallsAsync(host, [project.ProjectId], calls: 10);

        var store = host.Services.GetRequiredService<IModuleDataStore>();
        Assert.Equal(5, (await CallRateFlagsAsync(store, project.ProjectId)).Count);
        Assert.Empty(await store.ListBlocksAsync(project.ProjectId, CancellationToken.None));
        Assert.Equal(0, sink.CountFor(project.ProjectId));
    }

    [SkippableFact]
    public async Task Store_history_beyond_the_read_bound_is_approximate()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store read bound");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        // Sixteen newest are reviewed, so the bounded read finds nothing countable; four older open rows lie beyond the read.
        for (var i = 1; i <= 16; i++)
            await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "dismissed");
        for (var i = 17; i <= 20; i++)
            await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "open");

        var decision = await Evaluate(store, project.ProjectId, Player, at);

        Assert.Equal(0, decision.CountedFirings);
        Assert.Equal("flag", decision.Stage);
        Assert.True(decision.Approximate);
    }

    [SkippableFact]
    public async Task Store_four_open_firings_saturate_without_approximation()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store saturation");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        for (var i = 1; i <= 4; i++)
            await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "open");

        var decision = await Evaluate(store, project.ProjectId, Player, at);

        Assert.Equal(4, decision.CountedFirings);
        Assert.Equal("block-all", decision.Stage);
        Assert.False(decision.Approximate);
    }

    [SkippableFact]
    public async Task Store_lookback_boundary_is_inclusive()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store lookback boundary");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - DayMs, "open");
        await SeedFlagAsync(store, project.ProjectId, OtherPlayer, IntegrityDetectors.CallRate, nowMs - DayMs - 1, "open");

        var atBoundary = await Evaluate(store, project.ProjectId, Player, at);
        var beyondBoundary = await Evaluate(store, project.ProjectId, OtherPlayer, at);

        Assert.Equal(1, atBoundary.CountedFirings);
        Assert.Equal("rate-limit", atBoundary.Stage);
        Assert.Equal(0, beyondBoundary.CountedFirings);
        Assert.Equal("flag", beyondBoundary.Stage);
    }

    [SkippableFact]
    public async Task Store_suppression_uses_the_detector_window()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store suppression");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        // Player: 20 s apart, inside the window, so the newer is suppressed.
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - 100_000, "open");
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - 120_000, "open");
        // OtherPlayer: exactly 60 s apart, on the window edge, so neither is suppressed.
        await SeedFlagAsync(store, project.ProjectId, OtherPlayer, IntegrityDetectors.CallRate, nowMs - 100_000, "open");
        await SeedFlagAsync(store, project.ProjectId, OtherPlayer, IntegrityDetectors.CallRate, nowMs - 160_000, "open");

        var suppressed = await Evaluate(store, project.ProjectId, Player, at);
        var edge = await Evaluate(store, project.ProjectId, OtherPlayer, at);

        Assert.Equal(1, suppressed.CountedFirings);
        Assert.Equal("rate-limit", suppressed.Stage);
        Assert.Equal(2, edge.CountedFirings);
        Assert.Equal("shadow", edge.Stage);
    }

    [SkippableFact]
    public async Task Store_reviewed_states_are_excluded_from_the_count()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store reviewed states");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - 600_000, "dismissed");
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - 1_200_000, "confirmed");
        await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - 1_800_000, "reviewed-legitimate");

        var decision = await Evaluate(store, project.ProjectId, Player, at);

        Assert.Equal(1, decision.CountedFirings);
        Assert.Equal("rate-limit", decision.Stage);
    }

    [SkippableFact]
    public async Task Store_ineligible_identities_are_not_counted()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store ineligible");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        for (var i = 1; i <= 4; i++)
            await SeedFlagAsync(store, project.ProjectId, "anonymous", IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "open");

        var anonymous = await Evaluate(store, project.ProjectId, "anonymous", at);
        var blank = await Evaluate(store, project.ProjectId, "   ", at);

        Assert.Equal(ResponseEligibility.Anonymous, anonymous.Eligibility);
        Assert.Null(anonymous.Stage);
        Assert.Equal(0, anonymous.CountedFirings);
        Assert.Equal(ResponseEligibility.Blank, blank.Eligibility);
        Assert.Null(blank.Stage);
    }

    [SkippableFact]
    public async Task Store_concurrent_evaluations_of_one_player_agree()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Store concurrency");
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        var at = NowUtc();
        var nowMs = at.ToUnixTimeMilliseconds();
        for (var i = 1; i <= 4; i++)
            await SeedFlagAsync(store, project.ProjectId, Player, IntegrityDetectors.CallRate, nowMs - i * TenMinutesMs, "open");

        var decisions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Evaluate(store, project.ProjectId, Player, at)));

        Assert.All(decisions, decision =>
        {
            Assert.Equal(4, decision.CountedFirings);
            Assert.Equal("block-all", decision.Stage);
        });
    }

    private static Task<ResponseDecision> Evaluate(IModuleDataStore store, string projectId, string steamId,
        DateTimeOffset at)
        => IntegrityResponseEvaluator.EvaluateAsync(store, projectId, steamId, IntegrityDetectors.CallRate,
            Window, Lookback, at, CancellationToken.None);

    private static DateTimeOffset NowUtc()
        => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static long NowMs() => NowUtc().ToUnixTimeMilliseconds();

    private static string? State(JsonElement flag) => flag.GetProperty("state").GetString();

    private static Dictionary<string, string> Settings(string? mode, string? autoAction = null)
    {
        var settings = new Dictionary<string, string> { ["max_calls_per_minute"] = "1" };
        if (mode is not null) settings["response_mode"] = mode;
        if (autoAction is not null) settings["auto_action"] = autoAction;
        return settings;
    }

    private static async Task InstallIntegrityAsync(SelfHostFactory host, string projectId,
        IReadOnlyDictionary<string, string> settings)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var result = await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, projectId, "integrity",
            settings, CancellationToken.None);
        Assert.True(result.Ok);
    }

    private static async Task RunCallsAsync(SelfHostFactory host, string[] projectIds, int calls)
    {
        var service = host.Services.GetRequiredService<IntegrityModuleService>();
        for (var call = 0; call < calls; call++)
        {
            foreach (var projectId in projectIds)
                await service.OnEventAsync(ModuleHookDispatcher.Describe("endpoint.executed", projectId,
                    DateTimeOffset.UtcNow, steamId: Player, endpointSlug: "economy_earn"), CancellationToken.None);
            // Distinct millisecond timestamps keep the chronological order of flags stable.
            await Task.Delay(10);
        }
    }

    private static async Task SeedFlagAsync(IModuleDataStore store, string projectId, string steamId,
        string detector, long createdAtUnixMs, string state)
        => await store.InsertFlagAsync(projectId, Guid.NewGuid().ToString("N"), createdAtUnixMs, steamId,
            detector, "medium", state, null, null, CancellationToken.None);

    private static async Task<List<JsonElement>> CallRateFlagsAsync(IModuleDataStore store, string projectId,
        string? steamId = null)
    {
        var rows = await store.ListFlagsAsync(projectId, null, IntegrityDetectors.CallRate, null, steamId, 100,
            CancellationToken.None);
        return rows.OrderBy(row => row.GetProperty("created_at_unix_ms").GetInt64()).ToList();
    }

    private static JsonElement Evidence(JsonElement flag)
    {
        var raw = flag.GetProperty("evidence_json");
        return raw.ValueKind == JsonValueKind.String ? JsonDocument.Parse(raw.GetString()!).RootElement : raw;
    }

    /// <summary>Counts reads of the flag history. Used to prove the off path never reads it.</summary>
    private sealed class ReadProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Record() => Interlocked.Increment(ref _calls);
    }

    /// <summary>Delegates every call to the real store, except that a history read records and fails.</summary>
    private sealed class ProbedModuleStore(IModuleDataStore inner, ReadProbe probe) : IModuleDataStore
    {
        /// <summary>The real store behind the probe, for the reads a test performs itself.</summary>
        public IModuleDataStore Inner => inner;

        public Task InsertFlagAsync(string projectId, string flagId, long createdAtUnixMs, string steamId,
            string detector, string severity, string state, string? note, JsonElement? evidenceJson, CancellationToken ct)
            => inner.InsertFlagAsync(projectId, flagId, createdAtUnixMs, steamId, detector, severity, state, note,
                evidenceJson, ct);

        public Task<IReadOnlyList<JsonElement>> ListFlagsAsync(string projectId, string? state, string? detector,
            string? severity, string? steamId, int limit, CancellationToken ct)
        {
            probe.Record();
            return Task.FromException<IReadOnlyList<JsonElement>>(
                new InvalidOperationException("Response history was read on an off-mode path."));
        }

        public Task<bool> UpdateFlagStateAsync(string projectId, string flagId, string state, string? note,
            CancellationToken ct) => inner.UpdateFlagStateAsync(projectId, flagId, state, note, ct);

        public Task<long> CountOpenFlagsAsync(string projectId, CancellationToken ct)
            => inner.CountOpenFlagsAsync(projectId, ct);

        public Task UpsertBlockAsync(string projectId, string steamId, string scope, string? reason,
            long createdAtUnixMs, long? expiresAtUnixMs, string? createdBy, CancellationToken ct)
            => inner.UpsertBlockAsync(projectId, steamId, scope, reason, createdAtUnixMs, expiresAtUnixMs, createdBy, ct);

        public Task<JsonElement?> ReadBlockAsync(string projectId, string steamId, CancellationToken ct)
            => inner.ReadBlockAsync(projectId, steamId, ct);

        public Task<IReadOnlyList<JsonElement>> ListBlocksAsync(string projectId, CancellationToken ct)
            => inner.ListBlocksAsync(projectId, ct);

        public Task DeleteBlockAsync(string projectId, string steamId, CancellationToken ct)
            => inner.DeleteBlockAsync(projectId, steamId, ct);

        public Task<IReadOnlyList<JsonElement>> ListSessionsAsync(string projectId, string? steamId, int limit,
            CancellationToken ct) => inner.ListSessionsAsync(projectId, steamId, limit, ct);

        public Task InsertActionLogAsync(string projectId, string steamId, long createdAtUnixMs, string entryId,
            string? endpointSlug, long? revisionId, string? outcome, JsonElement? changedFieldsJson,
            JsonElement? bodyJson, CancellationToken ct)
            => inner.InsertActionLogAsync(projectId, steamId, createdAtUnixMs, entryId, endpointSlug, revisionId,
                outcome, changedFieldsJson, bodyJson, ct);

        public Task<IReadOnlyList<JsonElement>> ListActionLogAsync(string projectId, string? steamId,
            string? endpointSlug, string? outcome, long? fromUnixMs, long? toUnixMs, int limit, CancellationToken ct)
            => inner.ListActionLogAsync(projectId, steamId, endpointSlug, outcome, fromUnixMs, toUnixMs, limit, ct);

        public Task<long> PurgeActionLogBeforeAsync(long beforeUnixMs, CancellationToken ct)
            => inner.PurgeActionLogBeforeAsync(beforeUnixMs, ct);

        public Task<long> PurgeProjectActionLogAsync(string projectId, long beforeUnixMs, CancellationToken ct)
            => inner.PurgeProjectActionLogAsync(projectId, beforeUnixMs, ct);
    }

    /// <summary>Records the projects that received an integrity alert.</summary>
    private sealed class RecordingAlertSink : IIntegrityAlertSink
    {
        private readonly ConcurrentBag<string> _projects = new();

        public Task NotifyFlagAsync(string projectId, string detector, string severity, string steamId,
            string summary, CancellationToken ct)
        {
            _projects.Add(projectId);
            return Task.CompletedTask;
        }

        public int CountFor(string projectId) => _projects.Count(id => id == projectId);
    }
}

public sealed class SqliteIntegrityResponseStoreTests(SqliteHostFactory factory)
    : IntegrityResponseStoreTests<SqliteHostFactory>(factory);

public sealed class PostgresIntegrityResponseStoreTests(PostgresHostFactory factory)
    : IntegrityResponseStoreTests<PostgresHostFactory>(factory);
