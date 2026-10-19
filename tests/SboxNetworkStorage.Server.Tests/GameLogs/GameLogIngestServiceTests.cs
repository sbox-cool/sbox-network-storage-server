using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.GameLogs;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.GameLogs;

public sealed class GameLogIngestServiceTests
{
    private const string Player = "76561198000000001";
    private const string Victim = "76561198000000099";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();

    private static GameLogIngestContext Context(
        GameLogSettings? settings = null,
        bool trusted = false,
        string? verifiedActor = Player,
        double random = 0.0)
        => new(
            ProjectId: "p1",
            Now: Now,
            Policy: new GameLogPolicy(settings ?? new GameLogSettings(Enabled: true)),
            Trusted: trusted,
            VerifiedActorSteamId: verifiedActor,
            IsSteamId: s => s.Length == 17 && s.All(char.IsAsciiDigit),
            NextRandom: () => random);

    private static JsonElement Batch(string json) => JsonDocument.Parse(json).RootElement;

    private static GameLogIngestOutcome Normalize(string json, GameLogIngestContext context)
        => GameLogEntryNormalizer.Normalize(Batch(json), context);

    private static string RejectCode(string json, GameLogIngestContext context)
    {
        var outcome = Normalize(json, context);
        Assert.Empty(outcome.Rows);
        return Assert.Single(outcome.Rejected).Code;
    }

    [Fact]
    public void Public_key_actor_field_is_rejected_per_entry_and_never_stored()
    {
        var outcome = Normalize($"[{{\"event\":\"client.join\",\"actor\":\"{Victim}\"}}]", Context());

        Assert.Empty(outcome.Rows);
        var rejection = Assert.Single(outcome.Rejected);
        Assert.Equal(0, rejection.Index);
        Assert.Equal("ACTOR_NOT_PERMITTED", rejection.Code);
    }

    [Fact]
    public void Public_key_entry_without_actor_is_stored_with_the_session_verified_player()
    {
        var outcome = Normalize("[{\"event\":\"client.join\"}]", Context());

        var row = Assert.Single(outcome.Rows).Row;
        Assert.Equal(Player, row.ActorSteamId);
        Assert.Empty(outcome.Rejected);
    }

    [Fact]
    public void Trusted_caller_actor_is_honoured_when_valid_and_rejected_when_malformed()
    {
        var honoured = Normalize($"[{{\"event\":\"admin.kick\",\"actor\":\"{Victim}\"}}]", Context(trusted: true));
        Assert.Equal(Victim, Assert.Single(honoured.Rows).Row.ActorSteamId);

        Assert.Equal("INVALID_ACTOR", RejectCode("[{\"event\":\"admin.kick\",\"actor\":\"nope\"}]", Context(trusted: true)));
    }

    [Fact]
    public void Trusted_caller_without_actor_uses_the_on_behalf_of_player()
    {
        var outcome = Normalize("[{\"event\":\"admin.kick\"}]", Context(trusted: true, verifiedActor: Victim));

        Assert.Equal(Victim, Assert.Single(outcome.Rows).Row.ActorSteamId);
    }

    [Fact]
    public void Public_key_callers_may_only_emit_client_chat_and_allow_listed_events()
    {
        Assert.Equal("EVENT_NOT_ALLOWED", RejectCode("[{\"event\":\"admin.kick\"}]", Context()));

        var allowed = new GameLogSettings(Enabled: true, AllowedEvents: ["match.*"]);
        Assert.Single(Normalize("[{\"event\":\"match.start\"}]", Context(allowed)).Rows);
        Assert.Equal("EVENT_NOT_ALLOWED", RejectCode("[{\"event\":\"matchmaking.x\"}]", Context(allowed)));

        Assert.Single(Normalize("[{\"event\":\"admin.kick\"}]", Context(trusted: true)).Rows);
    }

    [Fact]
    public void Chat_stream_is_off_by_default_and_accepts_entries_once_enabled()
    {
        Assert.Equal("STREAM_DISABLED", RejectCode("[{\"event\":\"chat.say\"}]", Context()));

        var enabled = new GameLogSettings(Enabled: true, Streams: new Dictionary<string, GameLogStreamSettings>
        {
            ["chat"] = new(Enabled: true),
        });
        var row = Assert.Single(Normalize("[{\"event\":\"chat.say\",\"message\":\"gg\"}]", Context(enabled)).Rows).Row;
        Assert.Equal("chat", row.Stream);
    }

