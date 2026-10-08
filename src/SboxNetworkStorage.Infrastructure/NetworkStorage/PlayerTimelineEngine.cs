using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Pure port of the player timeline view builders from
/// <c>controllers/storage-modules/insights-routes.js</c>
/// (<c>buildPlayerTimelineView</c>, <c>buildPlayerErrorTimelineView</c>,
/// <c>buildTimelineGroups</c>, <c>buildPlayerSessionJourney</c>,
/// <c>buildManagedSessionSummary</c>). No I/O; operates over the in-memory
/// event/session lists so the grouping + classification logic can be tested
/// for parity directly.
/// </summary>
internal static class PlayerTimelineEngine
{
    private static readonly HashSet<string> Groups = new(StringComparer.Ordinal)
    {
        "session", "hour", "day", "week", "raw",
    };

    public static string NormalizeGroup(string? value)
        => value is not null && Groups.Contains(value) ? value : "session";

    public static int NormalizeLowFpsThreshold(double? value)
    {
        var rounded = (int)Math.Round(double.IsFinite(value ?? double.NaN) ? value!.Value : 50, MidpointRounding.AwayFromZero);
        if (rounded == 0 && (value is null || !double.IsFinite(value.Value))) rounded = 50;
        return Math.Max(1, Math.Min(240, rounded));
    }

    // ── FPS helpers ──

    private static JsonElement TimelineEventFps(PlayerAnalyticsEvent e)
    {
        var payload = e.Payload;
        if (payload.ValueKind != JsonValueKind.Object) return default;
        if (payload.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Object) return fps;
        if (payload.TryGetProperty("performance", out var perf) && perf.ValueKind == JsonValueKind.Object) return perf;
        return default;
    }

    private static int? FpsMetricValue(JsonElement fps, params string[] keys)
    {
        if (fps.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (fps.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d > 0)
            {
                return (int)Math.Round(d, MidpointRounding.AwayFromZero);
            }
        }
        return null;
    }

    private static (int? Avg, int? Min, int? Max) TimelineFpsMetrics(PlayerAnalyticsEvent e)
    {
        var fps = TimelineEventFps(e);
        return (
            FpsMetricValue(fps, "average", "avg", "fpsAverage"),
            FpsMetricValue(fps, "min", "minimum", "fpsMin"),
            FpsMetricValue(fps, "max", "peak", "fpsPeak"));
    }

    /// <summary>Numeric fps.average/avg used by the group avg accumulation (raw double, parity with Number()).</summary>
    private static double FpsAverageRaw(PlayerAnalyticsEvent e)
    {
        var fps = TimelineEventFps(e);
        if (fps.ValueKind != JsonValueKind.Object) return 0;
        return PlayerAnalyticsEvent.NumberOf(fps, "average") ?? PlayerAnalyticsEvent.NumberOf(fps, "avg") ?? 0;
    }

    private static double FpsPeakRaw(PlayerAnalyticsEvent e)
    {
        var fps = TimelineEventFps(e);
        if (fps.ValueKind != JsonValueKind.Object) return 0;
        return PlayerAnalyticsEvent.NumberOf(fps, "peak") ?? PlayerAnalyticsEvent.NumberOf(fps, "max") ?? 0;
    }

    /// <summary>Parity: average(values) — rounds the mean of finite positive values.</summary>
    public static int Average(IEnumerable<double> values)
    {
        var valid = values.Where(v => double.IsFinite(v) && v > 0).ToList();
        if (valid.Count == 0) return 0;
        return (int)Math.Round(valid.Sum() / valid.Count, MidpointRounding.AwayFromZero);
    }

    // ── Classification ──

    private static bool IsRoutineHeartbeat(PlayerAnalyticsEvent e, int lowFpsThreshold)
    {
        if (e.Type != "session.heartbeat") return false;
        var (avg, _, _) = TimelineFpsMetrics(e);
        return !(avg is { } a && a > 0 && a < lowFpsThreshold);
    }

    /// <summary>
    /// Cumulative play-session seconds carried by a heartbeat. Native heartbeats
    /// store it at the payload top level; legacy stats-heartbeat events
    /// double-nested the inner payload under "payload", so fall back to that shape
    /// so existing data still reports a duration instead of 0.
    /// </summary>
    private static double HeartbeatSessionSeconds(PlayerAnalyticsEvent e)
    {
        if (!e.HasPayload) return 0;
        if (PlayerAnalyticsEvent.NumberOf(e.Payload, "sessionSeconds") is { } direct && direct > 0) return direct;
        var nested = PlayerAnalyticsEvent.ObjectSection(e.Payload, "payload");
        if (nested.ValueKind == JsonValueKind.Object
            && PlayerAnalyticsEvent.NumberOf(nested, "sessionSeconds") is { } n && n > 0)
        {
            return n;
        }
        return 0;
    }

