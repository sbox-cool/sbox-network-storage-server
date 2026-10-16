namespace SboxNetworkStorage.EndpointSpike.Runner;

/// <summary>
/// One deterministic run of the finish-combat endpoint. <see cref="Diagnostic"/> selects a worker kill-path mode and
/// makes the scenario sandbox-only. <see cref="ExpectStatus"/>/<see cref="ExpectCode"/> are the outcome the design
/// specifies; a mismatch fails the demo.
/// </summary>
public sealed record Scenario(
    string Name,
    Dictionary<string, object?> Input,
    Dictionary<string, object?>? PlayerPayload,
    int ExpectStatus,
    string? ExpectCode,
    bool DryRunAllowed = true,
    string? Diagnostic = null,
    string? ExpectLimit = null,
    bool ExpectOk = false);

public static class FinishCombatScenarios
{
    /// <summary>Fixed clock so the in-process dry-run and the worker see identical time.</summary>
    public const long NowMs = 1_700_000_000_000L;

    public const string SteamId = "76561190000000001";

    public static Dictionary<string, object?> GameValues() => new()
    {
        ["weapons"] = new List<object?>
        {
            new Dictionary<string, object?> { ["weapon_id"] = "pistol", ["damage"] = 25.0, ["fire_interval_ms"] = 200.0 },
        },
        ["combat"] = new Dictionary<string, object?>
        {
            ["xp_per_zombie_level"] = 10.0,
            ["max_clock_skew_ms"] = 5000.0,
        },
    };

    public static Dictionary<string, object?> Input(double hits, long clientTimeMs) => new()
    {
        ["hits"] = hits,
        ["client_time_ms"] = (double)clientTimeMs,
    };

    /// <summary>Player record payload. Pass <paramref name="startedMs"/> null to model a player not in combat.</summary>
    public static Dictionary<string, object?> Player(long? startedMs, string weaponId = "pistol", double hp = 100, double level = 2)
    {
        var combat = new Dictionary<string, object?>
        {
            ["weapon_id"] = weaponId,
            ["zombie_hp"] = hp,
            ["zombie_level"] = level,
        };
        if (startedMs is { } started)
        {
            combat["started_ms"] = (double)started;
        }
        return new Dictionary<string, object?> { ["combat"] = combat };
    }

    /// <summary>Scenarios in fixed order. Each runs against a fresh project so state never leaks between them.</summary>
    public static IReadOnlyList<Scenario> All() =>
    [
        new("success", Input(4, NowMs), Player(NowMs - 2000), 200, null, ExpectOk: true),
        new("boundary_hits_equal_max", Input(4, NowMs), Player(NowMs - 600), 200, null, ExpectOk: true),
        new("boundary_skew_equal_limit", Input(4, NowMs - 5000), Player(NowMs - 2000), 200, null, ExpectOk: true),
        new("no_combat", Input(4, NowMs), Player(null), 409, "NO_COMBAT"),
        new("unknown_weapon", Input(4, NowMs), Player(NowMs - 2000, weaponId: "railgun"), 404, "UNKNOWN_WEAPON"),
        new("zombie_alive", Input(3, NowMs), Player(NowMs - 2000), 400, "ZOMBIE_ALIVE"),
        new("too_many_hits", Input(4, NowMs), Player(NowMs - 100), 400, "TOO_MANY_HITS"),
        new("clock_skew", Input(4, NowMs - 6000), Player(NowMs - 2000), 400, "CLOCK_SKEW"),
        new("materialized_limit", Input(4, NowMs), WithBlob(Player(NowMs - 2000), 5 * 1024 * 1024), 0, null,
            DryRunAllowed: false, ExpectLimit: "MATERIALIZED_LIMIT"),
        new("input_limit", WithPad(Input(4, NowMs), 5 * 1024 * 1024), Player(NowMs - 2000), 0, null,
            DryRunAllowed: false, ExpectLimit: "INPUT_LIMIT"),
        new("diagnostic_cpu", Input(4, NowMs), Player(NowMs - 2000), 0, null,
            DryRunAllowed: false, Diagnostic: "spin", ExpectLimit: "CPU_LIMIT"),
        new("diagnostic_rss", Input(4, NowMs), Player(NowMs - 2000), 0, null,
            DryRunAllowed: false, Diagnostic: "allocate", ExpectLimit: "RSS_LIMIT"),
        new("diagnostic_wall", Input(4, NowMs), Player(NowMs - 2000), 0, null,
            DryRunAllowed: false, Diagnostic: "sleep", ExpectLimit: "WALL_LIMIT"),
        new("diagnostic_output", Input(4, NowMs), Player(NowMs - 2000), 0, null,
            DryRunAllowed: false, Diagnostic: "flood", ExpectLimit: "OUTPUT_LIMIT"),
    ];

    private static Dictionary<string, object?> WithPad(Dictionary<string, object?> input, int bytes)
    {
        input["pad"] = new string('a', bytes);
        return input;
    }

    private static Dictionary<string, object?> WithBlob(Dictionary<string, object?> player, int bytes)
    {
        player["blob"] = new string('a', bytes);
        return player;
    }
}
