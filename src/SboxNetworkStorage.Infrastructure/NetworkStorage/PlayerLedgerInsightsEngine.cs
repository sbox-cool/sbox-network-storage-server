using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Pure port of the player ledger-insights aggregation engine from
/// <c>services/network-storage-player-analytics.js</c>
/// (<c>buildPlayerLedgerInsights</c> + <c>buildLedgerMilestones</c> +
/// correlation/session-context helpers). Operates over the in-memory event /
/// session list; performs no I/O so it can be unit-tested for parity directly.
/// </summary>
internal static class PlayerLedgerInsightsEngine
{
    internal readonly record struct Options(
        string FieldKey,
        int PointLimit,
        int MilestoneLimit,
        double CorrelationBeforeSeconds,
        double CorrelationAfterSeconds,
        double AbsoluteThreshold,
        double ProportionalThreshold,
        double SessionNetAbsoluteThreshold,
        int NearbyLimit);

    private static double Clamp(double value, double fallback, double min, double max)
        => !double.IsFinite(value) ? fallback : Math.Max(min, Math.Min(max, value));

    /// <summary>Parity: <c>normalizeLedgerInsightOptions</c>.</summary>
    public static Options NormalizeOptions(IReadOnlyDictionary<string, double?>? raw, string? fieldKey)
    {
        double Get(string key, double fallback, double min, double max)
        {
            if (raw is not null && raw.TryGetValue(key, out var v) && v.HasValue) return Clamp(v.Value, fallback, min, max);
            return fallback;
        }

        double? correlationWindow = raw is not null && raw.TryGetValue("correlationWindowSeconds", out var w) ? w : null;
        var before = correlationWindow ?? Get("correlationBeforeSeconds", 30, 0, 300);
        var after = correlationWindow ?? Get("correlationAfterSeconds", 10, 0, 300);

        return new Options(
            FieldKey: (fieldKey ?? string.Empty).Trim(),
            PointLimit: (int)Math.Round(Get("pointLimit", 500, 25, 2000), MidpointRounding.AwayFromZero),
            MilestoneLimit: (int)Math.Round(Get("milestoneLimit", 80, 10, 250), MidpointRounding.AwayFromZero),
            CorrelationBeforeSeconds: Clamp(before, 30, 0, 300),
            CorrelationAfterSeconds: Clamp(after, 10, 0, 300),
            AbsoluteThreshold: Get("absoluteThreshold", 1, 0, long.MaxValue),
            ProportionalThreshold: Get("proportionalThreshold", 0.25, 0, 10),
            SessionNetAbsoluteThreshold: Get("sessionNetAbsoluteThreshold", 100, 0, long.MaxValue),
            NearbyLimit: (int)Math.Round(Get("nearbyLimit", 6, 0, 20), MidpointRounding.AwayFromZero));
    }

    private static bool IsTrackedFieldDelta(PlayerAnalyticsEvent e)
        => e.Category == "tracked_field" || e.Type == "tracked_field.delta";

    private static string LedgerFieldPath(PlayerAnalyticsEvent e)
    {
        var fromPayload = e.HasPayload ? PlayerAnalyticsEvent.StrOf(e.Payload, "fieldPath") : string.Empty;
        if (fromPayload.Length > 0) return fromPayload.Trim();
        var direct = e.Str("fieldPath");
        if (direct.Length > 0) return direct.Trim();
        return e.Label.Trim();
    }

    private static string LedgerFieldKeyOf(PlayerAnalyticsEvent e)
    {
        var collectionId = e.CollectionId;
        if (collectionId.Length == 0 && e.HasPayload) collectionId = PlayerAnalyticsEvent.StrOf(e.Payload, "collectionId");
        return $"{collectionId.Trim()}:{LedgerFieldPath(e)}";
    }

