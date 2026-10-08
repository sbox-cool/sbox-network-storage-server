using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Byte-parity coverage for the native player-analytics aggregation engines
/// (<see cref="PlayerLedgerInsightsEngine"/> / <see cref="PlayerTimelineEngine"/>)
/// ported from <c>services/network-storage-player-analytics.js</c> and
/// <c>controllers/storage-modules/insights-routes.js</c>.
///
/// The expected fixture (<c>ledger-insights-expected.json</c>) was captured by
/// running the legacy <c>buildPlayerLedgerInsights</c> directly over the input
/// fixture, so this asserts the C# port matches the legacy JS field-for-field.
/// </summary>
public sealed class PlayerAnalyticsEngineParityTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-analytics");

    [Fact]
    public void LedgerInsights_MatchesLegacyFixture()
    {
        using var inputDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "ledger-insights-input.json")));
        using var expectedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "ledger-insights-expected.json")));

        var root = inputDoc.RootElement;
        var events = root.GetProperty("events").EnumerateArray()
            .Select(e => PlayerAnalyticsEvent.From(e.Clone())!)
            .ToList();
        var sessions = root.GetProperty("sessions").EnumerateArray().Select(s => s.Clone()).ToList();
        var profile = root.GetProperty("profile").Clone();

        var options = PlayerLedgerInsightsEngine.NormalizeOptions(null, null);
        var result = PlayerLedgerInsightsEngine.Build(events, sessions, profile, options);

        var actualJson = JsonSerializer.Serialize(result);
        using var actualDoc = JsonDocument.Parse(actualJson);

        AssertJsonEqual("$", expectedDoc.RootElement, actualDoc.RootElement);
    }

    [Fact]
    public void Timeline_MatchesLegacyFixture()
    {
        using var inputDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "timeline-input.json")));
        using var expectedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "timeline-expected.json")));

        var root = inputDoc.RootElement;
        var events = root.GetProperty("events").EnumerateArray()
            .Select(e => PlayerAnalyticsEvent.From(e.Clone())!)
            .ToList();
        var sessions = root.GetProperty("sessions").EnumerateArray().Select(s => s.Clone()).ToList();

        var quiet = new[] { "load-profile", "save-profile", "get-public-player-info" };
        var timelineView = PlayerTimelineEngine.BuildTimelineView(events, showNoise: false, "session", quiet, 50);
        var errorView = PlayerTimelineEngine.BuildErrorTimelineView(events, "session", 50);
        var sessionJourney = PlayerTimelineEngine.BuildSessionJourney(sessions, events);

        var envelope = new Dictionary<string, object?>
        {
            ["timelineView"] = new Dictionary<string, object?>
            {
                ["hiddenTimelineCount"] = timelineView.HiddenTimelineCount,
                ["timeline"] = timelineView.Timeline,
                ["timelineGroup"] = timelineView.TimelineGroup,
                ["timelineGroups"] = timelineView.TimelineGroups,
            },
            ["errorView"] = new Dictionary<string, object?>
            {
                ["errorEventCount"] = errorView.ErrorEventCount,
                ["timeline"] = errorView.Timeline,
                ["timelineGroup"] = errorView.TimelineGroup,
                ["timelineGroups"] = errorView.TimelineGroups,
            },
            ["sessionJourney"] = sessionJourney,
        };

        var actualJson = JsonSerializer.Serialize(envelope);
        using var actualDoc = JsonDocument.Parse(actualJson);

        AssertJsonEqual("$", expectedDoc.RootElement, actualDoc.RootElement);
    }

    [Fact]
    public void NormalizeOptions_AppliesLegacyDefaults()
    {
        var o = PlayerLedgerInsightsEngine.NormalizeOptions(null, null);
        Assert.Equal(500, o.PointLimit);
        Assert.Equal(80, o.MilestoneLimit);
        Assert.Equal(30, o.CorrelationBeforeSeconds);
        Assert.Equal(10, o.CorrelationAfterSeconds);
        Assert.Equal(1, o.AbsoluteThreshold);
        Assert.Equal(0.25, o.ProportionalThreshold);
        Assert.Equal(100, o.SessionNetAbsoluteThreshold);
    }

    [Fact]
    public void NormalizeOptions_CorrelationWindowOverridesBeforeAndAfter()
    {
        var values = new Dictionary<string, double?> { ["correlationWindowSeconds"] = 45 };
        var o = PlayerLedgerInsightsEngine.NormalizeOptions(values, null);
        Assert.Equal(45, o.CorrelationBeforeSeconds);
        Assert.Equal(45, o.CorrelationAfterSeconds);
    }

    [Fact]
    public void NormalizeGroup_FallsBackToSession()
    {
        Assert.Equal("session", PlayerTimelineEngine.NormalizeGroup(null));
        Assert.Equal("session", PlayerTimelineEngine.NormalizeGroup("bogus"));
        Assert.Equal("day", PlayerTimelineEngine.NormalizeGroup("day"));
        Assert.Equal("raw", PlayerTimelineEngine.NormalizeGroup("raw"));
    }

    private static void AssertJsonEqual(string path, JsonElement expected, JsonElement actual)
    {
        Assert.True(expected.ValueKind == actual.ValueKind || IsNumericPair(expected, actual),
            $"{path}: kind mismatch expected {expected.ValueKind} got {actual.ValueKind}");

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                foreach (var name in expectedProps.Keys)
                {
                    Assert.True(actualProps.ContainsKey(name), $"{path}: missing property '{name}'");
                    AssertJsonEqual($"{path}.{name}", expectedProps[name], actualProps[name]);
                }
                foreach (var name in actualProps.Keys)
                {
                    Assert.True(expectedProps.ContainsKey(name), $"{path}: unexpected extra property '{name}'");
                }
                break;
            case JsonValueKind.Array:
                var e = expected.EnumerateArray().ToList();
                var a = actual.EnumerateArray().ToList();
                Assert.True(e.Count == a.Count, $"{path}: array length expected {e.Count} got {a.Count}");
                for (var i = 0; i < e.Count; i++)
                {
                    AssertJsonEqual($"{path}[{i}]", e[i], a[i]);
                }
                break;
            case JsonValueKind.Number:
                Assert.True(NumbersEqual(expected, actual), $"{path}: number expected {expected.GetRawText()} got {actual.GetRawText()}");
                break;
            case JsonValueKind.String:
                Assert.True(expected.GetString() == actual.GetString(), $"{path}: string expected '{expected.GetString()}' got '{actual.GetString()}'");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                // ValueKind already matched.
                break;
        }
    }

    private static bool IsNumericPair(JsonElement a, JsonElement b)
        => a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number;

    private static bool NumbersEqual(JsonElement a, JsonElement b)
    {
        var da = a.GetDouble();
        var db = b.GetDouble();
        if (da == db) return true;
        var scale = Math.Max(1, Math.Max(Math.Abs(da), Math.Abs(db)));
        return Math.Abs(da - db) <= 1e-9 * scale;
    }
}