    [Fact]
    public void Stream_is_derived_from_the_event_and_an_explicit_stream_must_be_known()
    {
        Assert.Equal("audit", Assert.Single(Normalize("[{\"event\":\"command.reset\"}]", Context(trusted: true)).Rows).Row.Stream);
        Assert.Equal("default", Assert.Single(Normalize("[{\"event\":\"match.end\"}]", Context(trusted: true)).Rows).Row.Stream);
        Assert.Equal("INVALID_STREAM", RejectCode("[{\"event\":\"match.end\",\"stream\":\"bogus\"}]", Context(trusted: true)));
    }

    [Fact]
    public void Timestamps_outside_the_future_skew_or_retention_window_are_rejected()
    {
        var future = NowMs + GameLogPolicy.MaxFutureSkewMs + 1;
        var tooOld = NowMs - 31L * 86_400_000;

        Assert.Equal("TIMESTAMP_OUT_OF_WINDOW", RejectCode($"[{{\"event\":\"match.end\",\"ts\":{future}}}]", Context(trusted: true)));
        Assert.Equal("TIMESTAMP_OUT_OF_WINDOW", RejectCode($"[{{\"event\":\"match.end\",\"ts\":{tooOld}}}]", Context(trusted: true)));

        var inside = Normalize($"[{{\"event\":\"match.end\",\"ts\":{NowMs - 86_400_000}}}]", Context(trusted: true));
        Assert.Equal(NowMs - 86_400_000, Assert.Single(inside.Rows).Row.TsUnixMs);
    }

    [Fact]
    public void Invalid_event_names_and_levels_are_rejected_with_typed_codes()
    {
        Assert.Equal("INVALID_EVENT", RejectCode("[{\"event\":\"Bad Event\"}]", Context(trusted: true)));
        Assert.Equal("INVALID_EVENT", RejectCode("[{}]", Context(trusted: true)));
        Assert.Equal("INVALID_LEVEL", RejectCode("[{\"event\":\"match.end\",\"level\":\"fatal\"}]", Context(trusted: true)));
        Assert.Equal("INVALID_ENTRY", RejectCode("[\"not-an-object\"]", Context(trusted: true)));
    }

    [Fact]
    public void Message_longer_than_the_limit_is_truncated_and_reported_not_silently_cut()
    {
        var message = new string('a', GameLogPolicy.MaxMessageLength + 900);
        var outcome = Normalize($"[{{\"event\":\"match.end\",\"message\":\"{message}\"}}]", Context(trusted: true));

        var ingested = Assert.Single(outcome.Rows);
        Assert.Equal(0, ingested.Index);
        Assert.True(ingested.Row.Message.Length <= GameLogPolicy.MaxMessageLength);
        Assert.EndsWith(GameLogAttributes.TruncationSuffix, ingested.Row.Message, StringComparison.Ordinal);
        Assert.Equal([0], outcome.Truncated);
    }

    [Fact]
    public void Entry_over_the_byte_budget_is_rejected_as_entry_too_large()
    {
        var big = new string('x', 1024);
        var attrs = string.Join(",", Enumerable.Range(0, 7).Select(i => $"\"k{i}\":\"{big}\""));
        var message = new string('m', GameLogPolicy.MaxMessageLength);

        Assert.Equal("ENTRY_TOO_LARGE",
            RejectCode($"[{{\"event\":\"match.end\",\"message\":\"{message}\",\"attrs\":{{{attrs}}}}}]", Context(trusted: true)));
    }

    [Fact]
    public void Invalid_target_is_rejected_and_valid_target_is_kept()
    {
        Assert.Equal("INVALID_TARGET", RejectCode("[{\"event\":\"match.end\",\"target\":\"abc\"}]", Context(trusted: true)));
        Assert.Equal(Victim, Assert.Single(Normalize($"[{{\"event\":\"match.end\",\"target\":\"{Victim}\"}}]", Context(trusted: true)).Rows).Row.TargetSteamId);
    }

    [Fact]
    public void Sampling_drops_are_counted_as_sampled_not_rejected()
    {
        var sampled = new GameLogSettings(Enabled: true, Streams: new Dictionary<string, GameLogStreamSettings>
        {
            ["default"] = new(Sampling: 0.5),
        });

        var dropped = Normalize("[{\"event\":\"match.end\"}]", Context(sampled, trusted: true, random: 0.9));
        Assert.Empty(dropped.Rows);
        Assert.Empty(dropped.Rejected);
        Assert.Equal(1, dropped.Sampled);

        var kept = Normalize("[{\"event\":\"match.end\"}]", Context(sampled, trusted: true, random: 0.1));
        Assert.Single(kept.Rows);
        Assert.Equal(0, kept.Sampled);
    }

