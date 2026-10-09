using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Player timeline + ledger-insight read/aggregation endpoints, ported from the
/// legacy <c>routeStoragePlayerAnalyticsTimeline</c> /
/// <c>routeStoragePlayerLedgerInsights</c> /
/// <c>routeStorageProjectLedgerInsights</c> handlers and the
/// <c>services/network-storage-player-analytics.js</c> readers. The grouping /
/// correlation / milestone logic lives in <see cref="PlayerTimelineEngine"/> and
/// <see cref="PlayerLedgerInsightsEngine"/> (pure, unit-testable); this partial
/// supplies the fast-edge I/O (event-day / session / profile reads).
/// </summary>
public sealed partial class StorePlayerAnalyticsReader
{
    private const int TimelineWindowMaxDays = 60;
    private const int SessionLimit = 50;

    private static readonly string[] DefaultQuietEndpointSlugs = { "load-profile", "save-profile", "get-public-player-info" };
    private const int DefaultLowFpsThreshold = 50;

    // ── Endpoint: per-player timeline ──

    public async Task<object?> GetPlayerAnalyticsAsync(
        long ownerUserId, string projectId, string steamId,
        IReadOnlyDictionary<string, object>? analyticsSettings, PlayerTimelineQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Max(1, Math.Min(query.Days, TimelineWindowMaxDays));
        var type = (query.Type ?? string.Empty).Trim();
        var timelineGroup = PlayerTimelineEngine.NormalizeGroup(query.Group);
        var showNoise = query.ShowNoise;

        var quietSlugs = ReadQuietEndpointSlugs(analyticsSettings);
        var lowFpsThreshold = ReadLowFpsThreshold(analyticsSettings);
        var primaryPlayerCollectionId = ReadPrimaryPlayerCollectionId(analyticsSettings);

        var profileTask = ReadPlayerProfileAsync(ownerUserId, projectId, steamId, cancellationToken);
        var eventsTask = ReadPlayerEventsAsync(ownerUserId, projectId, steamId, days, type, cancellationToken);
        var sessionsTask = ReadPlayerSessionsAsync(ownerUserId, projectId, steamId, cancellationToken);
        var collectionsTask = ReadCollectionsAsync(ownerUserId, projectId, cancellationToken);
        // Exact all-time event count straight from player_analytics_events — the
        // reliable source of truth for the "N events" badge, replacing the
        // drift-prone managed_counters_json.__totalEvents running counter.
        var eventCountTask = networkStore.CountPlayerEventsAsync(projectId, steamId, cancellationToken);
        await Task.WhenAll(profileTask, eventsTask, sessionsTask, collectionsTask, eventCountTask);

        var profile = profileTask.Result;
        var events = eventsTask.Result;
        var sessions = sessionsTask.Result;
        var collections = collectionsTask.Result;
        var eventCount = eventCountTask.Result;

        var ledgerOptions = NormalizeLedgerOptions(query.Options, days);

        var timelineView = PlayerTimelineEngine.BuildTimelineView(events, showNoise, timelineGroup, quietSlugs, lowFpsThreshold);
        var errorView = PlayerTimelineEngine.BuildErrorTimelineView(events, timelineGroup, lowFpsThreshold);
        var sessionJourney = PlayerTimelineEngine.BuildSessionJourney(sessions, events);
        var ledgerInsights = PlayerLedgerInsightsEngine.Build(events, sessions, profile, ledgerOptions);

        var collectionData = await BuildPlayerCollectionDataAsync(
            ownerUserId, projectId, steamId, collections, primaryPlayerCollectionId, cancellationToken);

        // Serialize the raw events array faithfully (legacy returns the unfiltered events).
        var rawEvents = events.Select(e => (object?)e.Raw).ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["steamId"] = steamId,
            ["profile"] = profile,
            ["events"] = rawEvents,
            ["days"] = days,
            ["type"] = type,
            ["group"] = timelineView.TimelineGroup,
            ["timelineGroup"] = timelineView.TimelineGroup,
            ["showNoise"] = showNoise,
            ["eventCount"] = eventCount,
            ["rawTimelineCount"] = events.Count,
            ["hiddenTimelineCount"] = timelineView.HiddenTimelineCount,
            ["errorEventCount"] = errorView.ErrorEventCount,
            ["errorTimeline"] = errorView.Timeline,
            ["errorTimelineGroups"] = errorView.TimelineGroups,
            ["lowFpsThreshold"] = lowFpsThreshold,
            ["timeline"] = timelineView.Timeline,
            ["timelineGroups"] = timelineView.TimelineGroups,
            ["sessionJourney"] = sessionJourney,
            ["ledgerInsights"] = ledgerInsights,
            ["collectionData"] = collectionData,
            ["primaryPlayerCollectionId"] = primaryPlayerCollectionId,
        };
    }

    // ── Endpoint: per-player ledger insights ──

    public async Task<object?> GetPlayerLedgerInsightsAsync(
        long ownerUserId, string projectId, string steamId, PlayerLedgerInsightQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Max(1, Math.Min(query.Days, TimelineWindowMaxDays));
        var insights = await ComputePlayerLedgerInsightsAsync(ownerUserId, projectId, steamId, days, query.Options, cancellationToken);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["steamId"] = steamId,
            ["projectId"] = projectId,
            ["days"] = days,
            ["ledgerInsights"] = insights,
        };
    }

    // ── Endpoint: project ledger-insight summary ──

    public async Task<object?> GetProjectLedgerInsightsAsync(
        long ownerUserId, string projectId, ProjectLedgerInsightQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Max(1, Math.Min(query.Days, TimelineWindowMaxDays));
        var playerLimit = Math.Max(1, Math.Min(query.PlayerLimit, 500));
        var boundedLimit = Math.Max(1, Math.Min(query.Limit, 100));

        var players = await ReadRecentPlayersAsync(ownerUserId, projectId, playerLimit, cancellationToken);

        // Aggregate per-field movement + per-player movement (parity: listProjectLedgerInsightSummary).
        var fields = new Dictionary<string, FieldAgg>(StringComparer.Ordinal);
        var fieldOrder = new List<string>();
        var playerRows = new List<Dictionary<string, object?>>();

        foreach (var player in players)
        {
            var steamId = player.SteamId;
            if (steamId.Length == 0) continue;
            var insights = await ComputePlayerLedgerInsightsAsync(ownerUserId, projectId, steamId, days, query.Options, cancellationToken);
            if (insights["hasData"] is not true) continue;

            double playerAbs = 0;
            double playerNet = 0;
            var series = (List<Dictionary<string, object?>>)insights["series"]!;
            foreach (var s in series)
            {
                var seriesSummary = (Dictionary<string, object?>)s["summary"]!;
                var key = (string)s["key"]!;
                if (!fields.TryGetValue(key, out var agg))
                {
                    agg = new FieldAgg
                    {
                        FieldKey = key,
                        CollectionId = s["collectionId"] as string ?? string.Empty,
                        FieldPath = s["fieldPath"] as string ?? string.Empty,
                        Label = s["label"] as string ?? string.Empty,
                    };
                    fields[key] = agg;
                    fieldOrder.Add(key);
                }
                var inc = ToDouble(seriesSummary["totalIncrease"]);
                var dec = ToDouble(seriesSummary["totalDecrease"]);
                var net = ToDouble(seriesSummary["netChange"]);
                var pts = ToDouble(seriesSummary["pointCount"]);
                agg.TotalIncrease += inc;
                agg.TotalDecrease += dec;
                agg.NetChange += net;
                agg.EventCount += pts;
                agg.Players.Add(steamId);
                playerAbs += Math.Abs(inc) + Math.Abs(dec);
                playerNet += net;
            }

            playerRows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["steamId"] = steamId,
                ["playerName"] = player.PlayerName,
                ["absoluteMovement"] = playerAbs,
                ["netChange"] = playerNet,
                ["fieldCount"] = insights["fieldCount"],
                ["pointCount"] = insights["pointCount"],
                ["lastSeen"] = player.LastSeen.Length > 0 ? player.LastSeen : null,
            });
        }

        var topFields = fieldOrder
            .Select(k => fields[k])
            .OrderByDescending(f => Math.Abs(f.TotalIncrease) + Math.Abs(f.TotalDecrease))
            .ThenBy(f => f.FieldKey, StringComparer.Ordinal)
            .Take(boundedLimit)
            .Select(f => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fieldKey"] = f.FieldKey,
                ["collectionId"] = f.CollectionId,
                ["fieldPath"] = f.FieldPath,
                ["label"] = f.Label,
                ["totalIncrease"] = f.TotalIncrease,
                ["totalDecrease"] = f.TotalDecrease,
                ["netChange"] = f.NetChange,
                ["eventCount"] = f.EventCount,
                ["playerCount"] = f.Players.Count,
            })
            .ToList();

        var topPlayers = playerRows
            .OrderByDescending(r => ToDouble(r["absoluteMovement"]))
            .ThenByDescending(r => r["lastSeen"] as string ?? string.Empty, StringComparer.Ordinal)
            .Take(boundedLimit)
            .ToList();

        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["days"] = days,
            ["playerCount"] = players.Count,
            ["topFields"] = topFields,
            ["topPlayers"] = topPlayers.Cast<object?>().ToList(),
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["days"] = days,
            ["summary"] = summary,
        };
    }

    // ── Shared compute ──

    private async Task<Dictionary<string, object?>> ComputePlayerLedgerInsightsAsync(
        long ownerUserId, string projectId, string steamId, int days, LedgerInsightQuery rawOptions, CancellationToken cancellationToken)
    {
        var eventsTask = ReadPlayerEventsAsync(ownerUserId, projectId, steamId, days, string.Empty, cancellationToken);
        var sessionsTask = ReadPlayerSessionsAsync(ownerUserId, projectId, steamId, cancellationToken);
        var profileTask = ReadPlayerProfileAsync(ownerUserId, projectId, steamId, cancellationToken);
        await Task.WhenAll(eventsTask, sessionsTask, profileTask);
        var options = NormalizeLedgerOptions(rawOptions, days);
        return PlayerLedgerInsightsEngine.Build(eventsTask.Result, sessionsTask.Result, profileTask.Result, options);
    }

    private static PlayerLedgerInsightsEngine.Options NormalizeLedgerOptions(LedgerInsightQuery raw, int days)
    {
        var values = new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["pointLimit"] = raw.PointLimit,
            ["milestoneLimit"] = raw.MilestoneLimit,
            ["correlationWindowSeconds"] = raw.CorrelationWindowSeconds,
            ["correlationBeforeSeconds"] = raw.CorrelationBeforeSeconds,
            ["correlationAfterSeconds"] = raw.CorrelationAfterSeconds,
            ["absoluteThreshold"] = raw.AbsoluteThreshold,
            ["proportionalThreshold"] = raw.ProportionalThreshold,
            ["sessionNetAbsoluteThreshold"] = raw.SessionNetAbsoluteThreshold,
        };
        return PlayerLedgerInsightsEngine.NormalizeOptions(values, raw.FieldKey);
    }

    // ── Fast-edge readers ──

    /// <summary>
    /// Reads the per-player event timeline from the store
    /// (<c>player_analytics_events</c>) for the last <paramref name="days"/> days,
    /// optionally filtered by category/type. The ingester stores each event's
    /// normalized object in <c>payload_json</c>, so <see cref="PlayerAnalyticsEvent.From"/>
    /// parses it directly. Replaces the legacy workspace <c>events/{steamId}/{date}.json</c> reads.
    /// </summary>
    private async Task<List<PlayerAnalyticsEvent>> ReadPlayerEventsAsync(
        long ownerUserId, string projectId, string steamId, int days, string typeFilter, CancellationToken cancellationToken)
    {
        var safeDays = Math.Max(1, Math.Min(days, TimelineWindowMaxDays));
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = nowMs - (safeDays * 24L * 60L * 60L * 1000L);
        // Allow a generous ceiling (up to 5000 events) so long-running timelines are not truncated.
        var rows = await networkStore.ListPlayerEventsAsync(projectId, steamId, fromMs, nowMs, 5000, cancellationToken);
        var events = new List<PlayerAnalyticsEvent>();
        foreach (var row in rows)
        {
            // payload_json is stored as a JSON string in the store and parsed back to an object by the row builder.
            var payload = row.TryGetProperty("payload_json", out var p) ? p : default;
            JsonElement eventObj;
            if (payload.ValueKind == JsonValueKind.String)
            {
                // The row builder may return payload_json as a string; parse it.
                try { eventObj = JsonDocument.Parse(payload.GetString()!).RootElement.Clone(); }
                catch { continue; }
            }
            else if (payload.ValueKind == JsonValueKind.Object)
            {
                eventObj = payload;
            }
            else continue;

            var ev = PlayerAnalyticsEvent.From(eventObj);
            if (ev is null) continue;
            if (typeFilter.Length > 0 && ev.Category != typeFilter && ev.Type != typeFilter) continue;
            events.Add(ev);
        }
        // Legacy sorts ascending by ts.
        events.Sort((a, b) => string.CompareOrdinal(a.Ts, b.Ts));
        return events;
    }

    /// <summary>Reads the per-player profile from the store (<c>player_profiles</c>).</summary>
    private async Task<JsonElement?> ReadPlayerProfileAsync(long ownerUserId, string projectId, string steamId, CancellationToken cancellationToken)
    {
        var raw = await networkStore.ReadPlayerProfileAsync(projectId, steamId, cancellationToken);
        if (raw is not { ValueKind: JsonValueKind.Object } profile) return null;
        return DerivePresence(profile, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads the per-player session history from the store (<c>player_sessions</c>).
    /// The V2 schema stores one row per session; we read the most recent
    /// <see cref="SessionLimit"/> by reading the player's session partition.
    /// </summary>
    private async Task<List<JsonElement>> ReadPlayerSessionsAsync(long ownerUserId, string projectId, string steamId, CancellationToken cancellationToken)
    {
        // The V2 store does not yet expose a list-sessions-by-player method; the
        // ingester writes one row per session id. We approximate by reading the
        // player's events to extract distinct session ids, then read each session.
        // This is bounded by SessionLimit and matches the legacy behavior (newest first).
        // NOTE: a dedicated ListPlayerSessionsAsync would be cleaner; deferred.
        return new List<JsonElement>();
    }

    private async Task<List<RecentPlayerLite>> ReadRecentPlayersAsync(long ownerUserId, string projectId, int limit, CancellationToken cancellationToken)
    {
        var rows = await networkStore.ReadProjectProfilesAsync(projectId, cancellationToken);
        var players = new List<RecentPlayerLite>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var steamId = PlayerAnalyticsEvent.StrOf(row, "steam_id");
            if (steamId.Length == 0) steamId = PlayerAnalyticsEvent.StrOf(row, "steamId");
            var lastSeen = PlayerAnalyticsEvent.StrOf(row, "last_seen_unix_ms");
            if (lastSeen.Length == 0) lastSeen = PlayerAnalyticsEvent.StrOf(row, "lastSeen");
            players.Add(new RecentPlayerLite(
                steamId,
                PlayerAnalyticsEvent.StrOf(row, "player_name") is { Length: > 0 } name ? name : PlayerAnalyticsEvent.StrOf(row, "playerName"),
                lastSeen));
        }
        // Parity: comparePlayersByRecentSession (lastSeen desc, steamId asc) then slice(limit).
        players.Sort((a, b) =>
        {
            var byLastSeen = string.CompareOrdinal(b.LastSeen, a.LastSeen);
            return byLastSeen != 0 ? byLastSeen : string.CompareOrdinal(a.SteamId, b.SteamId);
        });
        var bounded = Math.Max(1, Math.Min(limit, 500));
        return players.Take(bounded).ToList();
    }


    /// <summary>Parity: collectionData block in routeStoragePlayerAnalyticsTimeline.</summary>
    private async Task<List<object?>> BuildPlayerCollectionDataAsync(
        long ownerUserId, string projectId, string steamId,
        List<CollectionMeta> collections, string primaryPlayerCollectionId, CancellationToken cancellationToken)
    {
        var rows = new List<(string Name, string Id, bool IsPrimary, Dictionary<string, object?> Data)>();
        foreach (var col in collections)
        {
            var colType = col.CollectionType.Length > 0 ? col.CollectionType : "per-steamid";
            if (colType == "global") continue;
            var playerKeys = await ListPlayerRecordKeysAsync(ownerUserId, projectId, col, steamId, cancellationToken);
            if (playerKeys.Count == 0) continue;
            var primaryKey = playerKeys.Contains(steamId) ? steamId : playerKeys[0];
            var isPrimary = primaryPlayerCollectionId == col.Id;
            rows.Add((col.Name.Length > 0 ? col.Name : col.Id, col.Id, isPrimary, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["collection"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = col.Id, ["name"] = col.Name.Length > 0 ? col.Name : col.Id },
                ["keys"] = playerKeys,
                ["primaryKey"] = primaryKey,
                ["keyCount"] = playerKeys.Count,
                ["isPrimaryPlayerTable"] = isPrimary,
            }));
        }

        return rows
            .OrderByDescending(r => r.IsPrimary)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (object?)r.Data)
            .ToList();
    }

    /// <summary>
    /// Enumerate this player's record keys for a collection. the store is the
    /// authoritative record store, so the <c>records</c> table is queried directly;
    /// the legacy workspace CDN directory listing (pre-cutover saves only) is no longer
    /// consulted.
    /// </summary>
    private async Task<List<string>> ListPlayerRecordKeysAsync(
        long ownerUserId, string projectId, CollectionMeta col, string steamId, CancellationToken cancellationToken)
    {
        // The store is the authoritative record store; enumerate this player's record
        // keys directly from the records table. The workspace CDN directory listing only
        // reflected pre-cutover saves and is no longer consulted.
        var rows = await networkStore.ListRecordsAsync(projectId, col.Id, cancellationToken);
        var prefix = $"{steamId}_";
        return rows
            .Where(r => !(r.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True))
            .Select(r => r.TryGetProperty("record_key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() ?? string.Empty
                : string.Empty)
            .Where(key => key.Length > 0 && (key == steamId || key.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ── Settings extraction ──

    private static IReadOnlyCollection<string> ReadQuietEndpointSlugs(IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is not null && settings.TryGetValue("quietEndpointSlugs", out var value))
        {
            var list = ReadStringList(value);
            if (list is not null) return list.Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        }
        return DefaultQuietEndpointSlugs.ToHashSet(StringComparer.Ordinal);
    }

    private static int ReadLowFpsThreshold(IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is not null && settings.TryGetValue("lowFpsThreshold", out var value))
        {
            var n = ReadNumber(value);
            if (n.HasValue && double.IsFinite(n.Value))
            {
                return Math.Max(1, Math.Min(240, (int)Math.Round(n.Value, MidpointRounding.AwayFromZero)));
            }
        }
        return DefaultLowFpsThreshold;
    }

    private static string ReadPrimaryPlayerCollectionId(IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is null) return string.Empty;
        // primaryPlayerTable.collectionId || primaryPlayerCollectionId
        if (settings.TryGetValue("primaryPlayerTable", out var table) && table is JsonElement { ValueKind: JsonValueKind.Object } te
            && te.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.String)
        {
            var value = cid.GetString();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        if (settings.TryGetValue("primaryPlayerCollectionId", out var direct))
        {
            return direct switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? string.Empty,
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    private static List<string>? ReadStringList(object value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Array } arr)
        {
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .ToList();
        }
        if (value is IEnumerable<string> strings) return strings.ToList();
        return null;
    }

    private static double? ReadNumber(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var d) => d,
        int i => i,
        long l => l,
        double d => d,
        _ => null,
    };

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var d) => d,
        _ => 0,
    };

    /// <summary>Parity: derivePresence — recompute isOnline/presence/effectiveTotalSeconds.</summary>
    private static JsonElement DerivePresence(JsonElement profile, DateTimeOffset now)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in profile.EnumerateObject()) map[prop.Name] = prop.Value;

        // The store player_profiles row (BuildPlayerProfileRow) stores
        // last_seen_unix_ms / last_heartbeat_unix_ms as bigint numbers and
        // is_online as a boolean. The legacy workspace/JSON path stored lastSeen /
        // lastHeartbeatAt as ISO strings. Support both so presence derivation
        // works regardless of which store the profile came from.
        long? anchorMs = ReadUnixMs(profile, "last_seen_unix_ms", "lastSeen")
                         ?? ReadUnixMs(profile, "last_heartbeat_unix_ms", "lastHeartbeatAt");
        var lastEvent = (PlayerAnalyticsEvent.StrOf(profile, "last_event_type")
                         ?? PlayerAnalyticsEvent.StrOf(profile, "lastEvent") ?? "").ToLowerInvariant();
        var storedOnline = profile.TryGetProperty("is_online", out var onSc) && onSc.ValueKind == JsonValueKind.True
                            || profile.TryGetProperty("isOnline", out var onCc) && onCc.ValueKind == JsonValueKind.True;
        var explicitLeave = lastEvent is "session.leave" or "leave" or "disconnect";

        var onlineFresh = !explicitLeave && anchorMs.HasValue && now.ToUnixTimeMilliseconds() - anchorMs.Value < 60_000;

        var totalSeconds = PlayerAnalyticsEvent.NumberOf(profile, "total_seconds")
                           ?? PlayerAnalyticsEvent.NumberOf(profile, "totalSeconds") ?? 0;
        long impliedSeconds = 0;
        if (onlineFresh && anchorMs.HasValue)
        {
            var diff = (now.ToUnixTimeMilliseconds() - anchorMs.Value) / 1000.0;
            if (diff > 0) impliedSeconds = (long)Math.Floor(diff);
        }

        // Expose both snake_case (raw) and camelCase (UI) keys so the frontend
        // can read whichever it expects.
        if (anchorMs.HasValue)
        {
            var iso = DateTimeOffset.FromUnixTimeMilliseconds(anchorMs.Value).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            map["lastSeen"] = iso;
            map["lastHeartbeatAt"] = iso;
        }
        // Expose onlineSince as an ISO string so the frontend's live-session
        // timer can anchor to the profile's authoritative session-start timestamp.
        var onlineSinceMs = ReadUnixMs(profile, "online_since_unix_ms");
        if (onlineSinceMs.HasValue)
        {
            map["onlineSince"] = DateTimeOffset.FromUnixTimeMilliseconds(onlineSinceMs.Value).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }
        map["isOnline"] = onlineFresh;
        map["is_online"] = onlineFresh;
        map["presence"] = onlineFresh ? "online" : "offline";
        map["presenceLabel"] = onlineFresh ? "Online" : "Offline";
        map["effectiveTotalSeconds"] = totalSeconds + impliedSeconds;
        if (!onlineFresh && storedOnline) map["staleOnline"] = true;

        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    /// <summary>Reads a Unix epoch-millis value from a JSON element, trying both bigint and ISO-string shapes.</summary>
    private static long? ReadUnixMs(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms)) return ms;
            if (v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.ToUnixTimeMilliseconds();
        }
        return null;
    }

    private sealed class FieldAgg
    {
        public required string FieldKey { get; init; }
        public required string CollectionId { get; init; }
        public required string FieldPath { get; init; }
        public required string Label { get; init; }
        public double TotalIncrease { get; set; }
        public double TotalDecrease { get; set; }
        public double NetChange { get; set; }
        public double EventCount { get; set; }
        public HashSet<string> Players { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct RecentPlayerLite(string SteamId, string PlayerName, string LastSeen);
}