    private static bool FieldMatchesFilter(Series series, string fieldKey)
    {
        if (fieldKey.Length == 0) return true;
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            series.Key,
            $"{series.CollectionId}.{series.FieldPath}",
            $"{series.CollectionId}:{series.FieldPath}",
            series.FieldPath,
            series.Label,
        };
        return candidates.Contains(fieldKey);
    }

    private static long? EventMs(PlayerAnalyticsEvent e) => e.TimestampMs;

    private static string NearbyLabel(PlayerAnalyticsEvent e)
    {
        if (e.Label.Length > 0) return e.Label;
        if (e.EndpointSlug.Length > 0) return e.EndpointSlug;
        if (e.Type.Length > 0) return e.Type;
        return e.Category.Length > 0 ? e.Category : "activity";
    }

    private static string EventCategory(string type)
    {
        var raw = type.Length == 0 ? "custom" : type;
        if (raw.StartsWith("session.", StringComparison.Ordinal)) return "session";
        if (raw.StartsWith("endpoint.", StringComparison.Ordinal)) return "endpoint";
        if (raw.StartsWith("storage.", StringComparison.Ordinal)) return "storage";
        if (raw.StartsWith("tracked_field.", StringComparison.Ordinal)) return "tracked_field";
        if (raw.StartsWith("warning.", StringComparison.Ordinal)) return "warning";
        if (raw.StartsWith("error.", StringComparison.Ordinal)) return "error";
        if (raw.StartsWith("custom.", StringComparison.Ordinal)) return "custom";
        var head = raw.Split('.')[0];
        return head.Length > 0 ? head : "custom";
    }

    /// <summary>Parity: <c>compactNearbyPayload</c> + <c>boundAnalyticsPayload</c> for the small scalar set.</summary>
    private static Dictionary<string, object?> CompactNearbyPayload(PlayerAnalyticsEvent e)
    {
        var payload = e.Payload;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload.ValueKind != JsonValueKind.Object) return result;

        void CopyScalar(string key)
        {
            if (payload.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                // boundAnalyticsPayload drops undefined/empty; keep present scalar/object values.
                if (v.ValueKind == JsonValueKind.String && (v.GetString() ?? string.Empty).Length == 0) return;
                result[key] = v;
            }
        }

        CopyScalar("code");
        CopyScalar("message");
        CopyScalar("severity");
        CopyScalar("status");
        CopyScalar("timingMs");
        CopyScalar("method");
        CopyScalar("section");
        // managedTelemetry: payload.managedTelemetry || payload.context?.managedTelemetry
        if (payload.TryGetProperty("managedTelemetry", out var mt) && mt.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            result["managedTelemetry"] = mt;
        }
        else if (PlayerAnalyticsEvent.ObjectSection(payload, "context") is { ValueKind: JsonValueKind.Object } ctx
                 && ctx.TryGetProperty("managedTelemetry", out var ctxMt)
                 && ctxMt.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            result["managedTelemetry"] = ctxMt;
        }
        CopyScalar("delta");
        return result;
    }

    private static Dictionary<string, object?> CompactNearbyActivity(PlayerAnalyticsEvent e, long? deltaMs)
    {
        var eventMs = EventMs(e);
        int? relative = eventMs.HasValue && deltaMs.HasValue
            ? (int)Math.Round((eventMs.Value - deltaMs.Value) / 1000.0, MidpointRounding.AwayFromZero)
            : null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = e.Id,
            ["ts"] = e.Ts.Length > 0 ? e.Ts : null,
            ["type"] = e.Type.Length > 0 ? e.Type : null,
            ["category"] = e.Category.Length > 0 ? e.Category : EventCategory(e.Type),
            ["label"] = NearbyLabel(e),
            ["endpointSlug"] = e.EndpointSlug,
            ["collectionId"] = e.CollectionId,
            ["ok"] = e.OkNotFalse,
            ["sessionId"] = e.SessionId.Length > 0 ? e.SessionId : null,
            ["relativeSeconds"] = relative,
            ["payload"] = CompactNearbyPayload(e),
        };
    }

    private static List<Dictionary<string, object?>> CorrelateNearbyActivity(
        PlayerAnalyticsEvent delta, IReadOnlyList<PlayerAnalyticsEvent> timeline, Options options)
    {
        var deltaMs = EventMs(delta);
        if (!deltaMs.HasValue || options.NearbyLimit <= 0) return new List<Dictionary<string, object?>>();
        var beforeMs = options.CorrelationBeforeSeconds * 1000;
        var afterMs = options.CorrelationAfterSeconds * 1000;
        var deltaSession = delta.SessionId;

        return timeline
            .Where(e => !ReferenceEquals(e, delta) && !IsTrackedFieldDelta(e))
            .Select(e => (Event: e, Ms: EventMs(e)))
            .Where(row => row.Ms.HasValue && row.Ms.Value >= deltaMs.Value - beforeMs && row.Ms.Value <= deltaMs.Value + afterMs)
            .OrderBy(row => row, Comparer<(PlayerAnalyticsEvent Event, long? Ms)>.Create((a, b) =>
            {
                var sameA = a.Event.SessionId.Length > 0 && a.Event.SessionId == deltaSession ? 0 : 1;
                var sameB = b.Event.SessionId.Length > 0 && b.Event.SessionId == deltaSession ? 0 : 1;
                if (sameA != sameB) return sameA - sameB;
                var distA = Math.Abs(a.Ms!.Value - deltaMs.Value);
                var distB = Math.Abs(b.Ms!.Value - deltaMs.Value);
                if (distA != distB) return distA.CompareTo(distB);
                return string.CompareOrdinal(a.Event.Ts, b.Event.Ts);
            }))
            .Take(options.NearbyLimit)
            .Select(row => CompactNearbyActivity(row.Event, deltaMs))
            .ToList();
    }

    private static Dictionary<string, object?> SessionContextFor(PlayerAnalyticsEvent e, IReadOnlyDictionary<string, JsonElement> sessionsById)
    {
        var sessionId = e.SessionId;
        sessionsById.TryGetValue(sessionId, out var session);
        var startedAt = session.ValueKind == JsonValueKind.Object ? PlayerAnalyticsEvent.StrOf(session, "startedAt") : string.Empty;
        var endedAt = session.ValueKind == JsonValueKind.Object ? PlayerAnalyticsEvent.StrOf(session, "endedAt") : string.Empty;

        var payload = e.Payload;
        var payloadSeconds = PlayerAnalyticsEvent.NumberOf(payload, "sessionSeconds")
                             ?? PlayerAnalyticsEvent.NumberOf(payload, "durationSeconds") ?? 0;
        var eventMs = EventMs(e);
        long? startedMs = null;
        if (startedAt.Length > 0 && DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var s))
        {
            startedMs = s.ToUnixTimeMilliseconds();
        }

        int? relative;
        if (double.IsFinite(payloadSeconds) && payloadSeconds > 0)
        {
            relative = (int)Math.Round(payloadSeconds, MidpointRounding.AwayFromZero);
        }
        else if (eventMs.HasValue && startedMs.HasValue && eventMs.Value >= startedMs.Value)
        {
            relative = (int)Math.Round((eventMs.Value - startedMs.Value) / 1000.0, MidpointRounding.AwayFromZero);
        }
        else
        {
            relative = null;
        }

        // Object.fromEntries(filter(value !== null && value !== "")).
        var context = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sessionId)) context["sessionId"] = sessionId;
        if (!string.IsNullOrEmpty(startedAt)) context["startedAt"] = startedAt;
        if (!string.IsNullOrEmpty(endedAt)) context["endedAt"] = endedAt;
        if (relative.HasValue) context["relativeSeconds"] = relative.Value;
        return context;
    }

    private sealed class Point
    {
        public required string Id { get; init; }
        public string? Ts { get; init; }
        public double? Value { get; init; }
        public double? Before { get; init; }
        public double? After { get; init; }
        public double Delta { get; init; }
        public string Source { get; init; } = "server";
        public string Key { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string Direction { get; init; } = "unchanged";
        public List<Dictionary<string, object?>> NearbyActivity { get; init; } = new();
        public Dictionary<string, object?> SessionContext { get; init; } = new();

        public Dictionary<string, object?> ToMap() => new(StringComparer.Ordinal)
        {
            ["id"] = Id,
            ["ts"] = Ts,
            ["value"] = Value,
            ["before"] = Before,
            ["after"] = After,
            ["delta"] = Delta,
            ["source"] = Source,
            ["key"] = Key,
            ["sessionId"] = SessionId,
            ["direction"] = Direction,
            ["nearbyActivity"] = NearbyActivity,
            ["sessionContext"] = SessionContext,
        };
    }

    private sealed class Series
    {
        public required string Key { get; init; }
        public required string CollectionId { get; init; }
        public required string FieldPath { get; init; }
        public required string Label { get; init; }
        public string Kind { get; init; } = "stat";
        public string Source { get; init; } = "tracked_field";
        public string? FirstSeen { get; init; }
        public string? LastSeen { get; set; }
        public List<Point> Points { get; } = new();
        public int TotalPointCount { get; set; }
    }

    private static Point CreatePoint(PlayerAnalyticsEvent e, Series series,
        List<Dictionary<string, object?>> nearby, Dictionary<string, object?> sessionContext)
    {
        var payload = e.Payload;
        var before = PlayerAnalyticsEvent.NumberOf(payload, "before");
        var after = PlayerAnalyticsEvent.NumberOf(payload, "after");
        var delta = PlayerAnalyticsEvent.NumberOf(payload, "delta") ?? 0;
        return new Point
        {
            Id = e.Id ?? $"{series.Key}:{e.Ts}:{series.Points.Count}",
            Ts = e.Ts.Length > 0 ? e.Ts : null,
            Value = after,
            Before = before,
            After = after,
            Delta = double.IsFinite(delta) ? delta : 0,
            Source = e.Source.Length > 0 ? e.Source : "server",
            Key = e.Key,
            SessionId = e.SessionId.Length > 0 ? e.SessionId : null,
            Direction = delta > 0 ? "increase" : delta < 0 ? "decrease" : "unchanged",
            NearbyActivity = nearby,
            SessionContext = sessionContext,
        };
    }

    private static Dictionary<string, object?> SummarizeSeries(Series series, int totalPointCount, int omittedCount)
    {
        var valueCandidates = new List<double>();
        foreach (var p in series.Points)
        {
            if (p.Before is { } b && double.IsFinite(b)) valueCandidates.Add(b);
            if (p.After is { } a && double.IsFinite(a)) valueCandidates.Add(a);
        }
        var firstPoint = series.Points.Count > 0 ? series.Points[0] : null;
        var latestPoint = series.Points.Count > 0 ? series.Points[^1] : null;
        double? firstValue = firstPoint is null
            ? null
            : (firstPoint.Before is { } fb && double.IsFinite(fb) ? fb : firstPoint.After);
        double? latestValue = latestPoint is null
            ? null
            : (latestPoint.After is { } la && double.IsFinite(la) ? la : latestPoint.Value);
        var totalIncrease = series.Points.Sum(p => Math.Max(0, p.Delta));
        var totalDecrease = series.Points.Sum(p => Math.Min(0, p.Delta));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["firstValue"] = firstValue,
            ["latestValue"] = latestValue,
            ["minValue"] = valueCandidates.Count > 0 ? valueCandidates.Min() : null,
            ["maxValue"] = valueCandidates.Count > 0 ? valueCandidates.Max() : null,
            ["totalIncrease"] = totalIncrease,
            ["totalDecrease"] = totalDecrease,
            ["netChange"] = totalIncrease + totalDecrease,
            ["pointCount"] = totalPointCount,
            ["returnedPointCount"] = series.Points.Count,
            ["omittedCount"] = omittedCount,
        };
    }

    private static void AddMovementMilestone(List<Dictionary<string, object?>> milestones, Series series, Point point, Options options)
    {
        var delta = point.Delta;
        var before = point.Before ?? 0;
        double? ratio = before != 0 ? Math.Abs(delta) / Math.Abs(before) : null;
        var absoluteHit = Math.Abs(delta) >= options.AbsoluteThreshold;
        var proportionalHit = ratio.HasValue && ratio.Value >= options.ProportionalThreshold;
        if (!absoluteHit && !proportionalHit) return;
        var kind = delta >= 0 ? "increase" : "decrease";
        milestones.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = $"movement:{series.Key}:{point.Id}",
            ["kind"] = kind,
            ["fieldKey"] = series.Key,
            ["fieldPath"] = series.FieldPath,
            ["collectionId"] = series.CollectionId,
            ["label"] = series.Label,
            ["ts"] = point.Ts,
            ["delta"] = delta,
            ["before"] = point.Before,
            ["after"] = point.After,
            ["ratio"] = ratio,
            ["threshold"] = !ratio.HasValue || !proportionalHit
                ? new Dictionary<string, object?> { ["type"] = "absolute", ["value"] = options.AbsoluteThreshold }
                : new Dictionary<string, object?> { ["type"] = "proportional", ["value"] = options.ProportionalThreshold },
            ["nearbyActivity"] = point.NearbyActivity,
            ["sessionContext"] = point.SessionContext,
        });
    }

    private static void AddSessionMilestones(List<Dictionary<string, object?>> milestones, Series series, Options options)
    {
        var bySession = new Dictionary<string, List<Point>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var point in series.Points)
        {
            var sessionId = point.SessionId ?? "no-session";
            if (!bySession.TryGetValue(sessionId, out var list))
            {
                list = new List<Point>();
                bySession[sessionId] = list;
                order.Add(sessionId);
            }
            list.Add(point);
        }
        foreach (var sessionId in order)
        {
            var points = bySession[sessionId];
            if (points.Count < 2 && sessionId == "no-session") continue;
            var totalIncrease = points.Sum(p => Math.Max(0, p.Delta));
            var totalDecrease = points.Sum(p => Math.Min(0, p.Delta));
            var netChange = totalIncrease + totalDecrease;
            if (Math.Abs(netChange) < options.SessionNetAbsoluteThreshold) continue;
            var last = points[^1];
            milestones.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = $"session-net:{series.Key}:{sessionId}:{points[0].Ts ?? string.Empty}",
                ["kind"] = netChange >= 0 ? "session_net_increase" : "session_net_decrease",
                ["fieldKey"] = series.Key,
                ["fieldPath"] = series.FieldPath,
                ["collectionId"] = series.CollectionId,
                ["label"] = series.Label,
                ["ts"] = last.Ts ?? points[0].Ts,
                ["sessionId"] = sessionId,
                ["totalIncrease"] = totalIncrease,
                ["totalDecrease"] = totalDecrease,
                ["netChange"] = netChange,
                ["eventCount"] = points.Count,
                ["threshold"] = new Dictionary<string, object?> { ["type"] = "session_net_absolute", ["value"] = options.SessionNetAbsoluteThreshold },
                ["nearbyActivity"] = last.NearbyActivity,
                ["sessionContext"] = last.SessionContext,
            });
        }
    }

    private static List<Dictionary<string, object?>> BuildMilestones(IReadOnlyList<Series> seriesList, Options options)
    {
        var milestones = new List<Dictionary<string, object?>>();
        foreach (var series in seriesList)
        {
            double? runningMin = null;
            double? runningMax = null;
            foreach (var point in series.Points)
            {
                AddMovementMilestone(milestones, series, point, options);
                if (point.After is not { } after || !double.IsFinite(after)) continue;
                if (runningMin is null || after < runningMin)
                {
                    if (runningMin is not null)
                    {
                        milestones.Add(LocalExtreme("local-min", "local_min", series, point, after));
                    }
                    runningMin = after;
                }
                if (runningMax is null || after > runningMax)
                {
                    if (runningMax is not null)
                    {
                        milestones.Add(LocalExtreme("local-max", "local_max", series, point, after));
                    }
                    runningMax = after;
                }
            }
            AddSessionMilestones(milestones, series, options);
        }
        return milestones
            .OrderByDescending(m => m.TryGetValue("ts", out var ts) ? ts as string ?? string.Empty : string.Empty, StringComparer.Ordinal)
            .Take(options.MilestoneLimit)
            .ToList();
    }

    private static Dictionary<string, object?> LocalExtreme(string idPrefix, string kind, Series series, Point point, double after)
        => new(StringComparer.Ordinal)
        {
            ["id"] = $"{idPrefix}:{series.Key}:{point.Id}",
            ["kind"] = kind,
            ["fieldKey"] = series.Key,
            ["label"] = series.Label,
            ["ts"] = point.Ts,
            ["after"] = after,
            ["delta"] = point.Delta,
            ["nearbyActivity"] = point.NearbyActivity,
            ["sessionContext"] = point.SessionContext,
        };

    /// <summary>
    /// Parity: <c>buildPlayerLedgerInsights({ events, sessions, profile, options })</c>.
    /// </summary>
    public static Dictionary<string, object?> Build(
        IReadOnlyList<PlayerAnalyticsEvent> events,
        IReadOnlyList<JsonElement> sessions,
        JsonElement? profile,
        Options options)
    {
        var sessionsById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session.ValueKind != JsonValueKind.Object) continue;
            sessionsById[PlayerAnalyticsEvent.StrOf(session, "sessionId")] = session;
        }

        // timeline = events sorted ascending by ts (stable).
        var timeline = events
            .OrderBy(e => e.Ts, StringComparer.Ordinal)
            .ToList();

        var fields = new Dictionary<string, Series>(StringComparer.Ordinal);
        var fieldOrder = new List<string>();

        foreach (var e in timeline.Where(IsTrackedFieldDelta))
        {
            var fieldPath = LedgerFieldPath(e);
            var after = e.HasPayload ? PlayerAnalyticsEvent.NumberOf(e.Payload, "after") : null;
            if (fieldPath.Length == 0 || after is null || !double.IsFinite(after.Value)) continue;
            var collectionId = e.CollectionId;
            if (collectionId.Length == 0 && e.HasPayload) collectionId = PlayerAnalyticsEvent.StrOf(e.Payload, "collectionId");
            collectionId = collectionId.Trim();
            var key = LedgerFieldKeyOf(e);
            if (!fields.TryGetValue(key, out var series))
            {
                var payloadLabel = e.HasPayload ? PlayerAnalyticsEvent.StrOf(e.Payload, "label") : string.Empty;
                var label = payloadLabel.Length > 0 ? payloadLabel : (e.Label.Length > 0 ? e.Label : fieldPath);
                var kind = e.HasPayload ? PlayerAnalyticsEvent.StrOf(e.Payload, "kind") : string.Empty;
                var source = e.HasPayload ? PlayerAnalyticsEvent.StrOf(e.Payload, "source") : string.Empty;
                if (source.Length == 0) source = e.Source;
                series = new Series
                {
                    Key = key,
                    CollectionId = collectionId,
                    FieldPath = fieldPath,
                    Label = label,
                    Kind = kind.Length > 0 ? kind : "stat",
                    Source = source.Length > 0 ? source : "tracked_field",
                    FirstSeen = e.Ts.Length > 0 ? e.Ts : null,
                    LastSeen = e.Ts.Length > 0 ? e.Ts : null,
                };
                fields[key] = series;
                fieldOrder.Add(key);
            }
            if (e.Ts.Length > 0) series.LastSeen = e.Ts;
            series.TotalPointCount += 1;
            var point = CreatePoint(e, series,
                CorrelateNearbyActivity(e, timeline, options),
                SessionContextFor(e, sessionsById));
            series.Points.Add(point);
        }

        var orderedSeries = fieldOrder
            .Select(k => fields[k])
            .Where(s => FieldMatchesFilter(s, options.FieldKey))
            .OrderByDescending(s => s.LastSeen ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.CollectionId, StringComparer.Ordinal)
            .ThenBy(s => s.FieldPath, StringComparer.Ordinal)
            .ToList();

        var publicSeries = new List<Dictionary<string, object?>>();
        foreach (var series in orderedSeries)
        {
            var total = series.TotalPointCount > 0 ? series.TotalPointCount : series.Points.Count;
            var returned = series.Points.Count > options.PointLimit
                ? series.Points.GetRange(series.Points.Count - options.PointLimit, options.PointLimit)
                : series.Points;
            var omitted = Math.Max(0, total - returned.Count);

            // summarizeLedgerSeries is computed over the FULL points, then returnedPointCount overridden.
            var summary = SummarizeSeries(series, total, omitted);
            summary["returnedPointCount"] = returned.Count;

            publicSeries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = series.Key,
                ["collectionId"] = series.CollectionId,
                ["fieldPath"] = series.FieldPath,
                ["label"] = series.Label,
                ["kind"] = series.Kind,
                ["source"] = series.Source,
                ["firstSeen"] = series.FirstSeen,
                ["lastSeen"] = series.LastSeen,
                ["points"] = returned.Select(p => p.ToMap()).ToList(),
                ["omittedCount"] = omitted,
                ["summary"] = summary,
            });
        }

        // Milestones operate on the returned (sliced) points — parity: buildLedgerMilestones(seriesList)
        // is called with the public sliced series. Rebuild lightweight Series from returned points.
        var milestoneSeries = new List<Series>();
        for (var i = 0; i < orderedSeries.Count; i++)
        {
            var src = orderedSeries[i];
            var returnedPoints = (List<Dictionary<string, object?>>)publicSeries[i]["points"]!;
            var rebuilt = new Series
            {
                Key = src.Key,
                CollectionId = src.CollectionId,
                FieldPath = src.FieldPath,
                Label = src.Label,
                Kind = src.Kind,
                Source = src.Source,
                FirstSeen = src.FirstSeen,
                LastSeen = src.LastSeen,
            };
            var sliceStart = src.Points.Count - returnedPoints.Count;
            for (var j = 0; j < returnedPoints.Count; j++)
            {
                rebuilt.Points.Add(src.Points[sliceStart + j]);
            }
            milestoneSeries.Add(rebuilt);
        }
        var milestones = BuildMilestones(milestoneSeries, options);

        var thresholds = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["absolute"] = options.AbsoluteThreshold,
            ["proportional"] = options.ProportionalThreshold,
            ["sessionNetAbsolute"] = options.SessionNetAbsoluteThreshold,
            ["correlationBeforeSeconds"] = options.CorrelationBeforeSeconds,
            ["correlationAfterSeconds"] = options.CorrelationAfterSeconds,
        };

        Dictionary<string, object?>? profileSummary = null;
        if (profile is { ValueKind: JsonValueKind.Object } p)
        {
            var totalSeconds = PlayerAnalyticsEvent.NumberOf(p, "totalSeconds") ?? 0;
            var effective = PlayerAnalyticsEvent.NumberOf(p, "effectiveTotalSeconds") ?? totalSeconds;
            profileSummary = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["steamId"] = PlayerAnalyticsEvent.StrOf(p, "steamId") is { Length: > 0 } sid ? sid : null,
                ["totalSeconds"] = totalSeconds,
                ["effectiveTotalSeconds"] = effective,
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hasData"] = publicSeries.Count > 0,
            ["fieldCount"] = publicSeries.Count,
            ["pointCount"] = publicSeries.Sum(s => Convert.ToInt32(((Dictionary<string, object?>)s["summary"]!)["pointCount"], CultureInfo.InvariantCulture)),
            ["omittedPointCount"] = publicSeries.Sum(s => Convert.ToInt32(s["omittedCount"], CultureInfo.InvariantCulture)),
            ["selectedFieldKey"] = options.FieldKey.Length > 0 ? options.FieldKey : null,
            ["thresholds"] = thresholds,
            ["profile"] = profileSummary,
            ["series"] = publicSeries,
            ["milestones"] = milestones,
        };
    }
}