    [Fact]
    public void Batch_indexes_stay_aligned_with_the_submitted_array()
    {
        var outcome = Normalize(
            $"[{{\"event\":\"client.a\"}},{{\"event\":\"admin.kick\"}},{{\"event\":\"client.b\",\"actor\":\"{Victim}\"}}]",
            Context());

        Assert.Equal([0], outcome.Rows.Select(r => r.Index));
        Assert.Equal([(1, "EVENT_NOT_ALLOWED"), (2, "ACTOR_NOT_PERMITTED")], outcome.Rejected.Select(r => (r.Index, r.Code)));
    }

    [Fact]
    public void Tokenizer_lowercases_splits_on_punctuation_and_dedupes()
    {
        Assert.Equal(["red", "team", "2"], GameLogTokenizer.Tokenize("Red-Team, red 2"));
        Assert.Equal(["ärger", "café"], GameLogTokenizer.Tokenize("Ärger café"));
        Assert.Empty(GameLogTokenizer.Tokenize(""));
        Assert.Equal("hello world", GameLogTokenizer.SearchText("Hello, WORLD hello"));
    }

    [Fact]
    public void Attribute_numbers_have_one_canonical_text_form()
    {
        Assert.Equal("1", GameLogAttributes.CanonicalNumber("1.0"));
        Assert.Equal("7", GameLogAttributes.CanonicalNumber("007"));
        Assert.Equal("-2.5", GameLogAttributes.CanonicalNumber("-2.50"));
        Assert.Equal("0", GameLogAttributes.CanonicalNumber("0"));
        Assert.Null(GameLogAttributes.CanonicalNumber("1e20"));
        Assert.Null(GameLogAttributes.CanonicalNumber("0.00001"));
    }

    [Fact]
    public void Attributes_reject_non_objects_and_reformat_ignores_key_order()
    {
        var array = JsonDocument.Parse("[1]").RootElement;
        Assert.Equal("INVALID_ATTRS", GameLogAttributes.Canonicalize(array).RejectCode);

        Assert.Equal(
            GameLogAttributes.Reformat("{\"b\":1,\"a\":2}"),
            GameLogAttributes.Reformat("{\"a\":2,\"b\":1}"));
    }

    [Fact]
    public void Sanitize_removes_nul_characters()
    {
        Assert.Equal("ab", GameLogAttributes.Sanitize("a\0b"));
    }

    [Fact]
    public void Search_parser_rejects_unknown_and_repeated_parameters()
    {
        var policy = new GameLogPolicy(new GameLogSettings(Enabled: true));
        Assert.Equal("UNKNOWN_PARAMETER", Parse(policy, ("nope", "1")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("level", "info"), ("level", "warn")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("attr.bad-key", "x")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("limit", "0")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("limit", "501")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("player", "abc")).ErrorCode);
        Assert.Equal("INVALID_CURSOR", Parse(policy, ("cursor", "garbage")).ErrorCode);
    }

    [Fact]
    public void Search_parser_defaults_to_enabled_streams_and_keeps_filters()
    {
        var policy = new GameLogPolicy(new GameLogSettings(Enabled: true));
        var result = Parse(policy,
            ("player", Player), ("attr.team", "red"), ("attr.score", "1.0"), ("q", "Victory now"), ("event", "match.end"));

        Assert.True(result.Ok);
        var query = result.Query!;
        Assert.Equal(["default", "audit"], query.Streams);
        Assert.Equal(Player, query.PlayerSteamId);
        Assert.Equal("match.end", query.Event);
        Assert.Equal(["victory", "now"], query.SearchTokens);
        Assert.Equal([new GameLogAttrFilter("team", "red"), new GameLogAttrFilter("score", "1")], query.Attrs);
        Assert.Equal(GameLogSearchParser.DefaultLimit, query.Limit);
    }

    [Fact]
    public void Search_parser_rejects_disabled_streams_explicitly_requested()
    {
        var policy = new GameLogPolicy(new GameLogSettings(Enabled: true));
        Assert.Equal("STREAM_DISABLED", Parse(policy, ("stream", "chat")).ErrorCode);
        Assert.Equal("INVALID_QUERY", Parse(policy, ("from", "2024-01-02T00:00:00Z"), ("to", "2024-01-01T00:00:00Z")).ErrorCode);
    }