    private static bool IsQuiet(PlayerAnalyticsEvent e, IReadOnlyCollection<string> quietEndpointSlugs, bool includeRoutineHeartbeats, int lowFpsThreshold)
    {
        if (includeRoutineHeartbeats && IsRoutineHeartbeat(e, lowFpsThreshold)) return true;
        if (e.Category != "endpoint") return false;
        return quietEndpointSlugs.Contains(e.EndpointSlug.ToLowerInvariant());
    }

    private static int? TimelineEventStatus(PlayerAnalyticsEvent e)
    {
        var payload = e.Payload;
        var response = ObjOrDefault(payload, "responsePayload", "response");
        var responseSummary = ObjOrDefault(payload, "responsePayloadSummary", "responseSummary");
        int? Num(JsonElement parent, string key)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (parent.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return (int)d;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            }
            return null;
        }
        foreach (var (parent, key) in new[]
                 {
                     (e.Raw, "status"), (e.Raw, "statusCode"),
                     (payload, "status"), (payload, "statusCode"),
                     (response, "status"), (response, "statusCode"),
                     (responseSummary, "status"), (responseSummary, "statusCode"),
                 })
        {
            var s = Num(parent, key);
            if (s.HasValue) return s;
        }
        return null;
    }

    private static JsonElement ObjOrDefault(JsonElement parent, string primary, string fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object) return default;
        if (parent.TryGetProperty(primary, out var a) && a.ValueKind == JsonValueKind.Object) return a;
        if (parent.TryGetProperty(fallback, out var b) && b.ValueKind == JsonValueKind.Object) return b;
        return default;
    }

    private static bool IsExplicitError(PlayerAnalyticsEvent e)
    {
        var type = e.Type.ToLowerInvariant();
        var payload = e.Payload;
        var severity = (payload.ValueKind == JsonValueKind.Object
            ? PlayerAnalyticsEvent.StrOf(payload, "severity")
            : string.Empty);
        if (severity.Length == 0) severity = e.Str("severity");
        severity = severity.ToLowerInvariant();
        return e.Category == "error"
               || type.StartsWith("error.", StringComparison.Ordinal)
               || type.Contains(".error", StringComparison.Ordinal)
               || type.EndsWith("_error", StringComparison.Ordinal)
               || severity == "error";
    }

    private static bool IsSoftError(PlayerAnalyticsEvent e)
    {
        if (IsExplicitError(e)) return false;
        var payload = e.Payload;
        var response = ObjOrDefault(payload, "responsePayload", "response");
        var status = TimelineEventStatus(e);
        var responseOkFalse = response.ValueKind == JsonValueKind.Object
                              && response.TryGetProperty("ok", out var ro) && ro.ValueKind == JsonValueKind.False;
        return (status is { } s && s >= 400) || e.OkIsFalse || responseOkFalse;
    }

    private static bool IsErrorEvent(PlayerAnalyticsEvent e) => IsExplicitError(e) || IsSoftError(e);

    // ── Endpoint / moment labels ──

    public static string FormatEndpointJourneyLabel(string? slug)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "^network[_-]storage[_-]", string.Empty);
        switch (normalized)
        {
            case "load-profile": return "Loaded profile";
            case "save-profile": return "Saved profile";
            case "get-public-player-info": return "Read public player info";
            case "cast-line": return "Cast a line";
            case "resolve-cast": return "Caught / resolved fish";
        }
        var parts = normalized.Split(new[] { '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        var joined = string.Join(' ', parts);
        return joined.Length > 0 ? joined : "Called endpoint";
    }

    private static string EventSnapshotSection(PlayerAnalyticsEvent e)
    {
        var payload = e.Payload;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var section = PlayerAnalyticsEvent.StrOf(payload, "section");
            if (section.Length > 0) return section;
        }
        var type = e.Type;
        return type.EndsWith(".snapshot", StringComparison.Ordinal) ? type[..^".snapshot".Length] : string.Empty;
    }

    private static readonly Dictionary<string, string> ManagedSignalLabels = new(StringComparer.Ordinal)
    {
        ["client"] = "Client", ["runtime"] = "Runtime", ["revision"] = "Revision", ["scene"] = "Scene",
        ["system"] = "System", ["networkRoster"] = "Network roster", ["performance"] = "Performance",
        ["network"] = "Network", ["voice"] = "Voice", ["inputFocus"] = "Input focus",
        ["endpointHealth"] = "Endpoint health",
    };

    private static string ManagedSignalLabel(string section)
    {
        if (ManagedSignalLabels.TryGetValue(section, out var label)) return label;
        var spaced = System.Text.RegularExpressions.Regex.Replace(section ?? string.Empty, "([a-z])([A-Z])", "$1 $2");
        spaced = System.Text.RegularExpressions.Regex.Replace(spaced, "[_-]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(spaced, @"\b\w", m => m.Value.ToUpperInvariant());
    }

    private static string FormatMomentLabel(PlayerAnalyticsEvent e)
    {
        if (e.Category == "endpoint") return FormatEndpointJourneyLabel(e.EndpointSlug);
        if (e.Category == "error")
        {
            var basis = e.EndpointSlug.Length > 0 ? e.EndpointSlug : (e.Label.Length > 0 ? e.Label : e.Type);
            return $"{FormatEndpointJourneyLabel(basis)} error";
        }
        if (e.Category == "warning")
        {
            var code = e.HasPayload ? PlayerAnalyticsEvent.StrOf(e.Payload, "code") : string.Empty;
            var basis = e.Label.Length > 0 ? e.Label : (code.Length > 0 ? code : e.Type);
            return $"Warning: {FormatEndpointJourneyLabel(basis)}";
        }
        var section = EventSnapshotSection(e);
        if (section.Length > 0) return $"{ManagedSignalLabel(section)} changed";
        switch (e.Type)
        {
            case "revision.outdated": return "Outdated revision";
            case "network.poor_quality": return "Network quality incident";
            case "voice.activity": return "Voice activity";
            case "performance.low_fps":
            case "performance.stutter": return "Performance incident";
        }
        return e.Label.Length > 0 ? e.Label : (e.Type.Length > 0 ? e.Type : "Event");
    }

    private static object? JourneyLabel(PlayerAnalyticsEvent e)
        => e.Category == "endpoint" ? FormatEndpointJourneyLabel(e.EndpointSlug) : null;

    // ── Timeline buckets ──

    private sealed class GroupRow
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public List<PlayerAnalyticsEvent> Events { get; } = new();
        public string? StartedAt { get; set; }
        public string? EndedAt { get; set; }
        public List<double> FpsValues { get; } = new();
        public int AvgFps { get; set; }
        public double PeakFps { get; set; }
    }

    private static (string Key, string Label) TimelineBucket(PlayerAnalyticsEvent e, string group)
    {
        var ts = e.Ts;
        if (ts.Length == 0 || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            // legacy: new Date(event.ts || Date.now()); invalid → "unknown"
            if (group == "session")
            {
                var sessionId = e.SessionId.Length > 0 ? e.SessionId : "no-session";
                return (sessionId, "Game session");
            }
            return ("unknown", "Unknown time");
        }
        var utc = date.ToUniversalTime();
        switch (group)
        {
            case "hour":
                return (utc.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture),
                    utc.ToString("ddd, MMM d, h tt", CultureInfo.GetCultureInfo("en-US")));
            case "day":
                return (utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    utc.ToString("dddd, MMM d", CultureInfo.GetCultureInfo("en-US")));
            case "week":
            {
                var dow = ((int)utc.DayOfWeek + 6) % 7; // Monday-based, parity with (getDay()+6)%7
                var weekStart = utc.AddDays(-dow);
                return (weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    $"Week of {weekStart.ToString("MMM d", CultureInfo.GetCultureInfo("en-US"))}");
            }
            default:
                var sid = e.SessionId.Length > 0 ? e.SessionId : "no-session";
                return (sid, "Game session");
        }
    }

    private static List<Dictionary<string, object?>> BuildTimelineGroups(IReadOnlyList<PlayerAnalyticsEvent> events, string group, int lowFpsThreshold)
    {
        var byKey = new Dictionary<string, GroupRow>(StringComparer.Ordinal);
        var order = new List<GroupRow>();
        foreach (var e in events)
        {
            var (key, label) = TimelineBucket(e, group);
            if (!byKey.TryGetValue(key, out var current))
            {
                current = new GroupRow { Key = key, Label = label, StartedAt = e.Ts, EndedAt = e.Ts };
                byKey[key] = current;
                order.Add(current);
            }
            current.Events.Add(e);
            if (current.StartedAt is null || string.CompareOrdinal(e.Ts, current.StartedAt) < 0) current.StartedAt = e.Ts;
            if (current.EndedAt is null || string.CompareOrdinal(e.Ts, current.EndedAt) > 0) current.EndedAt = e.Ts;
            var avg = FpsAverageRaw(e);
            var peak = FpsPeakRaw(e);
            if (avg > 0) current.FpsValues.Add(avg);
            if (peak > 0) current.PeakFps = Math.Max(current.PeakFps, peak);
        }

        foreach (var row in order)
        {
            if (group == "session")
            {
                row.Events.Sort((a, b) => string.CompareOrdinal(b.Ts, a.Ts));
            }
            row.AvgFps = Average(row.FpsValues);
        }

        return order.Select(row => CollapseLowFps(row, lowFpsThreshold, group)).ToList();
    }

    private static Dictionary<string, object?> CollapseLowFps(GroupRow row, int threshold, string group)
    {
        var summaryEvent = CreateLowFpsSummary(row, threshold);
        List<Dictionary<string, object?>> serializedEvents;
        Dictionary<string, object?>? lowFpsSummary = null;

        if (summaryEvent is null)
        {
            serializedEvents = row.Events.Select(SerializeTimelineEvent).ToList();
        }
        else
        {
            var kept = row.Events.Where(e =>
            {
                if (e.Type != "session.heartbeat") return true;
                var (avg, _, _) = TimelineFpsMetrics(e);
                return !(avg is { } a && a > 0 && a < threshold);
            }).Select(SerializeTimelineEvent).ToList();
            kept.Add(summaryEvent.Value.Event);
            kept.Sort((a, b) => string.CompareOrdinal(TsOf(b), TsOf(a)));
            serializedEvents = kept;
            lowFpsSummary = summaryEvent.Value.Payload;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = row.Key,
            ["label"] = row.Label,
            ["events"] = serializedEvents,
            ["startedAt"] = row.StartedAt,
            ["endedAt"] = row.EndedAt,
            ["avgFps"] = row.AvgFps,
            ["peakFps"] = row.PeakFps,
        };
        if (lowFpsSummary is not null) map["lowFpsSummary"] = lowFpsSummary;
        return map;
    }

    private static string TsOf(Dictionary<string, object?> serialized)
        => serialized.TryGetValue("ts", out var v) && v is string s ? s
           : serialized.TryGetValue("ts", out var je) && je is JsonElement j && j.ValueKind == JsonValueKind.String ? j.GetString() ?? string.Empty
           : string.Empty;

    private static Dictionary<string, object?> SerializeTimelineEvent(PlayerAnalyticsEvent e)
        => e.ToMapWith(("journeyLabel", JourneyLabel(e)));

    private static (Dictionary<string, object?> Event, Dictionary<string, object?> Payload)? CreateLowFpsSummary(GroupRow row, int threshold)
    {
        var samples = row.Events
            .Where(e => e.Type == "session.heartbeat")
            .Select(e => (Event: e, Fps: TimelineFpsMetrics(e)))
            .Where(r => r.Fps.Avg is { } a && a > 0 && a < threshold)
            .OrderBy(r => r.Fps.Avg!.Value)
            .ThenBy(r => r.Event.Ts, StringComparer.Ordinal)
            .ToList();

        if (samples.Count == 0) return null;
        var worst = samples[0];
        var breakdown = samples.Take(8).Select(r =>
        {
            var sessionSeconds = r.Event.HasPayload ? PlayerAnalyticsEvent.NumberOf(r.Event.Payload, "sessionSeconds") : null;
            return (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ts"] = r.Event.Ts.Length > 0 ? r.Event.Ts : null,
                ["sessionSeconds"] = sessionSeconds,
                ["avg"] = r.Fps.Avg,
                ["min"] = r.Fps.Min,
                ["max"] = r.Fps.Max,
            };
        }).ToList();

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["threshold"] = threshold,
            ["sampleCount"] = samples.Count,
            ["fps"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["average"] = worst.Fps.Avg,
                ["min"] = worst.Fps.Min,
                ["max"] = worst.Fps.Max,
            },
            ["lowest"] = breakdown,
        };

        var summaryEvent = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "session.low_fps.summary",
            ["category"] = "performance",
            ["ts"] = worst.Event.Ts,
            ["sessionId"] = worst.Event.SessionId.Length > 0 ? worst.Event.SessionId : row.Key,
            ["steamId"] = worst.Event.SteamId.Length > 0 ? worst.Event.SteamId : null,
            ["label"] = "Low FPS",
            ["payload"] = payload,
        };
        return (summaryEvent, payload);
    }

    private static Dictionary<string, object?> RawGroup(string label, List<PlayerAnalyticsEvent> timeline)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = "raw",
            ["label"] = label,
            ["events"] = timeline.Select(SerializeTimelineEvent).ToList(),
            ["startedAt"] = timeline.Count > 0 ? (timeline[0].Ts.Length > 0 ? timeline[0].Ts : null) : null,
            ["endedAt"] = timeline.Count > 0 ? (timeline[^1].Ts.Length > 0 ? timeline[^1].Ts : null) : null,
            ["avgFps"] = Average(timeline.Select(FpsAverageRaw)),
            ["peakFps"] = timeline.Count == 0 ? 0d : Math.Max(0, timeline.Select(FpsPeakRaw).DefaultIfEmpty(0).Max()),
        };
    }

    // ── Public view builders ──

    public sealed record TimelineView(
        int HiddenTimelineCount,
        List<Dictionary<string, object?>> Timeline,
        string TimelineGroup,
        List<Dictionary<string, object?>> TimelineGroups);

    public sealed record ErrorTimelineView(
        int ErrorEventCount,
        List<Dictionary<string, object?>> Timeline,
        string TimelineGroup,
        List<Dictionary<string, object?>> TimelineGroups);

    public static TimelineView BuildTimelineView(
        IReadOnlyList<PlayerAnalyticsEvent> rawTimeline, bool showNoise, string timelineGroup,
        IReadOnlyCollection<string> quietEndpointSlugs, int lowFpsThreshold)
    {
        var group = NormalizeGroup(timelineGroup);
        var hidden = rawTimeline.Count(e => IsQuiet(e, quietEndpointSlugs, includeRoutineHeartbeats: true, lowFpsThreshold));
        var display = (showNoise
                ? rawTimeline.ToList()
                : rawTimeline.Where(e => !IsQuiet(e, quietEndpointSlugs, includeRoutineHeartbeats: true, lowFpsThreshold)).ToList())
            .OrderByDescending(e => e.Ts, StringComparer.Ordinal)
            .ToList();
        var timeline = display.Select(SerializeTimelineEvent).ToList();
        var timelineGroups = group == "raw"
            ? new List<Dictionary<string, object?>> { RawGroup("Raw timeline", display) }
            : BuildTimelineGroups(display, group, lowFpsThreshold);
        return new TimelineView(hidden, timeline, group, timelineGroups);
    }

    public static ErrorTimelineView BuildErrorTimelineView(
        IReadOnlyList<PlayerAnalyticsEvent> rawTimeline, string timelineGroup, int lowFpsThreshold)
    {
        var group = NormalizeGroup(timelineGroup);
        var filtered = rawTimeline
            .Where(IsErrorEvent)
            .OrderByDescending(e => e.Ts, StringComparer.Ordinal)
            .ToList();
        var timeline = filtered.Select(SerializeTimelineEvent).ToList();
        var timelineGroups = group == "raw"
            ? new List<Dictionary<string, object?>> { RawGroup("Raw errors", filtered) }
            : BuildTimelineGroups(filtered, group, lowFpsThreshold);
        return new ErrorTimelineView(filtered.Count, timeline, group, timelineGroups);
    }

    // ── Session journey ──

    private static readonly string[] JourneyQuietSlugs = { "load-profile", "save-profile", "get-public-player-info" };

    public static List<Dictionary<string, object?>> BuildSessionJourney(
        IReadOnlyList<JsonElement> sessions, IReadOnlyList<PlayerAnalyticsEvent> events)
    {
        var eventsBySession = new Dictionary<string, List<PlayerAnalyticsEvent>>(StringComparer.Ordinal);
        var sessionEventOrder = new List<string>();
        foreach (var e in events)
        {
            var key = e.SessionId.Length > 0 ? e.SessionId : "no-session";
            if (!eventsBySession.TryGetValue(key, out var list))
            {
                list = new List<PlayerAnalyticsEvent>();
                eventsBySession[key] = list;
                sessionEventOrder.Add(key);
            }
            list.Add(e);
        }

        // sessionRows: real sessions when present; else synthesised from grouped events.
        var sessionRows = new List<(JsonElement? Session, string SessionId)>();
        if (sessions.Count > 0)
        {
            foreach (var s in sessions)
            {
                sessionRows.Add((s, SessionIdOf(s)));
            }
        }
        else
        {
            foreach (var key in sessionEventOrder)
            {
                sessionRows.Add((null, key));
            }
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var (session, sessionId) in sessionRows)
        {
            var lookupKey = sessionId.Length > 0 ? sessionId : "no-session";
            var rows = eventsBySession.TryGetValue(lookupKey, out var r) ? r : new List<PlayerAnalyticsEvent>();
            var heartbeats = rows.Where(e => e.Type == "session.heartbeat").ToList();
            var important = rows
                .Where(e => !IsQuiet(e, JourneyQuietSlugs, includeRoutineHeartbeats: true, 50) && e.Category != "session")
                .OrderByDescending(e => e.Ts, StringComparer.Ordinal)
                .ToList();
            var loadedProfile = rows.FirstOrDefault(e => e.EndpointSlug == "load-profile");
            var performance = session is { ValueKind: JsonValueKind.Object } sv
                              && sv.TryGetProperty("performance", out var perf) && perf.ValueKind == JsonValueKind.Object
                ? perf
                : default;

            var sessionStarted = session is { ValueKind: JsonValueKind.Object } s1 ? SessionTimestamp(s1, "startedAt", "started_at_unix_ms") : string.Empty;
            var sessionEnded = session is { ValueKind: JsonValueKind.Object } s2 ? SessionTimestamp(s2, "endedAt", "ended_at_unix_ms") : string.Empty;
            var sessionUpdated = session is { ValueKind: JsonValueKind.Object } s3 ? SessionTimestamp(s3, "updatedAt", "last_heartbeat_at_unix_ms") : string.Empty;
            var sessionDuration = session is { ValueKind: JsonValueKind.Object } s4 ? PlayerAnalyticsEvent.NumberOf(s4, "durationSeconds") ?? 0 : 0;
            var sessionEventCount = session is { ValueKind: JsonValueKind.Object } s5 ? PlayerAnalyticsEvent.NumberOf(s5, "eventCount") ?? 0 : 0;

            var startedAt = sessionStarted.Length > 0 ? sessionStarted : (rows.Count > 0 ? NullIfEmpty(rows[0].Ts) : null);
            var updatedAt = sessionUpdated.Length > 0 ? sessionUpdated : (rows.Count > 0 ? NullIfEmpty(rows[^1].Ts) : null);

            var heartbeatSeconds = heartbeats
                .Select(HeartbeatSessionSeconds)
                .DefaultIfEmpty(0).Max();
            var durationSeconds = Math.Max(Math.Max(heartbeatSeconds, sessionDuration), 0);

            var perfFpsAverage = performance.ValueKind == JsonValueKind.Object ? PlayerAnalyticsEvent.NumberOf(performance, "fpsAverage") ?? 0 : 0;
            var perfFpsPeak = performance.ValueKind == JsonValueKind.Object ? PlayerAnalyticsEvent.NumberOf(performance, "fpsPeak") ?? 0 : 0;
            var perfFpsMin = performance.ValueKind == JsonValueKind.Object ? PlayerAnalyticsEvent.NumberOf(performance, "fpsMin") ?? 0 : 0;

            var avgFps = perfFpsAverage > 0
                ? (int)Math.Round(perfFpsAverage, MidpointRounding.AwayFromZero)
                : Average(heartbeats.Select(FpsAverageRaw));
            var peakFps = perfFpsPeak > 0
                ? (int)Math.Round(perfFpsPeak, MidpointRounding.AwayFromZero)
                : (int)Math.Round(Math.Max(0, heartbeats.Select(FpsPeakRaw).DefaultIfEmpty(0).Max()), MidpointRounding.AwayFromZero);

            var hiddenCount = rows.Count(e => IsQuiet(e, JourneyQuietSlugs, includeRoutineHeartbeats: false, 50));

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (session is { ValueKind: JsonValueKind.Object } baseSession)
            {
                foreach (var prop in baseSession.EnumerateObject()) map[prop.Name] = prop.Value;
            }
            else
            {
                map["sessionId"] = sessionId;
            }
            map["startedAt"] = startedAt;
            map["endedAt"] = sessionEnded.Length > 0 ? sessionEnded : null;
            map["updatedAt"] = updatedAt;
            map["durationSeconds"] = (int)Math.Round(durationSeconds, MidpointRounding.AwayFromZero);
            map["avgFps"] = avgFps;
            map["peakFps"] = peakFps;
            map["minFps"] = (int)Math.Round(perfFpsMin, MidpointRounding.AwayFromZero);
            map["eventCount"] = rows.Count > 0 ? rows.Count : (int)Math.Round(sessionEventCount, MidpointRounding.AwayFromZero);
            map["hiddenCount"] = hiddenCount;
            map["heartbeatCount"] = heartbeats.Count;
            map["lastHeartbeatSeconds"] = (int)Math.Round(heartbeatSeconds, MidpointRounding.AwayFromZero);
            map["loadedProfileAt"] = loadedProfile is not null ? NullIfEmpty(loadedProfile.Ts) : null;
            map["managedTelemetry"] = BuildManagedSessionSummary(session, rows);
            map["keyMoments"] = important.Take(8).Select(e => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ts"] = NullIfEmpty(e.Ts),
                ["label"] = FormatMomentLabel(e),
                ["type"] = e.Type,
            }).ToList();

            result.Add(map);
        }

        return result
            .OrderByDescending(m => SortKey(m), StringComparer.Ordinal)
            .ToList();

        static string SortKey(Dictionary<string, object?> m)
        {
            // Sort by most recent activity (updatedAt), not by session start.
            // A live session that started 30m ago but has heartbeats from seconds
            // ago must sort above a dead session that started 7m ago with 1
            // heartbeat. Without this, sessions with a newer startedAt but no
            // recent activity push the live session out of the top 6 displayed.
            var updated = AsString(m.GetValueOrDefault("updatedAt"));
            if (updated.Length > 0) return updated;
            return AsString(m.GetValueOrDefault("startedAt"));
        }
    }

    private static string AsString(object? value) => value switch
    {
        string s => s,
        JsonElement j when j.ValueKind == JsonValueKind.String => j.GetString() ?? string.Empty,
        _ => string.Empty,
    };

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>
    /// Session id from either the Bun-shaped <c>sessionId</c> (parity fixtures) or
    /// the ScyllaDB row's <c>session_id</c> (live data).
    /// </summary>
    private static string SessionIdOf(JsonElement s)
    {
        var camel = PlayerAnalyticsEvent.StrOf(s, "sessionId");
        return camel.Length > 0 ? camel : PlayerAnalyticsEvent.StrOf(s, "session_id");
    }

    /// <summary>
    /// Read a session timestamp as an ISO string. Prefers the Bun-shaped ISO field
    /// (<paramref name="isoField"/>, used by parity fixtures); falls back to the
    /// ScyllaDB row's unix-ms field (<paramref name="unixMsField"/>) converted to
    /// ISO so the journey lines up with the ISO event timestamps.
    /// </summary>
    private static string SessionTimestamp(JsonElement s, string isoField, string unixMsField)
    {
        var iso = PlayerAnalyticsEvent.StrOf(s, isoField);
        if (iso.Length > 0) return iso;
        if (s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(unixMsField, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }
        return string.Empty;
    }

    // ── Managed session summary ──

    private static Dictionary<string, object?> BuildManagedSessionSummary(JsonElement? session, IReadOnlyList<PlayerAnalyticsEvent> rows)
    {
        var signals = new HashSet<string>(StringComparer.Ordinal);
        var snapshotSections = new HashSet<string>(StringComparer.Ordinal);

        void AddKeysOf(JsonElement parent, string key)
        {
            if (parent.ValueKind != JsonValueKind.Object) return;
            if (parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in v.EnumerateObject()) signals.Add(p.Name);
            }
        }

        if (session is { ValueKind: JsonValueKind.Object } s)
        {
            AddKeysOf(s, "managedSnapshotHashes");
            AddKeysOf(s, "managedSnapshots");
            AddKeysOf(s, "managedMetrics");
        }

        double endpointCalls = 0, endpointFailures = 0, voiceSeconds = 0, maxConnections = 0, maxPingMs = 0, memoryMb = 0;
        var incidentCount = 0;

        foreach (var e in rows)
        {
            var telemetry = e.HasPayload ? PlayerAnalyticsEvent.ObjectSection(e.Payload, "managedTelemetry") : default;
            if (telemetry.ValueKind == JsonValueKind.Object)
            {
                AddTelemetrySignals(signals, telemetry);
                var metrics = PlayerAnalyticsEvent.ObjectSection(telemetry, "metrics");
                if (metrics.ValueKind == JsonValueKind.Object)
                {
                    var perf = PlayerAnalyticsEvent.ObjectSection(metrics, "performance");
                    if (PlayerAnalyticsEvent.NumberOf(perf, "memoryMb") is { } mem && mem != 0) memoryMb = mem;
                    var net = PlayerAnalyticsEvent.ObjectSection(metrics, "network");
                    if (PlayerAnalyticsEvent.NumberOf(net, "connectionCount") is { } cc) maxConnections = Math.Max(maxConnections, cc);
                    if (PlayerAnalyticsEvent.NumberOf(net, "pingMs") is { } pm) maxPingMs = Math.Max(maxPingMs, pm);
                    var voice = PlayerAnalyticsEvent.ObjectSection(metrics, "voice");
                    if (PlayerAnalyticsEvent.NumberOf(voice, "speakingSeconds") is { } ss) voiceSeconds += ss;
                    var eh = PlayerAnalyticsEvent.ObjectSection(metrics, "endpointHealth");
                    if (PlayerAnalyticsEvent.NumberOf(eh, "callCount") is { } calls) endpointCalls += calls;
                    if (PlayerAnalyticsEvent.NumberOf(eh, "failureCount") is { } fails) endpointFailures += fails;
                }
            }

            var section = EventSnapshotSection(e);
            if (section.Length > 0)
            {
                signals.Add(section);
                snapshotSections.Add(section);
            }

            if (e.Category is "error" or "warning"
                || e.Type is "performance.low_fps" or "performance.stutter" or "network.poor_quality" or "revision.outdated" or "endpoint.client_summary")
            {
                incidentCount += 1;
            }
        }

        if (session is { ValueKind: JsonValueKind.Object } sm
            && sm.TryGetProperty("managedMetrics", out var managedMetrics) && managedMetrics.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in managedMetrics.EnumerateObject())
            {
                signals.Add(prop.Name);
                var values = prop.Value;
                if (values.ValueKind != JsonValueKind.Object) continue;
                if (PlayerAnalyticsEvent.NumberOf(values, "memoryMb") is { } mem && mem != 0) memoryMb = mem;
                if (PlayerAnalyticsEvent.NumberOf(values, "connectionCount") is { } cc) maxConnections = Math.Max(maxConnections, cc);
                if (PlayerAnalyticsEvent.NumberOf(values, "pingMs") is { } pm) maxPingMs = Math.Max(maxPingMs, pm);
                if (PlayerAnalyticsEvent.NumberOf(values, "speakingSeconds") is { } ss) voiceSeconds += ss;
                if (PlayerAnalyticsEvent.NumberOf(values, "callCount") is { } calls) endpointCalls += calls;
                if (PlayerAnalyticsEvent.NumberOf(values, "failureCount") is { } fails) endpointFailures += fails;
            }
        }

        var sortedSignals = signals.Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var sortedSnapshots = snapshotSections.Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal).ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceCount"] = sortedSignals.Count,
            ["sources"] = sortedSignals.Select(s => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = s, ["label"] = ManagedSignalLabel(s),
            }).ToList(),
            ["snapshotChangeCount"] = sortedSnapshots.Count,
            ["snapshotSections"] = sortedSnapshots.Select(s => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = s, ["label"] = ManagedSignalLabel(s),
            }).ToList(),
            ["incidentCount"] = incidentCount,
            ["endpointCalls"] = (int)Math.Round(endpointCalls, MidpointRounding.AwayFromZero),
            ["endpointFailures"] = (int)Math.Round(endpointFailures, MidpointRounding.AwayFromZero),
            ["voiceSeconds"] = (int)Math.Round(voiceSeconds, MidpointRounding.AwayFromZero),
            ["maxConnections"] = (int)Math.Round(maxConnections, MidpointRounding.AwayFromZero),
            ["maxPingMs"] = (int)Math.Round(maxPingMs, MidpointRounding.AwayFromZero),
            ["memoryMb"] = memoryMb != 0 ? (int)Math.Round(memoryMb, MidpointRounding.AwayFromZero) : 0,
        };
    }

    private static void AddTelemetrySignals(HashSet<string> signals, JsonElement telemetry)
    {
        void AddKeys(string key)
        {
            var obj = PlayerAnalyticsEvent.ObjectSection(telemetry, key);
            if (obj.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in obj.EnumerateObject()) signals.Add(p.Name);
            }
        }
        void AddArray(string key)
        {
            if (telemetry.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) signals.Add(item.GetString() ?? string.Empty);
                }
            }
        }
        AddKeys("snapshotHashes");
        AddArray("changedSnapshotKeys");
        AddArray("unchangedSnapshotKeys");
        AddKeys("metrics");
    }
}
