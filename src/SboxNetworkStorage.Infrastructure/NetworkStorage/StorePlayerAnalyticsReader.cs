using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Reads native player-analytics data from the fast-edge analytics resources.
/// The project endpoint aggregates the maintained index files (recent.json,
/// issues/recent.json, incidents/recent.json) into the payload the client
/// renderer (<c>wwwroot/js/player-analytics.js</c>) consumes — parity with the
/// legacy <c>buildProjectPlayerAnalyticsPayload</c> in
/// <c>controllers/storage-modules/insights-routes.js</c>.
/// </summary>
public sealed partial class StorePlayerAnalyticsReader(
    INetworkStorageStore scyllaStore) : IPlayerAnalyticsReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const int PerPage = 100;
    private const int OnlineStaleSeconds = 60;
    private const int StaleCollectionHours = 12;
    private const int IncidentWindowDays = 7;
    private const int ErrorWindowDays = 30;
    private const int MaxRecentIncidents = 250;
    private const int MaxErrorRows = 100;
    private const int LogWindowDays = 7;
    private const int MaxLogScanPlayers = 500;
    private const int MaxLogRows = 1000;
    private const int MaxPlayerEventsScan = 1000;

    [GeneratedRegex(@"(^|\s)(network storage )?test\s*player($|\s)|testplayer", RegexOptions.IgnoreCase, 100)]
    private static partial Regex TestPlayerPattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, 100)]
    private static partial Regex NonSlugRun();

    /// <summary>
    /// Known internal test steamIds that must never appear in customer-facing
    /// analytics dashboards. These are leftover accounts from analytics testing
    /// (e.g. <c>710013</c> — reported by a customer as showing 400+ hours
    /// "online"). Per AGENTS.md "Explicit State Over Heuristics", this is an
    /// explicit denylist, not a shape-guessing regex. Each entry must link to
    /// the incident that produced it. Must stay in sync with the JS parity
    /// filter in <c>controllers/storage-modules/insights-routes.js</c>.
    /// </summary>
    private static readonly HashSet<string> InternalTestSteamIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "710013", // analytics-testing leftover (defthue support ticket, 2026-06-27)
    };

    private static readonly string[] IncidentCountKeys =
    {
        "localErrors", "localWarnings", "endpointFailures", "performanceIssues",
        "networkQuality", "voiceActivity", "outdatedRevision",
    };

    public async Task<object?> GetProjectAnalyticsAsync(
        long ownerUserId,
        string projectId,
        IReadOnlyDictionary<string, object>? analyticsSettings,
        string? tab,
        string? query,
        int page,
        CancellationToken cancellationToken)
    {
        var activeTab = tab is "performance" or "errors" ? tab : "recent";
        var now = DateTimeOffset.UtcNow;

        var profileRows = await scyllaStore.ReadProjectProfilesAsync(projectId, cancellationToken);

        var players = new List<RecentPlayer>();
        foreach (var row in profileRows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            // Adapt the ScyllaDB player_profiles row (snake_case, unix-ms) to the
            // camelCase/ISO shape RecentPlayer.Parse expects (parity with the
            // legacy analytics/recent.json rows Bun maintained).
            var adapted = AdaptProfileRowForRecentPlayer(row, now);
            var player = RecentPlayer.Parse(adapted, now);
            if (player is null || player.IsObviousTestPlayer) continue;
            players.Add(player);
        }

        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length > 0)
        {
            players = players
                .Where(p => p.SteamId.Contains(q, StringComparison.Ordinal)
                            || p.PlayerName.ToLowerInvariant().Contains(q))
                .ToList();
        }

        players.Sort(CompareByRecentSession);

        var totalPlayers = players.Count;
        var onlinePlayers = players.Count(p => p.IsOnline);
        var lastHeartbeat = players
            .Select(p => p.LastSeen)
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .FirstOrDefault();

        var performanceSummary = BuildPerformanceSummary(players);

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalPlayers / (double)PerPage));
        var currentPage = Math.Min(Math.Max(1, page), totalPages);
        var pagePlayers = players
            .Skip((currentPage - 1) * PerPage)
            .Take(PerPage)
            .Select(p => p.ToRecentRow())
            .ToList();

        var managedIncidentSummary = await BuildManagedIncidentSummaryAsync(ownerUserId, projectId, now, cancellationToken);
        var errorRows = activeTab == "errors"
            ? await ReadErrorRowsAsync(ownerUserId, projectId, now, cancellationToken)
            : new List<JsonElement>();

        var analyticsEnabled = ReadAnalyticsEnabled(analyticsSettings);
        var collectionStatus = BuildCollectionStatus(analyticsEnabled, lastHeartbeat, now);

        return new Dictionary<string, object?>
        {
            ["players"] = pagePlayers,
            ["totalPlayers"] = totalPlayers,
            ["onlinePlayers"] = onlinePlayers,
            ["lastHeartbeat"] = lastHeartbeat,
            ["lastHeartbeatLabel"] = RelativeTime(lastHeartbeat, now),
            ["activeAnalyticsTab"] = activeTab,
            ["performanceSummary"] = performanceSummary,
            ["errorRows"] = errorRows,
            ["managedIncidentSummary"] = managedIncidentSummary,
            ["collectionStatus"] = collectionStatus,
            ["analyticsSettings"] = new Dictionary<string, object?> { ["enabled"] = analyticsEnabled },
            ["primaryPlayerColumns"] = Array.Empty<object>(),
            ["q"] = q,
            ["currentPage"] = currentPage,
            ["totalPages"] = totalPages,
            ["perPage"] = PerPage,
            ["startPlayer"] = totalPlayers == 0 ? 0 : ((currentPage - 1) * PerPage) + 1,
            ["endPlayer"] = Math.Min(currentPage * PerPage, totalPlayers),
            ["previousPageUrl"] = currentPage > 1 ? $"?page={currentPage - 1}" : null,
            ["nextPageUrl"] = currentPage < totalPages ? $"?page={currentPage + 1}" : null,
        };
    }

    public async Task<object?> GetPlayerTransactionsAsync(
        long ownerUserId, string projectId, string steamId, string? collection, int page, CancellationToken cancellationToken)
    {
        // Per-player storage operation history from ScyllaDB player_analytics_events
        // (category "record"), last LogWindowDays days, newest first, page 25.
        // Replaces the legacy logs/{collection}/ops/{date} Bunny day-file scan.
        const int perPage = 25;
        if (string.IsNullOrWhiteSpace(steamId))
            return Paginate(new List<Dictionary<string, JsonElement>>(), page, perPage, "transactions");

        var collections = await ReadCollectionsAsync(ownerUserId, projectId, cancellationToken);
        var nameById = BuildCollectionNameIndex(collections);
        var collectionFilter = string.IsNullOrWhiteSpace(collection) || collection == "all" ? null : collection;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = nowMs - (LogWindowDays * 24L * 60L * 60L * 1000L);
        var rows = await scyllaStore.ListPlayerEventsAsync(projectId, steamId, fromMs, nowMs, MaxPlayerEventsScan, cancellationToken);

        var transactions = new List<Dictionary<string, JsonElement>>();
        foreach (var row in rows)
        {
            var built = BuildStorageOpRow(row, steamId, nameById);
            if (built is null) continue;
            if (collectionFilter is not null && ReadString(built, "_collectionId") != collectionFilter) continue;
            transactions.Add(built);
        }

        transactions.Sort((a, b) => string.CompareOrdinal(LogTimestamp(b), LogTimestamp(a)));
        return Paginate(transactions, page, perPage, "transactions");
    }

    public async Task<object?> GetPlayerLedgerAsync(
        long ownerUserId, string projectId, string steamId, string? field, string? source, string? from, string? to, CancellationToken cancellationToken)
    {
        // Tracked-field deltas for a player from ScyllaDB ledger_entries (written by
        // PlayerAnalyticsIngester for fields marked _ledger:true / explicitly tracked),
        // summarised by source. Replaces the legacy
        // {collection}/data/{steamId}/logs/audit/{field}/{date}.json Bunny scan.
        var now = DateTimeOffset.UtcNow;
        var fromDate = string.IsNullOrWhiteSpace(from) ? now.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : from;
        var toDate = string.IsNullOrWhiteSpace(to) ? now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : to;

        var collections = await ReadCollectionsAsync(ownerUserId, projectId, cancellationToken);
        var entries = new List<Dictionary<string, JsonElement>>();
        foreach (var col in collections)
        {
            if (col.CollectionType == "global") continue;
            var ledgerFields = ExtractLedgerFields(col.Schema);
            if (ledgerFields.Count == 0) continue;

            var rows = await scyllaStore.ListLedgerEntriesAsync(projectId, col.Id, steamId, cancellationToken);
            foreach (var row in rows)
            {
                if (ParseJsonField(row, "entry_json") is not { ValueKind: JsonValueKind.Object } entry) continue;
                var entryField = ReadString(entry, "field");
                if (!string.IsNullOrWhiteSpace(field) && entryField != field) continue;
                var ts = ReadString(entry, "ts");
                var dateOnly = ts.Length >= 10 ? ts[..10] : string.Empty;
                if (dateOnly.Length > 0 && (string.CompareOrdinal(dateOnly, fromDate) < 0 || string.CompareOrdinal(dateOnly, toDate) > 0)) continue;

                var dict = ToStringKeyedDict(entry);
                dict["_collectionName"] = JsonSerializer.SerializeToElement(col.Name);
                dict["_collectionId"] = JsonSerializer.SerializeToElement(col.Id);
                entries.Add(dict);
            }
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            entries = entries.Where(e => ReadString(e, "source") == source).ToList();
        }

        entries.Sort((a, b) => string.CompareOrdinal(ReadString(b, "ts"), ReadString(a, "ts")));

        double totalIncrease = 0, totalDecrease = 0, netChange = 0;
        var sources = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var delta = ReadDouble(e, "delta");
            if (delta > 0) totalIncrease += delta; else totalDecrease += delta;
            netChange += delta;
            var src = ReadString(e, "source");
            if (string.IsNullOrEmpty(src)) src = "unknown";
            if (!sources.TryGetValue(src, out var agg))
            {
                agg = new Dictionary<string, object?> { ["count"] = 0, ["total"] = 0d };
                sources[src] = agg;
            }
            agg["count"] = Convert.ToInt32(agg["count"]) + 1;
            agg["total"] = Convert.ToDouble(agg["total"]) + delta;
        }

        return new Dictionary<string, object?>
        {
            ["steamId"] = steamId,
            ["field"] = string.IsNullOrWhiteSpace(field) ? "all" : field,
            ["from"] = fromDate,
            ["to"] = toDate,
            ["entries"] = entries.Take(200).ToList(),
            ["summary"] = new Dictionary<string, object?>
            {
                ["totalIncrease"] = totalIncrease,
                ["totalDecrease"] = totalDecrease,
                ["netChange"] = netChange,
                ["entryCount"] = entries.Count,
                ["sources"] = sources,
            },
        };
    }

    public async Task<object?> GetProjectLogsAsync(
        long ownerUserId, string projectId, string? collection, string? steamId, string? op, string? query, int page, CancellationToken cancellationToken)
    {
        // Project-wide storage operation browser from ScyllaDB player_analytics_events
        // (category "record"). Scans recent players' per-player event partitions for the
        // last LogWindowDays days. Replaces the legacy logs/{collection}/ops/{date} Bunny
        // day-file scan. Byte size is not captured by the .NET data plane analytics
        // events, so _bytes is omitted (the UI renders "-").
        const int perPage = 25;
        var collections = await ReadCollectionsAsync(ownerUserId, projectId, cancellationToken);
        var nameById = BuildCollectionNameIndex(collections);
        var collectionFilter = string.IsNullOrWhiteSpace(collection) ? null : collection;
        var opFilter = (op ?? string.Empty).Trim().ToLowerInvariant();
        var search = (query ?? string.Empty).Trim().ToLowerInvariant();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = nowMs - (LogWindowDays * 24L * 60L * 60L * 1000L);

        var steamIds = await ResolveLogPlayersAsync(projectId, steamId, cancellationToken);

        var logs = new List<Dictionary<string, JsonElement>>();
        foreach (var sid in steamIds)
        {
            if (logs.Count >= MaxLogRows) break;
            var rows = await scyllaStore.ListPlayerEventsAsync(projectId, sid, fromMs, nowMs, MaxPlayerEventsScan, cancellationToken);
            foreach (var row in rows)
            {
                var built = BuildStorageOpRow(row, sid, nameById);
                if (built is null) continue;
                if (collectionFilter is not null && ReadString(built, "_collectionId") != collectionFilter) continue;
                if (opFilter.Length > 0 && ReadString(built, "_op").ToLowerInvariant() != opFilter) continue;
                if (search.Length > 0 && !JsonSerializer.Serialize(built).ToLowerInvariant().Contains(search, StringComparison.Ordinal)) continue;
                logs.Add(built);
            }
        }

        logs.Sort((a, b) => string.CompareOrdinal(LogTimestamp(b), LogTimestamp(a)));
        return Paginate(logs, page, perPage, "logs");
    }

    public async Task<object?> GetProjectAuditLogsAsync(
        long ownerUserId, string projectId, int page, int pageSize, string? search, string? action, string? date, string? sort, CancellationToken cancellationToken)
    {
        // Read from ScyllaDB project_audit_logs (the authoritative audit store).
        // Replaces the legacy logs/project/{date}.json Bunny day-file reads.
        var size = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500);
        var oldestFirst = string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase);
        var searchTerm = (search ?? string.Empty).Trim().ToLowerInvariant();
        var actionFilter = (action ?? string.Empty).Trim();

        var auditRows = await scyllaStore.ListAuditLogsAsync(projectId, 500, cancellationToken);
        var all = new List<Dictionary<string, JsonElement>>();
        foreach (var row in auditRows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in row.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            if (actionFilter.Length > 0 && ReadString(row, "action") != actionFilter) continue;
            if (searchTerm.Length > 0 && !JsonSerializer.Serialize(dict).ToLowerInvariant().Contains(searchTerm, StringComparison.Ordinal)) continue;
            all.Add(dict);
        }

        all.Sort((a, b) =>
        {
            var cmp = string.CompareOrdinal(ProjectLogTimestamp(a), ProjectLogTimestamp(b));
            return oldestFirst ? cmp : -cmp;
        });

        var total = all.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var safePage = Math.Min(Math.Max(1, page), totalPages);
        var pageItems = all.Skip((safePage - 1) * size).Take(size).ToList();

        return new Dictionary<string, object?>
        {
            ["logs"] = pageItems,
            ["pagination"] = new Dictionary<string, object?>
            {
                ["page"] = safePage,
                ["pageSize"] = size,
                ["total"] = total,
                ["totalPages"] = totalPages,
                ["hasPrev"] = safePage > 1,
                ["hasNext"] = safePage < totalPages,
            },
            ["filters"] = new Dictionary<string, object?>
            {
                ["search"] = searchTerm,
                ["action"] = actionFilter,
                ["date"] = date ?? string.Empty,
                ["sort"] = oldestFirst ? "oldest" : "newest",
            },
        };
    }

    // ── Aggregation helpers ──

    private async Task<Dictionary<string, object?>> BuildManagedIncidentSummaryAsync(
        long ownerUserId, string projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, object?>();
        foreach (var key in IncidentCountKeys) counts[key] = 0;

        // Read the last IncidentWindowDays of issues from ScyllaDB
        // (project_analytics_issues), bucketed by date. Replaces the legacy
        // analytics/incidents/recent.json Bunny file.
        var recent = new List<JsonElement>();
        for (int i = 0; i < IncidentWindowDays; i++)
        {
            var date = now.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var rows = await scyllaStore.ListProjectIssuesAsync(projectId, date, MaxRecentIncidents, cancellationToken);
            foreach (var r in rows)
            {
                recent.Add(r);
                var key = ReadString(r, "category");
                // Map the V2 category to the legacy incident key the dashboard expects.
                var incidentKey = key switch
                {
                    "warning" => "localWarnings",
                    "endpoint" => "endpointFailures",
                    "performance" => "performanceIssues",
                    "network" => "networkQuality",
                    "voice" => "voiceActivity",
                    "error" => "localErrors",
                    _ => null,
                };
                if (incidentKey is not null && counts.ContainsKey(incidentKey))
                {
                    counts[incidentKey] = Convert.ToInt32(counts[incidentKey]) + 1;
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["days"] = IncidentWindowDays,
            ["playerCount"] = (int?)null,
            ["counts"] = counts,
            ["recent"] = recent.Take(MaxErrorRows).ToList(),
        };
    }

    private async Task<List<JsonElement>> ReadErrorRowsAsync(
        long ownerUserId, string projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Read error-category issues from ScyllaDB (project_analytics_issues) for
        // the last ErrorWindowDays. Replaces the legacy analytics/issues/recent.json
        // Bunny file.
        var rows = new List<JsonElement>();
        for (int i = 0; i < ErrorWindowDays; i++)
        {
            var date = now.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dayRows = await scyllaStore.ListProjectIssuesAsync(projectId, date, MaxErrorRows, cancellationToken);
            foreach (var r in dayRows)
            {
                if (ReadString(r, "category") == "error") rows.Add(r);
            }
            if (rows.Count >= MaxErrorRows) break;
        }
        return rows.Take(MaxErrorRows).ToList();
    }


    private static Dictionary<string, object?> BuildPerformanceSummary(IReadOnlyList<RecentPlayer> players)
    {
        var rows = players.Select(p => p.ToPerformanceRow()).ToList();
        var fpsRows = rows.Where(r => Convert.ToInt32(r["avgFps"]) > 0).ToList();

        return new Dictionary<string, object?>
        {
            ["playerCount"] = rows.Count,
            ["sampleCount"] = players.Sum(p => p.PerformanceSamples),
            ["avgFps"] = Average(fpsRows.Select(r => Convert.ToDouble(r["avgFps"]))),
            ["peakFps"] = fpsRows.Count == 0 ? 0 : fpsRows.Max(r => Convert.ToInt32(r["peakFps"])),
            ["avgPlaytimeSeconds"] = Average(rows.Select(r => Convert.ToDouble(r["totalSeconds"]))),
            ["avgSessionSeconds"] = Average(rows.Select(r => Convert.ToDouble(r["avgSessionSeconds"]))),
            ["rows"] = rows.Take(100).ToList(),
        };
    }

    private static Dictionary<string, object?> BuildCollectionStatus(bool enabled, string? lastHeartbeat, DateTimeOffset now)
    {
        if (!enabled) return new Dictionary<string, object?> { ["label"] = "Not collecting", ["tone"] = "off" };
        if (string.IsNullOrEmpty(lastHeartbeat) || !TryParseTime(lastHeartbeat, out var last))
            return new Dictionary<string, object?> { ["label"] = "Waiting for activity", ["tone"] = "stale" };
        if ((now - last).TotalHours >= StaleCollectionHours)
            return new Dictionary<string, object?> { ["label"] = "Stale collection", ["tone"] = "stale" };
        return new Dictionary<string, object?> { ["label"] = "Collecting", ["tone"] = "on" };
    }

    private static bool ReadAnalyticsEnabled(IReadOnlyDictionary<string, object>? settings)
    {
        // Analytics defaults to enabled when unset (services/network-storage-analytics-settings.js).
        if (settings is null || !settings.TryGetValue("enabled", out var value)) return true;
        return value switch
        {
            bool b => b,
            JsonElement je => je.ValueKind != JsonValueKind.False,
            _ => true,
        };
    }


    private static int CompareByRecentSession(RecentPlayer a, RecentPlayer b)
    {
        var byLastSeen = string.CompareOrdinal(b.LastSeen, a.LastSeen);
        return byLastSeen != 0 ? byLastSeen : string.CompareOrdinal(a.SteamId, b.SteamId);
    }

    private static int Average(IEnumerable<double> values)
    {
        var valid = values.Where(v => double.IsFinite(v) && v > 0).ToList();
        if (valid.Count == 0) return 0;
        return (int)Math.Round(valid.Sum() / valid.Count, MidpointRounding.AwayFromZero);
    }

    private static string RelativeTime(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(value) || !TryParseTime(value, out var time)) return "Never";
        var seconds = Math.Max(0, (long)(now - time).TotalSeconds);
        if (seconds < 60) return $"{Math.Max(1, seconds)} sec{(seconds == 1 ? "" : "s")} ago";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} min{(minutes == 1 ? "" : "s")} ago";
        var hours = minutes / 60;
        if (hours < 48) return $"{hours} hr{(hours == 1 ? "" : "s")} ago";
        var days = hours / 24;
        return $"{days}d ago";
    }

    private static bool TryParseTime(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    /// <summary>
    /// Adapt a ScyllaDB <c>player_profiles</c> row (snake_case, unix-ms
    /// timestamps) to the camelCase/ISO-8601 shape <see cref="RecentPlayer.Parse"/>
    /// expects (parity with the legacy <c>analytics/recent.json</c> rows).
    /// </summary>
    private static JsonElement AdaptProfileRowForRecentPlayer(JsonElement row, DateTimeOffset now)
    {
        long? Long(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
        string? Iso(long? ms) => ms is { } m ? DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) : null;
        var lastSeenMs = Long("last_seen_unix_ms");
        var lastHeartbeatMs = Long("last_heartbeat_unix_ms");
        var onlineSinceMs = Long("online_since_unix_ms");
        // The dashboard's "N events" badge reads eventCount. We keep a running
        // total inside managed_counters_json under the reserved __totalEvents
        // key (see PlayerAnalyticsIngester). BuildPlayerProfileRow parses the
        // column into a JSON object (ParseJsonOrNull), so the element may be
        // Object (real store) or String (InMemoryNetworkStorageStore); handle both.
        long eventCount = 0;
        if (row.TryGetProperty("managed_counters_json", out var mc))
        {
            try
            {
                var jsonText = mc.ValueKind == JsonValueKind.String
                    ? mc.GetString()
                    : mc.ValueKind == JsonValueKind.Object
                        ? mc.GetRawText()
                        : null;
                if (!string.IsNullOrEmpty(jsonText))
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    if (doc.RootElement.TryGetProperty("__totalEvents", out var te) && te.ValueKind == JsonValueKind.Number && te.TryGetInt64(out var n))
                        eventCount = n;
                }
            }
            catch { /* malformed counters blob — treat as zero */ }
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["steamId"] = ReadString(row, "steam_id"),
            ["playerName"] = ReadString(row, "player_name"),
            ["lastSeen"] = Iso(lastSeenMs),
            ["lastHeartbeatAt"] = Iso(lastHeartbeatMs),
            ["onlineSince"] = Iso(onlineSinceMs),
            ["isOnline"] = row.TryGetProperty("is_online", out var on) && on.ValueKind == JsonValueKind.True,
            ["lastEvent"] = ReadString(row, "last_event_type"),
            ["totalSeconds"] = Long("total_seconds") ?? 0,
            ["sessionCount"] = Long("session_count") ?? 0,
            ["eventCount"] = eventCount,
            ["analyticsStatus"] = "Server-only",
        }, JsonOptions);
    }

    private static string AnalyticsStatusLabel(string status)
    {
        var normalized = (string.IsNullOrEmpty(status) ? "Server-only" : status).Trim().ToLowerInvariant();
        return normalized switch
        {
            "server-only" => "Endpoint collection only",
            "managed" => "Library analytics",
            "manual" => "Manual event collection",
            _ => string.IsNullOrEmpty(status) ? "Endpoint collection only" : status,
        };
    }

    private static string Slugify(string label)
    {
        var slug = NonSlugRun().Replace(label.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "endpoint-collection-only" : slug;
    }

    private static string ReadString(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static Dictionary<string, string> BuildCollectionNameIndex(IReadOnlyList<CollectionMeta> collections)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in collections)
        {
            if (c.Id.Length == 0) continue;
            map[c.Id] = c.Name.Length > 0 ? c.Name : c.Id;
        }
        return map;
    }

    private async Task<IReadOnlyList<string>> ResolveLogPlayersAsync(string projectId, string? steamId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(steamId)) return new[] { steamId };
        var profiles = await scyllaStore.ReadProjectProfilesAsync(projectId, cancellationToken);
        return profiles
            .Where(p => p.ValueKind == JsonValueKind.Object)
            .Select(p => (SteamId: ReadString(p, "steam_id"), LastSeen: ReadLongField(p, "last_seen_unix_ms")))
            .Where(p => p.SteamId.Length > 0)
            .OrderByDescending(p => p.LastSeen)
            .ThenBy(p => p.SteamId, StringComparer.Ordinal)
            .Select(p => p.SteamId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLogScanPlayers)
            .ToList();
    }

    /// <summary>
    /// Build a legacy ops-log/transaction row from a ScyllaDB player_analytics_events
    /// row. Returns null for non-storage events (only record mutations are part of the
    /// project "write history"; reads are excluded).
    /// </summary>
    private static Dictionary<string, JsonElement>? BuildStorageOpRow(
        JsonElement eventRow, string steamId, IReadOnlyDictionary<string, string> nameById)
    {
        if (eventRow.ValueKind != JsonValueKind.Object) return null;
        var eventType = ReadString(eventRow, "event_type");
        var category = ReadString(eventRow, "category");
        if (!IsStorageWriteOp(eventType, category)) return null;

        var payload = ParseJsonField(eventRow, "payload_json");
        var payloadObj = payload is { ValueKind: JsonValueKind.Object } p ? p : default;

        var collectionId = ReadString(eventRow, "collection_id");
        if (collectionId.Length == 0 && payloadObj.ValueKind == JsonValueKind.Object) collectionId = ReadString(payloadObj, "collectionId");

        var tsIso = payloadObj.ValueKind == JsonValueKind.Object ? ReadString(payloadObj, "ts") : string.Empty;
        if (tsIso.Length == 0)
        {
            var ms = ReadLongField(eventRow, "created_at_unix_ms");
            if (ms > 0) tsIso = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }

        var ok = eventType != "record.save_unconfirmed";
        var collectionName = nameById.TryGetValue(collectionId, out var n) ? n : collectionId;

        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["_ts"] = JsonSerializer.SerializeToElement(tsIso),
            ["_timestamp"] = JsonSerializer.SerializeToElement(tsIso),
            ["_steamId"] = JsonSerializer.SerializeToElement(steamId),
            ["_op"] = JsonSerializer.SerializeToElement(StorageOpLabel(eventType)),
            ["_key"] = JsonSerializer.SerializeToElement(steamId),
            ["_collectionId"] = JsonSerializer.SerializeToElement(collectionId),
            ["_collectionName"] = JsonSerializer.SerializeToElement(collectionName),
            ["_ok"] = JsonSerializer.SerializeToElement(ok),
        };
        if (!ok && payloadObj.ValueKind == JsonValueKind.Object)
        {
            var reason = ReadString(payloadObj, "reason");
            if (reason.Length > 0) dict["_err"] = JsonSerializer.SerializeToElement(reason);
        }
        return dict;
    }

    private static bool IsStorageWriteOp(string eventType, string category)
        => category == "record" && eventType.Length > 0 && eventType != "record.read";

    private static string StorageOpLabel(string eventType) => eventType switch
    {
        "record.write" => "save",
        "record.delete" => "delete",
        "record.save_unconfirmed" => "save",
        _ when eventType.StartsWith("record.", StringComparison.Ordinal) => eventType["record.".Length..],
        _ => eventType,
    };

    /// <summary>Parse a JSON field the store may return as a JSON string or an object.</summary>
    private static JsonElement? ParseJsonField(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (string.IsNullOrEmpty(s)) return null;
            try { return JsonDocument.Parse(s).RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static Dictionary<string, JsonElement> ToStringKeyedDict(JsonElement obj)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (obj.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in obj.EnumerateObject()) dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private static long ReadLongField(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var n)
            ? n
            : 0;

    // ── Scan helpers (logs / transactions / ledger) ──

    private sealed record CollectionMeta(string Id, string Name, string CollectionType, JsonElement? Schema);

    private async Task<List<CollectionMeta>> ReadCollectionsAsync(long ownerUserId, string projectId, CancellationToken cancellationToken)
    {
        // Read from ScyllaDB (the authoritative collection metadata store) rather
        // than the legacy collections.json Bunny file. The V2 schema returns
        // {collection_id, name, visibility, definition_json, ...}.
        var rows = await scyllaStore.ListCollectionsAsync(projectId, cancellationToken);
        var list = new List<CollectionMeta>();
        foreach (var c in rows)
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(c, "collection_id");
            if (id.Length == 0) id = ReadString(c, "id");
            if (id.Length == 0) continue;
            var name = ReadString(c, "name");
            var type = ReadString(c, "visibility");
            JsonElement? schema = c.TryGetProperty("definition_json", out var d) ? d.Clone() : null;
            list.Add(new CollectionMeta(id, name.Length == 0 ? id : name, type.Length == 0 ? "per-steamid" : type, schema));
        }
        return list;
    }


    private static List<string> ExtractLedgerFields(JsonElement? schema)
    {
        var result = new List<string>();
        CollectLedgerFields(schema, string.Empty, result);
        return result;
    }

    private static void CollectLedgerFields(JsonElement? schema, string prefix, List<string> result)
    {
        var props = GetSchemaProps(schema);
        if (props is not { } p) return;
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            var type = ReadString(prop.Value, "type");
            if (type == "number" && prop.Value.TryGetProperty("_ledger", out var ledger) && ledger.ValueKind == JsonValueKind.True && !result.Contains(path))
            {
                result.Add(path);
            }
            if (type == "object" && prop.Value.TryGetProperty("properties", out _))
            {
                CollectLedgerFields(prop.Value, path, result);
            }
        }
    }

    private static JsonElement? GetSchemaProps(JsonElement? schema)
    {
        if (schema is not { } s || s.ValueKind != JsonValueKind.Object) return null;
        if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object) return props;
        var any = false;
        foreach (var member in s.EnumerateObject())
        {
            any = true;
            if (member.Name == "type" || member.Value.ValueKind != JsonValueKind.Object) return null;
        }
        return any ? s : null;
    }

    private static Dictionary<string, object?> Paginate(List<Dictionary<string, JsonElement>> items, int page, int perPage, string itemsKey)
    {
        var total = items.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));
        var safePage = Math.Min(Math.Max(1, page), totalPages);
        return new Dictionary<string, object?>
        {
            [itemsKey] = items.Skip((safePage - 1) * perPage).Take(perPage).ToList(),
            ["page"] = safePage,
            ["totalPages"] = totalPages,
            ["total"] = total,
        };
    }


    private static string LogTimestamp(Dictionary<string, JsonElement> row)
    {
        var ts = ReadString(row, "_ts");
        return ts.Length > 0 ? ts : ReadString(row, "_timestamp");
    }

    private static string ProjectLogTimestamp(Dictionary<string, JsonElement> row)
    {
        var ts = ReadString(row, "ts");
        return ts.Length > 0 ? ts : ReadString(row, "createdAt");
    }

    private static string ReadString(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static double ReadDouble(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;


    private sealed class RecentPlayer
    {
        public required string SteamId { get; init; }
        public required string PlayerName { get; init; }
        public string LastSeen { get; init; } = string.Empty;
        public long TotalSeconds { get; init; }
        public long EffectiveTotalSeconds { get; init; }
        public bool IsOnline { get; init; }
        public string AnalyticsStatus { get; init; } = "Server-only";
        public int AvgFps { get; init; }
        public int PeakFps { get; init; }
        public int MinFps { get; init; }
        public int PerformanceSamples { get; init; }
        public long SessionCount { get; init; }
        public long EventCount { get; init; }
        public JsonElement? Revision { get; init; }
        public string? RevisionStatus { get; init; }

        public bool IsObviousTestPlayer =>
            TestPlayerPattern().IsMatch(PlayerName)
            || string.Equals(SteamId, "testplayer", StringComparison.OrdinalIgnoreCase)
            || InternalTestSteamIds.Contains(SteamId);

        public static RecentPlayer? Parse(JsonElement element, DateTimeOffset now)
        {
            var steamId = ReadString(element, "steamId");
            if (string.IsNullOrEmpty(steamId)) return null;

            var lastSeen = ReadString(element, "lastSeen");
            var lastHeartbeatAt = ReadString(element, "lastHeartbeatAt");
            var lastEvent = ReadString(element, "lastEvent").ToLowerInvariant();
            var storedOnline = element.TryGetProperty("isOnline", out var on) && on.ValueKind == JsonValueKind.True;
            var hasOfflineMarker = element.TryGetProperty("lastOfflineAt", out var off)
                                   && off.ValueKind == JsonValueKind.String
                                   && !string.IsNullOrEmpty(off.GetString());

            var presenceAnchor = !string.IsNullOrEmpty(lastSeen) ? lastSeen : lastHeartbeatAt;
            var explicitLeave = lastEvent is "session.leave" or "leave" or "disconnect"
                                || (hasOfflineMarker && !storedOnline);
            var onlineFresh = !explicitLeave
                              && TryParseTime(presenceAnchor, out var anchor)
                              && (now - anchor).TotalSeconds < OnlineStaleSeconds;

            var totalSeconds = ReadLong(element, "totalSeconds");
            var implied = onlineFresh && TryParseTime(lastSeen, out var seen)
                ? Math.Max(0, (long)(now - seen).TotalSeconds)
                : 0;

            var performance = element.TryGetProperty("performance", out var perf) && perf.ValueKind == JsonValueKind.Object
                ? perf
                : (JsonElement?)null;

            JsonElement? revision = element.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Object
                ? rev.Clone()
                : null;

            return new RecentPlayer
            {
                SteamId = steamId,
                PlayerName = ReadString(element, "playerName"),
                LastSeen = lastSeen,
                TotalSeconds = totalSeconds,
                EffectiveTotalSeconds = totalSeconds + implied,
                IsOnline = onlineFresh,
                AnalyticsStatus = string.IsNullOrEmpty(ReadString(element, "analyticsStatus")) ? "Server-only" : ReadString(element, "analyticsStatus"),
                AvgFps = ReadPerfInt(performance, "fpsAverage", "lastFpsAverage"),
                PeakFps = ReadPerfInt(performance, "fpsPeak", "lastFpsPeak"),
                MinFps = ReadPerfInt(performance, "fpsMin", "lastFpsMin"),
                PerformanceSamples = (int)ReadPerfLong(performance, "samples"),
                SessionCount = ReadLong(element, "sessionCount"),
                EventCount = ReadLong(element, "eventCount"),
                Revision = revision,
                RevisionStatus = element.TryGetProperty("revisionStatus", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null,
            };
        }

        public Dictionary<string, object?> ToRecentRow()
        {
            var statusLabel = AnalyticsStatusLabel(AnalyticsStatus);
            return new Dictionary<string, object?>
            {
                ["steamId"] = SteamId,
                ["playerName"] = PlayerName,
                ["displayName"] = string.IsNullOrEmpty(PlayerName) ? SteamId : PlayerName,
                ["lastSeen"] = LastSeen,
                ["lastSeenLabel"] = RelativeTime(LastSeen, DateTimeOffset.UtcNow),
                ["totalSeconds"] = EffectiveTotalSeconds,
                ["analyticsStatusLabel"] = statusLabel,
                ["analyticsStatusSlug"] = Slugify(statusLabel),
                ["onlineStatusLabel"] = IsOnline ? "Online" : "Offline",
                ["onlineStatusSlug"] = IsOnline ? "online" : "offline",
                ["isOnline"] = IsOnline,
                ["revision"] = Revision,
                ["revisionStatus"] = RevisionStatus,
                ["primaryPlayerColumns"] = Array.Empty<object>(),
                ["eventCount"] = EventCount,
            };
        }

        public Dictionary<string, object?> ToPerformanceRow()
        {
            var avgSessionSeconds = SessionCount > 0
                ? (long)Math.Round(EffectiveTotalSeconds / (double)SessionCount)
                : EffectiveTotalSeconds;
            return new Dictionary<string, object?>
            {
                ["steamId"] = SteamId,
                ["playerName"] = PlayerName,
                ["displayName"] = string.IsNullOrEmpty(PlayerName) ? SteamId : PlayerName,
                ["lastSeen"] = LastSeen,
                ["lastSeenLabel"] = RelativeTime(LastSeen, DateTimeOffset.UtcNow),
                ["totalSeconds"] = EffectiveTotalSeconds,
                ["avgFps"] = AvgFps,
                ["peakFps"] = PeakFps,
                ["minFps"] = MinFps,
                ["avgSessionSeconds"] = avgSessionSeconds,
                ["onlineStatusLabel"] = IsOnline ? "Online" : "Offline",
                ["onlineStatusSlug"] = IsOnline ? "online" : "offline",
                ["revision"] = Revision,
                ["revisionStatus"] = RevisionStatus,
            };
        }

        private static long ReadLong(JsonElement element, string key)
            => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        private static int ReadPerfInt(JsonElement? perf, string key, string fallbackKey)
        {
            if (perf is not { } p) return 0;
            var value = ReadPerfDouble(p, key);
            if (value <= 0) value = ReadPerfDouble(p, fallbackKey);
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static double ReadPerfDouble(JsonElement perf, string key)
            => perf.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

        private static long ReadPerfLong(JsonElement? perf, string key)
            => perf is { } p && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