    [Fact]
    public void Settings_form_parses_bounds_and_keeps_current_values_for_invalid_input()
    {
        Assert.Null(GameLogSettingsForm.ParseRetention("0"));
        Assert.Null(GameLogSettingsForm.ParseRetention("3651"));
        Assert.Equal(30, GameLogSettingsForm.ParseRetention("30"));
        Assert.Null(GameLogSettingsForm.ParseSampling("0"));
        Assert.Null(GameLogSettingsForm.ParseSampling("1.5"));
        Assert.Equal(0.25, GameLogSettingsForm.ParseSampling("0.25"));
        Assert.Null(GameLogSettingsForm.ParseRateLimit("0"));
        Assert.Equal(["match.*", "scoreboard.update"], GameLogSettingsForm.ParseAllowedEvents("match.*, scoreboard.update\nbad name"));

        var current = new GameLogSettings(Enabled: true, Streams: new Dictionary<string, GameLogStreamSettings>
        {
            ["default"] = new(RetentionDays: 60, Sampling: 0.5),
        }, RateLimitPerMinute: 500);

        var applied = GameLogSettingsForm.Apply(current, key => key switch
        {
            "gameLogsEnabled" => "on",
            "gameLogs_default_enabled" => "on",
            "gameLogs_default_retention" => "not-a-number",
            "gameLogs_default_sampling" => "9",
            "gameLogsRateLimitPerMinute" => "-4",
            _ => null,
        });

        Assert.True(applied.Enabled);
        Assert.Equal(60, applied.Streams!["default"].RetentionDays);
        Assert.Equal(0.5, applied.Streams["default"].Sampling);
        Assert.Equal(500, applied.RateLimitPerMinute);
    }

    [Fact]
    public void Policy_applies_stream_defaults_clamps_and_derives_streams()
    {
        var policy = new GameLogPolicy(new GameLogSettings(Enabled: true, Streams: new Dictionary<string, GameLogStreamSettings>
        {
            ["default"] = new(RetentionDays: 99_999),
        }));

        Assert.True(policy.IsStreamEnabled("default"));
        Assert.False(policy.IsStreamEnabled("chat"));
        Assert.True(policy.IsStreamEnabled("audit"));
        Assert.Equal(GameLogPolicy.MaxRetentionDays, policy.RetentionDaysFor("default"));
        Assert.Equal(14, new GameLogPolicy(new GameLogSettings(Enabled: true)).RetentionDaysFor("chat"));
        Assert.Equal(365, new GameLogPolicy(new GameLogSettings(Enabled: true)).RetentionDaysFor("audit"));
        Assert.Equal(GameLogPolicy.DefaultRateLimitPerMinute, policy.RateLimitPerMinute);

        Assert.Equal("chat", GameLogPolicy.DeriveStream("chat.say"));
        Assert.Equal("audit", GameLogPolicy.DeriveStream("admin.ban"));
        Assert.Equal("audit", GameLogPolicy.DeriveStream("command.reset"));
        Assert.Equal("default", GameLogPolicy.DeriveStream("match.end"));
    }

    [Fact]
    public void Rate_limiter_allows_the_budget_then_denies_with_retry_after()
    {
        var limiter = new GameLogRateLimiter();
        Assert.True(limiter.TryConsume("p1", perMinute: 3, cost: 1).Allowed);
        Assert.True(limiter.TryConsume("p1", perMinute: 3, cost: 2).Allowed);

        var denied = limiter.TryConsume("p1", perMinute: 3, cost: 1);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterSeconds >= 1);

        Assert.True(limiter.TryConsume("p2", perMinute: 3, cost: 1).Allowed);
    }

    [Fact]
    public void Queue_drops_when_full_and_counts_the_drop_per_project()
    {
        var queue = new GameLogQueue();
        for (var i = 0; i < GameLogQueue.Capacity; i++)
            Assert.True(queue.TryEnqueue(Row($"e{i}")));

        Assert.False(queue.TryEnqueue(Row("overflow")));

        var counters = queue.Snapshot("p1");
        Assert.Equal(GameLogQueue.Capacity, counters.Accepted);
        Assert.Equal(1, counters.DroppedQueueFull);
        Assert.Equal(GameLogQueue.Capacity, counters.Buffered);
    }

    private static GameLogRow Row(string entryId) => new(
        ProjectId: "p1",
        EntryId: entryId,
        TsUnixMs: NowMs,
        Stream: "default",
        Level: "info",
        Event: "match.end",
        ActorSteamId: null,
        TargetSteamId: null,
        RevisionId: null,
        ClientType: null,
        LibraryVersion: null,
        Message: "",
        AttrsJson: "{}",
        TraceId: null,
        SearchText: "");

    private static GameLogSearchParseResult Parse(GameLogPolicy policy, params (string Key, string Value)[] parameters)
        => GameLogSearchParser.Parse("p1", parameters.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)), policy,
            s => s.Length == 17 && s.All(char.IsAsciiDigit));
}
