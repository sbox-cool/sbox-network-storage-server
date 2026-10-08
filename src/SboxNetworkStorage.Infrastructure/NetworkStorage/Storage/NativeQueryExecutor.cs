using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

/// <summary>
/// Records query run outcomes (last-run + log) to ScyllaDB. Fire-and-forget
/// from <see cref="NativeQueryExecutor"/> — a slow ScyllaDB write MUST NOT
/// stall the query response. Throttled to one record per (queryId, minute)
/// per process to avoid log spam on hot queries; <c>force</c> bypasses the
/// throttle for explicit reruns. The <c>query_run_logs</c> table carries a
/// 90-day TTL; reads LIMIT 50 (newest first).
/// </summary>
public interface IQueryRunRecorder
{
    /// <summary>Record a query execution. Best-effort, never throws.</summary>
    void Record(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, bool force);
}

/// <summary>Default no-op recorder (used when ScyllaDB recording is disabled).</summary>
public sealed class NullQueryRunRecorder : IQueryRunRecorder
{
    public static NullQueryRunRecorder Instance { get; } = new();
    private NullQueryRunRecorder() { }
    public void Record(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, bool force) { }
}

/// <summary>
/// ScyllaDB-backed recorder. Throttles per (projectId, queryId) to one
/// log per minute unless <c>force</c> is set. The ScyllaDB write runs on the
/// ThreadPool in a service-provider scope (the store is scoped) and is detached
/// from the request lifetime — see <see cref="NetworkStorageUsageTracker"/> for
/// the same pattern. A bounded internal timeout (10 s) ensures an aborted
/// request never drops the telemetry write.
/// </summary>
public sealed class ScyllaQueryRunRecorder(IServiceScopeFactory scopeFactory, ILogger<ScyllaQueryRunRecorder>? logger) : IQueryRunRecorder
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastRecordUnixMs = new();
    private const int MinLogIntervalMs = 60_000; // 1 minute throttle
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    public void Record(string projectId, string queryId, string runAtIso, long durationMs, int keysScanned, int recordsReturned, bool fromCache, bool force)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(queryId)) return;
        var key = $"{projectId}:{queryId}";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && _lastRecordUnixMs.TryGetValue(key, out var last) && (nowMs - last) < MinLogIntervalMs)
            return; // throttled — the latest-run row is already recent

        _lastRecordUnixMs[key] = nowMs;
        // Fire-and-forget: never block the request path on a ScyllaDB write.
        // Detached from the request token (bounded timeout instead) so an
        // aborted request doesn't cancel the write mid-commit.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cts.CancelAfter(WriteTimeout);
            try
            {
                await store.RecordQueryRunAsync(projectId, queryId, runAtIso, durationMs, keysScanned, recordsReturned, fromCache, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "Failed to record query run for {ProjectId}/{QueryId}", projectId, queryId);
            }
        });
    }
}

/// <summary>
/// Native .NET executor for Network Storage queries. Full parity port of the Bun
/// <c>tools/sbox/queries.js</c> engine: multi-source merge, foreign-key joins,
/// computed fields, object-valued metric fields, output column normalization,
/// in-memory result cache with TTL, and performance tracking. Reads query
/// definitions and source records from ScyllaDB. No Bun fallback.
/// </summary>
public sealed class NativeQueryExecutor
{
    private readonly INetworkStorageStore _store;
    private readonly IQueryRunRecorder _recorder;
    private readonly ILogger<NativeQueryExecutor> _logger;

    private static readonly ConcurrentDictionary<string, CachedQuery> ResultCache = new();

    public NativeQueryExecutor(INetworkStorageStore store, ILogger<NativeQueryExecutor> logger)
        : this(store, NullQueryRunRecorder.Instance, logger) { }

    public NativeQueryExecutor(INetworkStorageStore store, IQueryRunRecorder recorder, ILogger<NativeQueryExecutor> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Collection scan routing ──────────────────────────────────────

    /// <summary>
    /// Scan a source collection's records, routing by <c>collectionType</c>:
    /// global collections read from <c>global_records</c>, per-player
    /// collections read from <c>records</c>. This matches
    /// <see cref="ScyllaEndpointShadowDataSource.ScanCollectionAsync"/> and
    /// <c>NetworkStorageController.BrowseCollectionDataApi</c>. Before this
    /// routing existed, a query over a global collection (e.g.
    /// <c>leaderboard_global</c>) scanned the <c>records</c> table — which is
    /// empty for global collections — so the query returned zero entries even
    /// though <c>global_records</c> had data. Same table-mismatch class as the
    /// in-game leaderboard read-path bug.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> ScanCollectionRecordsAsync(
        string projectId, string collectionId, CancellationToken ct)
    {
        if (await IsGlobalCollectionAsync(projectId, collectionId, ct))
            return await _store.ListGlobalRecordsAsync(projectId, collectionId, ct);
        return await _store.ListRecordsAsync(projectId, collectionId, ct);
    }

    /// <summary>
    /// Resolve whether a collection is <c>collectionType: "global"</c> by reading
    /// its <c>definition_json</c> from ScyllaDB. Mirrors
    /// <see cref="ScyllaEndpointShadowDataSource.IsGlobalCollectionAsync"/>:
    /// defaults to <c>false</c> (per-steamid) when the collection is missing,
    /// the field is absent, or the lookup fails — matching every other
    /// read/write path in the codebase.
    /// </summary>
    private async Task<bool> IsGlobalCollectionAsync(
        string projectId, string collectionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(collectionId))
            return false;
        try
        {
            var col = await _store.ReadCollectionAsync(projectId, collectionId, ct);
            if (!col.HasValue) return false;
            if (!col.Value.TryGetProperty("definition_json", out var defProp)) return false;
            // definition_json may be a JSON string (raw text column) or a parsed
            // object (BuildCollectionRow returns it via ParseJsonOrNull).
            if (defProp.ValueKind == JsonValueKind.String)
            {
                var s = defProp.GetString();
                if (string.IsNullOrEmpty(s)) return false;
                using var doc = JsonDocument.Parse(s);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("collectionType", out var ctProp)
                    && ctProp.ValueKind == JsonValueKind.String
                    && string.Equals(ctProp.GetString(), "global", StringComparison.OrdinalIgnoreCase);
            }
            if (defProp.ValueKind != JsonValueKind.Object) return false;
            return defProp.TryGetProperty("collectionType", out var ctObj)
                && ctObj.ValueKind == JsonValueKind.String
                && string.Equals(ctObj.GetString(), "global", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve collectionType for {ProjectId}/{CollectionId} — defaulting to per-steamid", projectId, collectionId);
            return false;
        }
    }
    // ── Public API ──

    /// <summary>
    /// Execute a query using a caller-provided query definition (e.g. from Bunny).
    /// Records are still read from ScyllaDB.
    /// </summary>
    public async Task<QueryResult?> ExecuteWithQueryAsync(
        string projectId, string queryId, JsonElement query,
        IReadOnlyDictionary<string, object?>? values,
        bool bypassCache, CancellationToken ct)
    {
        return await ExecuteCoreAsync(projectId, queryId, query, values, bypassCache, ct);
    }

    /// <summary>
    /// Execute a query by reading its definition from the ScyllaDB <c>queries</c> table.
    /// Extracts <c>definition_json</c> from the row (not the raw row).
    /// </summary>
    public async Task<QueryResult?> ExecuteAsync(
        string projectId, string queryId,
        IReadOnlyDictionary<string, object?>? values,
        bool bypassCache, CancellationToken ct)
    {
        var queryRow = await _store.ReadQueryAsync(projectId, queryId, ct);
        if (!queryRow.HasValue) return null;

        // The ScyllaDB queries row wraps the query definition in definition_json.
        var query = ExtractDefinition(queryRow.Value);
        if (query is null) return null;

        return await ExecuteCoreAsync(projectId, queryId, query.Value, values, bypassCache, ct);
    }

    /// <summary>Clear the cached result for a query (mirror <c>clearQueryCache</c>).</summary>
    public static void ClearQueryCache(string queryId, string? projectId = null)
    {
        var key = CacheKey(queryId, projectId);
        ResultCache.TryRemove(key, out _);
    }

    // ── Core execution ──

    private async Task<QueryResult?> ExecuteCoreAsync(
        string projectId, string queryId, JsonElement query,
        IReadOnlyDictionary<string, object?>? values, bool bypassCache, CancellationToken ct)
    {
        var cacheKey = CacheKey(queryId, projectId);

        // Check cache first unless the caller explicitly asks for live data.
        if (!bypassCache && ResultCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            var cachedResult = cached.Result;
            cachedResult.FromCache = true;
            cachedResult.CachedAt = cached.CachedAtIso;
            cachedResult.ExpiresAt = cached.ExpiresAtIso;
            return cachedResult;
        }

        var stopwatch = Stopwatch.StartNew();
        var queryType = Str(query, "type") ?? "leaderboard";
        var config = query.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var valuesDict = values;

        // Scan all source collections from ScyllaDB.
        var scannedSources = new List<ScannedSource>();
        if (query.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in src.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var collectionId = Str(s, "collectionId");
                if (string.IsNullOrEmpty(collectionId)) continue;


                var entries = new List<QueryEntry>();
                IReadOnlyList<JsonElement> records;
                try { records = await ScanCollectionRecordsAsync(projectId, collectionId, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to scan collection {CollectionId} for query {QueryId}", collectionId, queryId);
                    continue;
                }

                foreach (var record in records)
                {
                    // ExtractKey MUST match BrowseCollectionDataApi and
                    // ScyllaEndpointShadowDataSource — otherwise a query would
                    // see a different set of records than those paths. ExtractKey
                    // resolves record_key (per-player) OR record_id (global) so
                    // global collections are keyed correctly.
                    if (RecordRow.ExtractValueModel(record) is Dictionary<string, object?> data)
                        entries.Add(new QueryEntry { Key = RecordRow.ExtractKey(record) ?? "", Data = data });
                }

                // Resolve collection name for the source detail.
                string? colName = null;
                try
                {
                    var colRow = await _store.ReadCollectionAsync(projectId, collectionId, ct);
                    if (colRow.HasValue && colRow.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        colName = n.GetString();
                }
                catch { /* best-effort — name is informational only */ }

                var alias = GetSourceAlias(s, colName, collectionId);
                scannedSources.Add(new ScannedSource(s, collectionId, colName, alias, entries));
            }
        }

        if (scannedSources.Count == 0)
            return new QueryResult { Type = "error", Message = "Query has no sources." };

        // Merge all sources (key-union + joins) into a single entry list.
        var merged = MergeScannedSources(scannedSources, config, valuesDict);
        // Apply config.joins (foreign-key joins on a single source, distinct from
        // multi-source merge joins). Each join scans a foreign collection once
        // and merges matching records under the join alias.
        await ApplyConfigJoinsAsync(projectId, merged, config, valuesDict, ct);

        // Apply player-profile enrichment: merges display name, presence, and
        // session stats from player_profiles into each entry whose key is a
        // steamId. Triggered by config.enrichment.playerProfiles — a reserved
        // pseudo-join that cannot collide with user collections (__sbox_ namespace).
        await ApplyPlayerProfileEnrichmentAsync(projectId, merged, config, ct);

        // Apply computed fields.
        var computedInfo = ApplyComputedFields(merged, config, valuesDict);

        // Execute the query type.
        var result = ExecuteQueryType(queryType, config, merged, valuesDict);

        stopwatch.Stop();
        result.Performance = BuildPerformance(merged, config, computedInfo, scannedSources, stopwatch.ElapsedMilliseconds);
        result.FromCache = false;

        // Cache the result unless this was an explicit live read.
        if (!bypassCache)
        {
            var ttlSeconds = ReadCacheTtl(query);
            var now = DateTimeOffset.UtcNow;
            var cachedEntry = new CachedQuery(result.Clone(), now, now.AddSeconds(ttlSeconds));
            ResultCache[cacheKey] = cachedEntry;
        }

        // Record the run (fire-and-forget, throttled). bypassCache means the
        // caller asked for a live read (dashboard "Run" button or rerun) —
        // force the log entry so it shows up immediately even if a recent
        // cached execution already recorded one this minute.
        if (result.Performance is { } perf)
        {
            _recorder.Record(
                projectId, queryId, perf.At ?? string.Empty, perf.DurationMs,
                perf.KeysScanned, result.Entries?.Count ?? 0, fromCache: false,
                force: bypassCache);
        }

        return result;
    }

    // ── Query type dispatch ──

    private static QueryResult ExecuteQueryType(string queryType, JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
        => queryType switch
        {
            "leaderboard" => ExecLeaderboard(config, entries, values),
            "count" => ExecCount(config, entries, values),
            "sum" => ExecSum(config, entries, values),
            "average" => ExecAverage(config, entries, values),
            "min" => ExecMinMax(config, entries, values, "min"),
            "max" => ExecMinMax(config, entries, values, "max"),
            _ => new QueryResult { Type = "error", Message = $"Unknown query type: {queryType}" }
        };

    // ── Query types (mirror tools/sbox/queries.js) ──

    private static QueryResult ExecLeaderboard(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
    {
        var order = Str(config, "order") ?? "desc";
        var limit = (int)Num(config, "limit", 100);
        var columns = NormalizeOutputColumns(config, "fields", "columns");
        var outputFields = columns.Select(col => col.Key).ToList();
        var multiplier = order == "asc" ? 1 : -1;

        var sorted = entries
            .Select(e => (e.Key, e.Data, Value: AsFinite(ResolveMetricValue(e, config, values))))
            .Where(e => e.Value.HasValue)
            .OrderBy(e => multiplier * e.Value!.Value)
            .Take(Math.Min(Math.Max(limit, 0), 1000))
            .ToList();

        return new QueryResult
        {
            Type = "leaderboard",
            Field = MetricFieldName(config),
            FieldLabel = MetricFieldLabel(config),
            OutputFields = outputFields,
            Columns = columns.Any()
                ? columns.Select(col => ColumnToObject(col)).Cast<object>().ToList()
                : new List<object>(),
            Entries = sorted.Select((e, i) => new QueryEntryResult
            {
                Rank = i + 1,
                Key = e.Key,
                Value = e.Value!.Value,
                Data = columns.Any() ? PickFields(e.Data, columns, e.Key, values) : e.Data,
                OutputValues = OutputValuesForEntry(e.Data, columns, e.Key, values)
            }).ToList()
        };
    }

    private static QueryResult ExecCount(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
    {
        var condition = Str(config, "condition");
        var conditionValue = Str(config, "conditionValue");
        var hasMetric = HasMetric(config);

        int count;
        if (hasMetric && !string.IsNullOrEmpty(condition))
            count = entries.Count(e => EvaluateCond(ResolveMetricValue(e, config, values), condition, conditionValue));
        else
            count = entries.Count;

        return new QueryResult { Type = "count", Count = count };
    }

    private static QueryResult ExecSum(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
    {
        var (sum, counted) = Accumulate(config, entries, values);
        return new QueryResult { Type = "sum", Field = MetricFieldName(config), FieldLabel = MetricFieldLabel(config), Sum = sum, Counted = counted };
    }

    private static QueryResult ExecAverage(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
    {
        var (sum, counted) = Accumulate(config, entries, values);
        return new QueryResult { Type = "average", Field = MetricFieldName(config), FieldLabel = MetricFieldLabel(config), Average = counted > 0 ? sum / counted : 0, Counted = counted };
    }

    private static QueryResult ExecMinMax(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values, string mode)
    {
        double? result = null;
        string? resultKey = null;
        foreach (var e in entries)
        {
            var v = AsFinite(ResolveMetricValue(e, config, values));
            if (v is null) continue;
            if (result is null || (mode == "min" ? v < result : v > result)) { result = v; resultKey = e.Key; }
        }
        return new QueryResult { Type = mode, Field = MetricFieldName(config), FieldLabel = MetricFieldLabel(config), Value = result, Key = resultKey };
    }

    private static (double Sum, int Counted) Accumulate(JsonElement config, List<QueryEntry> entries, IReadOnlyDictionary<string, object?>? values)
    {
        double sum = 0;
        int counted = 0;
        foreach (var e in entries)
            if (AsFinite(ResolveMetricValue(e, config, values)) is double v) { sum += v; counted++; }
        return (sum, counted);
    }

    // ── Source merging and joins (mirror mergeScannedSources) ──

    private static string GetSourceAlias(JsonElement source, string? collectionName, string? collectionId)
    {
        var alias = Str(source, "alias") ?? Str(source, "as");
        if (!string.IsNullOrWhiteSpace(alias)) return alias!.Trim();
        if (!string.IsNullOrWhiteSpace(collectionName)) return collectionName!.Trim();
        if (!string.IsNullOrWhiteSpace(collectionId)) return collectionId!.Trim();
        return "source";
    }

    /// <summary>
    /// Merge scanned sources into a single entry list. Single-source is a straight
    /// map; multi-source builds key-union rows with per-source alias nesting, and
    /// applies join specs when present (mirror <c>mergeScannedSources</c>).
    /// </summary>
    private static List<QueryEntry> MergeScannedSources(List<ScannedSource> sources, JsonElement config, IReadOnlyDictionary<string, object?>? values)
    {
        if (sources.Count == 0) return [];
        if (sources.Count == 1)
            return sources[0].Entries.Select(e => new QueryEntry { Key = e.Key, Data = e.Data }).ToList();

        var first = sources[0];
        var rows = new List<QueryEntry>();
        var rowsByKey = new Dictionary<string, QueryEntry>(StringComparer.Ordinal);

        foreach (var entry in first.Entries)
        {
            if (rowsByKey.TryGetValue(entry.Key, out var existing))
            {
                existing.Data[first.Alias] = entry.Data;
            }
            else
            {
                var data = new Dictionary<string, object?>(StringComparer.Ordinal) { [first.Alias] = entry.Data };
                var row = new QueryEntry { Key = entry.Key, Data = data };
                rows.Add(row);
                rowsByKey[entry.Key] = row;
            }
        }

        for (var i = 1; i < sources.Count; i++)
        {
            var sourceInfo = sources[i];
            var joinSpec = NormalizeJoinSpec(sourceInfo, i, config);
            if (joinSpec is not null)
            {
                rows = ApplyJoinedSource(rows, sourceInfo, joinSpec, values);
                rowsByKey = RebuildRowsByKey(rows);
                continue;
            }

            rowsByKey = RebuildRowsByKey(rows);
            foreach (var entry in sourceInfo.Entries)
            {
                if (rowsByKey.TryGetValue(entry.Key, out var existing))
                {
                    existing.Data[sourceInfo.Alias] = entry.Data;
                }
                else
                {
                    var data = new Dictionary<string, object?>(StringComparer.Ordinal) { [sourceInfo.Alias] = entry.Data };
                    var row = new QueryEntry { Key = entry.Key, Data = data };
                    rows.Add(row);
                    rowsByKey[entry.Key] = row;
                }
            }
        }

        return rows;
    }
    /// <summary>
    /// Apply config.joins (foreign-key joins on a single source). Each join scans
    /// a foreign collection from ScyllaDB, builds a key→data lookup, and merges
    /// matching records under the join alias. Mirrors the old ReadJoins/ApplyJoinsAsync.
    /// </summary>
    private async Task ApplyConfigJoinsAsync(string projectId, List<QueryEntry> entries, JsonElement config, IReadOnlyDictionary<string, object?>? values, CancellationToken ct)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("joins", out var joinsEl) || joinsEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var j in joinsEl.EnumerateArray())
        {
            if (j.ValueKind != JsonValueKind.Object) continue;
            var cid = Str(j, "sourceCollectionId") ?? Str(j, "collectionId");
            if (string.IsNullOrWhiteSpace(cid)) continue;
            var alias = Str(j, "alias");
            if (string.IsNullOrWhiteSpace(alias)) alias = cid;
            var type = string.Equals(Str(j, "type"), "inner", StringComparison.OrdinalIgnoreCase) ? "inner" : "left";
            var localKey = Str(j, "localKey") ?? "";
            var foreignKey = Str(j, "foreignKey") ?? "";
            // Scan foreign collection.
            IReadOnlyList<JsonElement> foreignRecords;
            try { foreignRecords = await ScanCollectionRecordsAsync(projectId, cid!, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Join scan failed for collection {CollectionId}", cid);
                foreignRecords = Array.Empty<JsonElement>();
            }

            // Build foreign key→data lookup.
            var lookup = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var rec in foreignRecords)
            {
                if (RecordRow.ExtractValueModel(rec) is not Dictionary<string, object?> fdata) continue;
                var recordKey = RecordRow.ExtractKey(rec) ?? "";
                var keyVal = ResolveForeignKeyValue(fdata, recordKey, foreignKey);
                if (keyVal is not null && !lookup.ContainsKey(keyVal)) lookup[keyVal] = fdata;
            }

            // Match entries.
            var survivors = type == "inner" ? new List<QueryEntry>(entries.Count) : null;
            foreach (var entry in entries)
            {
                var localVal = StringifyKey(EndpointExpression.GetNestedValue(entry.Data, localKey));
                if (localVal is not null && lookup.TryGetValue(localVal, out var match))
                {
                    entry.Data[alias!] = match;
                    survivors?.Add(entry);
                }
                else if (type == "left")
                {
                    entry.Data[alias!] = null;
                }
                // inner join with no match: dropped (not added to survivors)
            }
            if (survivors is not null)
            {
                entries.Clear();
                entries.AddRange(survivors);
            }
        }
    }

    // ── Player-profile enrichment (reserved pseudo-join) ──────────────

    /// <summary>
    /// Reserved pseudo-source ID for player-profile enrichment. The
    /// <c>__sbox_</c> prefix is a system namespace that cannot collide with
    /// user-created collections (player collections use <c>col_{guid}</c> or
    /// arbitrary YAML IDs; the collection create path rejects <c>__sbox_</c>-prefixed
    /// IDs). This is NOT a real collection — it triggers a read from
    /// <c>player_profiles</c> keyed by the entry's steamId.
    /// </summary>
    private const string PlayerProfilesPseudoSource = "__sbox_playerProfiles";

    /// <summary>
    /// The key under which enrichment fields are nested in each entry's Data.
    /// Output field paths reference this (e.g. <c>playerProfile.playerName</c>).
    /// </summary>
    private const string EnrichmentKey = "playerProfile";

    /// <summary>
    /// Apply player-profile enrichment to entries whose key is a steamId. Reads
    /// <c>player_profiles</c> once for the project, builds a steamId→profile lookup,
    /// and nests the requested fields under <c>playerProfile</c> in each entry's
    /// Data. Auto-enabled when any <c>playerProfile.*</c> field appears in
    /// <c>config.fields</c> — no separate toggle required. Best-effort: a
    /// ScyllaDB failure leaves entries un-enriched.
    /// </summary>
    private async Task ApplyPlayerProfileEnrichmentAsync(
        string projectId, List<QueryEntry> entries, JsonElement config, CancellationToken ct)
    {
        if (config.ValueKind != JsonValueKind.Object) return;

        // Auto-detect: scan config.fields for any playerProfile.* entry.
        // Also check explicit config.enrichment for backward compatibility.
        var requestedFields = new HashSet<string>(StringComparer.Ordinal);
        if (config.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fieldsEl.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.String) continue;
                var name = f.GetString()!;
                if (name.StartsWith("playerProfile.", StringComparison.Ordinal))
                    requestedFields.Add(name["playerProfile.".Length..]);
            }
        }

        // Backward compat: explicit config.enrichment.fields
        if (config.TryGetProperty("enrichment", out var enrichmentEl) && enrichmentEl.ValueKind == JsonValueKind.Object)
        {
            if (enrichmentEl.TryGetProperty("fields", out var efEl) && efEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in efEl.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.String) continue;
                    requestedFields.Add(f.GetString()!);
                }
            }
        }

        if (requestedFields.Count == 0) return; // no playerProfile.* fields selected

        // Map requested field names to (source column, output name) pairs.
        var fields = new List<(string Source, string Output)>();
        foreach (var name in requestedFields)
        {
            if (EnrichmentFieldMap.TryGetValue(name, out var mapped))
                fields.Add((mapped.Source, mapped.Output));
        }
        if (fields.Count == 0) return;

        // Read all player profiles for the project in one query.
        IReadOnlyList<JsonElement> profiles;
        try { profiles = await _store.ReadProjectProfilesAsync(projectId, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Player-profile enrichment failed for {ProjectId} — entries left un-enriched", projectId);
            return;
        }

        // Build steamId → enrichment dict lookup.
        var lookup = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile.ValueKind != JsonValueKind.Object) continue;
            var steamId = profile.TryGetProperty("steam_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString() : null;
            if (string.IsNullOrEmpty(steamId)) continue;

            var enrichment = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (sourceField, outputField) in fields)
            {
                if (profile.TryGetProperty(sourceField, out var valEl))
                {
                    enrichment[outputField] = ProfileFieldToObject(valEl, sourceField);
                }
            }
            if (enrichment.Count > 0)
                lookup[steamId] = enrichment;
        }

        // Merge enrichment into each entry whose key is a steamId.
        foreach (var entry in entries)
        {
            // Entry keys for per-player collections are the steamId directly;
            // for some collections they may be {steamId}_{suffix} — take the
            // first underscore-delimited segment if it looks like a steamId.
            var key = entry.Key;
            var steamId = ResolveSteamIdFromKey(key);
            if (steamId is null) continue;
            if (lookup.TryGetValue(steamId, out var enrichment))
            {
                // If the profile row exists but player_name is empty/missing,
                // fall back to the entry payload's playerName (spec:
                // query-player-name-resolution). Other fields stay profile-only.
                if (enrichment.TryGetValue("playerName", out var pn) is false || pn is null or "")
                {
                    var payloadName = ReadEntryPayloadPlayerName(entry.Data);
                    if (!string.IsNullOrEmpty(payloadName)) enrichment["playerName"] = payloadName;
                }
                entry.Data[EnrichmentKey] = enrichment;
            }
            else
            {
                // No profile row at all — still resolve playerName from the
                // payload so the output field isn't blank. Other playerProfile.*
                // fields stay null (left-join semantics).
                var payloadName = ReadEntryPayloadPlayerName(entry.Data);
                entry.Data[EnrichmentKey] = string.IsNullOrEmpty(payloadName)
                    ? null
                    : new Dictionary<string, object?>(StringComparer.Ordinal) { ["playerName"] = payloadName };
            }
        }
    }

    /// <summary>
    /// Read a player name from a record payload, checking common key variants
    /// (playerName, name, PlayerName). Returns null when none are present/non-empty.
    /// </summary>
    private static string? ReadEntryPayloadPlayerName(Dictionary<string, object?> data)
    {
        if (data is null) return null;
        foreach (var key in s_playerNameKeys)
        {
            if (data.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        return null;
    }

    private static readonly string[] s_playerNameKeys = { "playerName", "name", "PlayerName" };

    /// <summary>
    /// Resolve a steamId from an entry key. Keys are either the raw steamId
    /// (17-digit SteamID64) or {steamId}_{suffix} for composite keys. Returns
    /// null for non-steamId keys (e.g. "anonymous", custom keys).
    /// </summary>
    private static string? ResolveSteamIdFromKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var segment = key.Contains('_') ? key.Split('_')[0] : key;
        // SteamID64: 17 digits starting with 7656119.
        return segment.Length == 17 && segment.StartsWith("7656119", StringComparison.Ordinal) && segment.All(char.IsDigit)
            ? segment
            : null;
    }

    /// <summary>
    /// Convert a player_profiles column value to the output representation.
    /// Unix-ms timestamps become ISO-8601 strings for client-friendliness.
    /// </summary>
    private static object? ProfileFieldToObject(JsonElement valEl, string sourceField)
    {
        if (sourceField.EndsWith("_unix_ms", StringComparison.Ordinal) && valEl.ValueKind == JsonValueKind.Number)
        {
            var ms = valEl.TryGetInt64(out var msVal) ? msVal : (long)valEl.GetDouble();
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        return valEl.ValueKind switch
        {
            JsonValueKind.String => valEl.GetString(),
            JsonValueKind.Number => valEl.TryGetInt64(out var l) ? l : (object)valEl.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => valEl.GetRawText()
        };
    }

    /// <summary>
    /// Maps enrichment field names (as used in config + UI) to player_profiles
    /// columns and output field names. This is the canonical field catalog —
    /// the UI picker and the executor both read from here.
    /// </summary>
    private static readonly Dictionary<string, (string Source, string Output)> EnrichmentFieldMap = new(StringComparer.Ordinal)
    {
        ["playerName"] = ("player_name", "playerName"),
        ["isOnline"] = ("is_online", "isOnline"),
        ["lastSeen"] = ("last_seen_unix_ms", "lastSeen"),
        ["lastHeartbeat"] = ("last_heartbeat_unix_ms", "lastHeartbeat"),
        ["onlineSince"] = ("online_since_unix_ms", "onlineSince"),
        ["sessionCount"] = ("session_count", "sessionCount"),
        ["totalSeconds"] = ("total_seconds", "totalSeconds"),
        ["currentSessionSeconds"] = ("current_session_last_seconds", "currentSessionSeconds"),
        ["lastEventType"] = ("last_event_type", "lastEventType"),
        ["lastEndpointSlug"] = ("last_endpoint_slug", "lastEndpointSlug"),
    };

    /// <summary>
    /// The list of enrichment fields available in the UI picker. Exposed so the
    /// controller/view can render the same catalog the executor uses.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Source, string Output)> EnrichmentFields => EnrichmentFieldMap;

    /// <summary>Resolve a foreign record's join key (empty/key/_key uses record key; otherwise named field).</summary>
    private static string? ResolveForeignKeyValue(Dictionary<string, object?> data, string recordKey, string foreignKey)
    {
        if (string.IsNullOrWhiteSpace(foreignKey) || foreignKey is "key" or "_key")
            return string.IsNullOrEmpty(recordKey) ? null : recordKey;
        var resolved = StringifyKey(EndpointExpression.GetNestedValue(data, foreignKey));
        return resolved ?? (string.IsNullOrEmpty(recordKey) ? null : recordKey);
    }

    /// <summary>Normalize a join key value to a canonical string.</summary>
    private static string? StringifyKey(object? v) => v switch
    {
        null => null,
        JsUndefined => null,
        string s => s,
        bool b => b ? "true" : "false",
        double d when double.IsFinite(d) && d == Math.Floor(d) => ((long)d).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    /// <summary>Port of Bun <c>normalizeJoinSpec</c>: resolves a join config for a source.</summary>
    private static JoinSpec? NormalizeJoinSpec(ScannedSource sourceInfo, int index, JsonElement config)
    {
        // Direct join on the source object.
        JoinSpec? direct = null;
        if (sourceInfo.Source.TryGetProperty("join", out var joinEl) && joinEl.ValueKind == JsonValueKind.Object)
            direct = ParseJoinSpec(joinEl);

        // Configured join from config.joins array.
        JoinSpec? configured = null;
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("joins", out var joinsEl) && joinsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var j in joinsEl.EnumerateArray())
            {
                if (j.ValueKind != JsonValueKind.Object) continue;
                var sourceName = Str(j, "source") ?? Str(j, "alias") ?? Str(j, "as");
                var matches = IntField(j, "index") == index
                    || IntField(j, "sourceIndex") == index
                    || (!string.IsNullOrEmpty(sourceName) && sourceName == sourceInfo.Alias)
                    || (Str(j, "collectionId") == sourceInfo.CollectionId);
                if (matches) { configured = ParseJoinSpec(j); break; }
            }
        }

        var join = direct ?? configured;
        return join;
    }

    private static JoinSpec? ParseJoinSpec(JsonElement join)
    {
        var left = Str(join, "left") ?? Str(join, "local") ?? Str(join, "localField") ?? Str(join, "from");
        var right = Str(join, "right") ?? Str(join, "foreign") ?? Str(join, "foreignField") ?? Str(join, "to");

        // Check on.left / on.right nested form.
        if (left is null && join.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.Object)
        {
            left = Str(onEl, "left") ?? Str(onEl, "local");
            right ??= Str(onEl, "right") ?? Str(onEl, "foreign");
        }

        var type = (Str(join, "type") ?? (BoolField(join, "required") ? "inner" : null)) ?? "left";
        var many = BoolField(join, "many") || BoolField(join, "multiple") || (Str(join, "cardinality") == "many");

        return new JoinSpec(left ?? "$key", right ?? "$key", type.ToLowerInvariant(), many);
    }

    /// <summary>Port of Bun <c>applyJoinedSource</c>.</summary>
    private static List<QueryEntry> ApplyJoinedSource(List<QueryEntry> rows, ScannedSource sourceInfo, JoinSpec joinSpec, IReadOnlyDictionary<string, object?>? values)
    {
        // Build index of foreign entries by join key.
        var index = new Dictionary<string, List<QueryEntry>>(StringComparer.Ordinal);
        foreach (var entry in sourceInfo.Entries)
        {
            var joinKey = JoinValueKey(ResolveJoinValue(joinSpec.Right, entry.Data, entry.Key, values));
            if (joinKey is null) continue;
            if (!index.TryGetValue(joinKey, out var list)) { list = []; index[joinKey] = list; }
            list.Add(entry);
        }

        var nextRows = new List<QueryEntry>(rows.Count);
        foreach (var row in rows)
        {
            var localKey = JoinValueKey(ResolveJoinValue(joinSpec.Left, row.Data, row.Key, values));
            var matches = localKey is null ? [] : (index.TryGetValue(localKey, out var m) ? m : []);

            if (matches.Count == 0 && joinSpec.Type == "inner") continue;

            if (joinSpec.Many)
            {
                row.Data[sourceInfo.Alias] = matches.Select(x => (object?)x.Data).ToList();
            }
            else
            {
                row.Data[sourceInfo.Alias] = matches.Count > 0 ? matches[0].Data : null;
            }
            nextRows.Add(row);
        }

        return nextRows;
    }

    private static Dictionary<string, QueryEntry> RebuildRowsByKey(List<QueryEntry> rows)
    {
        var byKey = new Dictionary<string, QueryEntry>(StringComparer.Ordinal);
        foreach (var row in rows) byKey[row.Key] = row;
        return byKey;
    }

    private static string? JoinValueKey(object? value)
    {
        if (value is null or JsUndefined) return null;
        if (value is string s) return s;
        if (value is bool b) return b ? "true" : "false";
        if (value is double d)
        {
            if (!double.IsFinite(d)) return null;
            if (d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString(CultureInfo.InvariantCulture);
        }
        if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
        if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
        // Objects/arrays: JSON-stringify (mirror joinValueKey).
        try { return JsonSerializer.Serialize(value); }
        catch { return value.ToString(); }
    }

    private static object? ResolveJoinValue(string path, Dictionary<string, object?> data, string key, IReadOnlyDictionary<string, object?>? values)
    {
        if (string.IsNullOrEmpty(path) || path == "$key" || path == "_key") return key;
        var context = BuildContext(data, key, values);
        try
        {
            if (path.Contains("{{", StringComparison.Ordinal))
                return EndpointExpression.ResolveTemplate(path, context);
            return EndpointExpression.GetNestedValue(context, path);
        }
        catch
        {
            return null;
        }
    }

    // ── Computed fields (mirror applyComputedFields) ──

    private static ComputedInfo ApplyComputedFields(List<QueryEntry> entries, JsonElement config, IReadOnlyDictionary<string, object?>? values)
    {
        var definitions = NormalizeComputedFields(config);
        if (definitions.Count == 0) return new ComputedInfo([], 0);

        int errors = 0;
        foreach (var entry in entries)
        {
            if (entry.Data is not Dictionary<string, object?> data) continue;
            if (!data.TryGetValue("computed", out var computedObj) || computedObj is not Dictionary<string, object?>)
            {
                computedObj = new Dictionary<string, object?>(StringComparer.Ordinal);
                data["computed"] = computedObj;
            }
            var computed = (Dictionary<string, object?>)computedObj;

            foreach (var def in definitions)
            {
                var context = BuildContext(data, entry.Key, values);
                context["computed"] = new Dictionary<string, object?>(computed, StringComparer.Ordinal);

                object? value;
                try
                {
                    value = EvaluateConfigValue(def.Definition, context);
                }
                catch
                {
                    errors++;
                    object? dv = null;
                    var hasDefault = false;
                    if (def.Definition.ValueKind == JsonValueKind.Object)
                    {
                        if (def.Definition.TryGetProperty("default", out var defaultEl)) { dv = PropValue(defaultEl); hasDefault = true; }
                        else if (def.Definition.TryGetProperty("defaultValue", out var defaultValEl)) { dv = PropValue(defaultValEl); hasDefault = true; }
                    }
                    if (hasDefault)
                    {
                        value = dv is string s ? EndpointExpression.ResolveTemplate(s, context) : dv;
                    }
                    else
                    {
                        value = null;
                    }
                }

                if (value is not null and not JsUndefined)
                {
                    computed[def.Name] = value;
                    if (def.Definition.ValueKind == JsonValueKind.Object && def.Definition.TryGetProperty("targetPath", out var tpEl) && tpEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tpEl.GetString()))
                    {
                        AssignValueAtPath(data, tpEl.GetString()!, value);
                    }
                    else if (IsSimpleIdentifier(def.Name))
                    {
                        data[def.Name] = value;
                    }
                    else
                    {
                        AssignValueAtPath(data, def.Name, value);
                    }
                }
            }
        }

        return new ComputedInfo(
            definitions.Select(d => new ComputedFieldDef(d.Name, d.Label)).ToList(),
            errors);
    }

    private static List<ComputedFieldRaw> NormalizeComputedFields(JsonElement config)
    {
        var result = new List<ComputedFieldRaw>();
        if (config.ValueKind != JsonValueKind.Object) return result;

        JsonElement? raw = null;
        foreach (var name in new[] { "computedFields", "computations", "calculations", "computed" })
        {
            if (config.TryGetProperty(name, out var p)) { raw = p; break; }
        }
        if (raw is null) return result;

        List<JsonElement> definitions;
        if (raw.Value.ValueKind == JsonValueKind.Array)
        {
            definitions = raw.Value.EnumerateArray().ToList();
        }
        else if (raw.Value.ValueKind == JsonValueKind.Object)
        {
            // Object form: { name: definition, ... } → [{ name, ...definition }]
            definitions = [];
            foreach (var prop in raw.Value.EnumerateObject())
            {
                var def = prop.Value.ValueKind == JsonValueKind.Object
                    ? MergeName(prop.Value, prop.Name)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = prop.Name, ["expression"] = PropValue(prop.Value) });
                definitions.Add(def);
            }
        }
        else
        {
            return result;
        }

        foreach (var def in definitions)
        {
            if (def.ValueKind != JsonValueKind.Object) continue;
            var name = Str(def, "name") ?? Str(def, "key") ?? Str(def, "id");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var label = Str(def, "label") ?? Str(def, "title") ?? name;
            result.Add(new ComputedFieldRaw(name!.Trim(), label!.Trim(), def));
        }

        return result.Take(50).ToList();
    }

    private static JsonElement MergeName(JsonElement obj, string name)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = PropValue(prop.Value);
        dict["name"] = name;
        return JsonSerializer.SerializeToElement(dict);
    }

    private static object? PropValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetDouble(out var d) ? d : null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ValueKind == JsonValueKind.Object || el.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<object>(el.GetRawText())
            : null
    };

    /// <summary>Port of Bun <c>evaluateConfigValue</c>.</summary>
    private static object? EvaluateConfigValue(JsonElement definition, Dictionary<string, object?> context)
    {
        if (definition.ValueKind != JsonValueKind.Object) return null;

        var expr = Str(definition, "expression") ?? Str(definition, "expr") ?? Str(definition, "valueExpression");
        if (expr is not null)
            return EndpointExpression.EvaluateExpression(expr, context);

        if (definition.TryGetProperty("template", out var tmpl) && tmpl.ValueKind == JsonValueKind.String)
            return EndpointExpression.ResolveTemplate(tmpl.GetString()!, context);

        var path = Str(definition, "path") ?? Str(definition, "field");
        if (path is not null)
            return EndpointExpression.GetNestedValue(context, path);

        if (definition.TryGetProperty("value", out var valEl))
        {
            if (valEl.ValueKind == JsonValueKind.String)
                return EndpointExpression.ResolveTemplate(valEl.GetString()!, context);
            return PropValue(valEl);
        }

        return null;
    }

    // ── Output columns (mirror normalizeOutputColumns/pickFields/outputValuesForEntry) ──

    private static List<OutputColumn> NormalizeOutputColumns(JsonElement config, params string[] fieldNames)
    {
        var result = new List<OutputColumn>();
        foreach (var fieldName in fieldNames)
        {
            if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(fieldName, out var fields)) continue;
            if (fields.ValueKind != JsonValueKind.Array) continue;
            foreach (var f in fields.EnumerateArray())
            {
                OutputColumn? col = f.ValueKind == JsonValueKind.String
                    ? ParseStringColumn(f.GetString() ?? "")
                    : f.ValueKind == JsonValueKind.Object ? ParseObjectColumn(f) : null;
                if (col is not null) result.Add(col);
            }
        }
        return result.Take(100).ToList();
    }

    private static OutputColumn? ParseStringColumn(string path)
    {
        path = path.Trim();
        if (path.Length == 0) return null;
        return new OutputColumn(path, path, path, null, null, null);
    }

    private static OutputColumn? ParseObjectColumn(JsonElement field)
    {
        var expression = Str(field, "expression") ?? Str(field, "expr") ?? Str(field, "valueExpression");
        var template = Str(field, "template");
        var path = Str(field, "path") ?? Str(field, "field") ?? Str(field, "source") ?? Str(field, "valuePath");
        var fallbackKey = path ?? expression ?? template ?? Str(field, "label") ?? Str(field, "title");
        var key = Str(field, "name") ?? Str(field, "key") ?? Str(field, "id") ?? fallbackKey ?? "";
        if (string.IsNullOrWhiteSpace(key)) return null;
        var label = Str(field, "label") ?? Str(field, "title") ?? key;
        var outputPath = Str(field, "outputPath");
        return new OutputColumn(key!.Trim(), label!.Trim(), path, expression, template, outputPath);
    }

    private static object? ResolveColumnValue(Dictionary<string, object?> data, OutputColumn column, string key, IReadOnlyDictionary<string, object?>? values)
    {
        var context = BuildContext(data, key, values);
        try
        {
            if (column.Expression is not null)
                return EndpointExpression.EvaluateExpression(column.Expression, context);
            if (column.Template is not null)
                return EndpointExpression.ResolveTemplate(column.Template, context);
            if (column.Path is not null)
                return EndpointExpression.GetNestedValue(context, column.Path);
        }
        catch { return null; }
        return null;
    }

    private static Dictionary<string, object?> PickFields(Dictionary<string, object?> data, List<OutputColumn> columns, string key, IReadOnlyDictionary<string, object?>? values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var val = ResolveColumnValue(data, column, key, values);
            if (val is not null and not JsUndefined)
            {
                var outputPath = column.OutputPath
                    ?? (column.Path is not null && column.Key == column.Path ? column.Path : column.Key);
                AssignValueAtPath(result, outputPath, val);
            }
        }
        return result;
    }

    private static Dictionary<string, object?> OutputValuesForEntry(Dictionary<string, object?> data, List<OutputColumn> columns, string key, IReadOnlyDictionary<string, object?>? values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
            result[column.Key] = ResolveColumnValue(data, column, key, values);
        return result;
    }

    private static Dictionary<string, object?> ColumnToObject(OutputColumn col)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = col.Key,
            ["label"] = col.Label,
        };
        if (col.Path is not null) dict["path"] = col.Path;
        if (col.Expression is not null) dict["expression"] = col.Expression;
        if (col.Template is not null) dict["template"] = col.Template;
        if (col.OutputPath is not null) dict["outputPath"] = col.OutputPath;
        return dict;
    }

    // ── Value-path assignment (mirror assignValueAtPath) ──

    private static void AssignValueAtPath(Dictionary<string, object?> target, string fieldPath, object? value)
    {
        if (string.IsNullOrEmpty(fieldPath)) return;
        var parts = fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var cursor = target;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (!cursor.TryGetValue(part, out var child) || child is not Dictionary<string, object?> childDict)
            {
                childDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                cursor[part] = childDict;
            }
            cursor = childDict;
        }
        cursor[parts[^1]] = value;
    }

    // ── Metric resolution (mirror resolveMetricValue/getMetricFieldName) ──

    private static object? ResolveMetricValue(QueryEntry entry, JsonElement config, IReadOnlyDictionary<string, object?>? values)
    {
        var context = BuildContext(entry.Data, entry.Key, values);
        try
        {
            var expr = MetricExpression(config);
            if (!string.IsNullOrWhiteSpace(expr))
                return EndpointExpression.EvaluateExpression(expr!, context);

            // Object-valued config.field: evaluate via evaluateConfigValue.
            if (config.TryGetProperty("field", out var fieldEl) && fieldEl.ValueKind == JsonValueKind.Object)
                return EvaluateConfigValue(fieldEl, context);

            var field = (Str(config, "field") ?? "").Trim();
            if (field.Length == 0) return null;
            if (field.Contains("{{", StringComparison.Ordinal))
                return EndpointExpression.EvaluateExpression(field, context);
            return EndpointExpression.GetNestedValue(context, field);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildContext(Dictionary<string, object?> data, string key, IReadOnlyDictionary<string, object?>? values)
    {
        var context = new Dictionary<string, object?>(data, StringComparer.Ordinal);
        if (!context.ContainsKey("key")) context["key"] = key;
        context["_key"] = key;
        if (values is { Count: > 0 }) context["values"] = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        return context;
    }

    /// <summary>config.valueExpression ?? config.fieldExpression ?? config.expression</summary>
    private static string? MetricExpression(JsonElement config)
        => Str(config, "valueExpression") ?? Str(config, "fieldExpression") ?? Str(config, "expression");

    private static bool HasMetric(JsonElement config)
        => !string.IsNullOrWhiteSpace(MetricExpression(config)) || config.TryGetProperty("field", out _);

    private static string MetricFieldName(JsonElement config)
    {
        if (config.TryGetProperty("field", out var fieldEl))
        {
            if (fieldEl.ValueKind == JsonValueKind.String)
            {
                var s = fieldEl.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            else if (fieldEl.ValueKind == JsonValueKind.Object)
            {
                return Str(fieldEl, "name") ?? Str(fieldEl, "key") ?? Str(fieldEl, "path") ?? Str(fieldEl, "field") ?? "value";
            }
        }
        return MetricExpression(config) ?? "value";
    }

    /// <summary>config.fieldLabel || config.valueLabel || metricFieldName</summary>
    private static string MetricFieldLabel(JsonElement config)
        => Str(config, "fieldLabel") ?? Str(config, "valueLabel") ?? MetricFieldName(config);

    private static bool EvaluateCond(object? val, string condition, string? cv)
    {
        var num = double.TryParse(cv, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
        var asNum = AsFinite(val);
        return condition switch
        {
            "eq" => StrEq(val, cv) || (asNum is double e && !double.IsNaN(num) && e == num),
            "neq" => !StrEq(val, cv) && !(asNum is double ne && !double.IsNaN(num) && ne == num),
            "gt" => asNum is double g && !double.IsNaN(num) && g > num,
            "gte" => asNum is double ge && !double.IsNaN(num) && ge >= num,
            "lt" => asNum is double l && !double.IsNaN(num) && l < num,
            "lte" => asNum is double le && !double.IsNaN(num) && le <= num,
            "exists" => val is not null and not JsUndefined,
            "not_exists" => val is null or JsUndefined,
            _ => true
        };
    }

    private static bool StrEq(object? val, string? cv)
        => val is string s ? string.Equals(s, cv, StringComparison.Ordinal) : false;

    // ── Performance tracking ──

    private static QueryPerformance BuildPerformance(List<QueryEntry> entries, JsonElement config, ComputedInfo computedInfo, List<ScannedSource> sources, long durationMs)
    {
        var totalKeys = sources.Count > 0
            ? sources.Sum(s => s.Entries.Count)
            : entries.Count;
        var perf = new QueryPerformance
        {
            At = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            DurationMs = durationMs,
            KeysScanned = totalKeys,
            Sources = sources.Count > 0
                ? sources.Select(s => new QuerySourceInfo
                {
                    CollectionId = s.CollectionId,
                    Name = s.CollectionName,
                    Alias = s.Alias,
                    KeysScanned = s.Entries.Count,
                    Source = "scylladb",
                }).ToList()
                : []
        };
        if (computedInfo.Definitions.Count > 0) perf.ComputedFields = computedInfo.Definitions;
        if (computedInfo.Errors > 0) perf.ComputedErrors = computedInfo.Errors;
        return perf;
    }

    private static int ReadCacheTtl(JsonElement query)
    {
        if (query.TryGetProperty("cache", out var cacheEl) && cacheEl.ValueKind == JsonValueKind.Object)
        {
            if (cacheEl.TryGetProperty("ttlSeconds", out var ttlEl) && ttlEl.TryGetInt32(out var ttl))
                return ttl;
        }
        return 300;
    }

    // ── Cache helpers ──

    private static string CacheKey(string queryId, string? projectId)
        => string.IsNullOrEmpty(projectId) ? queryId : $"{projectId}:{queryId}";

    private static JsonElement? ExtractDefinition(JsonElement row)
    {
        if (row.TryGetProperty("definition_json", out var def))
        {
            if (def.ValueKind == JsonValueKind.Object) return def;
            if (def.ValueKind == JsonValueKind.String)
            {
                try { return JsonDocument.Parse(def.GetString()!).RootElement.Clone(); }
                catch { return null; }
            }
        }
        // Fallback: if the row IS the definition (test compatibility).
        if (row.TryGetProperty("type", out _) || row.TryGetProperty("sources", out _))
            return row;
        return null;
    }

    // ── JSON helpers ──

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double Num(JsonElement el, string name, double fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : fallback;

    private static int? IntField(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;

    private static bool BoolField(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static bool IsSimpleIdentifier(string value)
        => !string.IsNullOrEmpty(value) && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static double? AsFinite(object? v) => v switch
    {
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        long l => l,
        int i => i,
        decimal m => (double)m,
        _ => null
    };

    // ── Internal types ──

    private sealed class QueryEntry
    {
        public string Key { get; init; } = "";
        public Dictionary<string, object?> Data { get; init; } = new();
    }

    private sealed record ScannedSource(JsonElement Source, string CollectionId, string? CollectionName, string Alias, List<QueryEntry> Entries);
    private sealed record JoinSpec(string Left, string Right, string Type, bool Many);
    private sealed record OutputColumn(string Key, string Label, string? Path, string? Expression, string? Template, string? OutputPath);
    private sealed record ComputedFieldRaw(string Name, string Label, JsonElement Definition);
    private sealed record ComputedInfo(List<ComputedFieldDef> Definitions, int Errors);
    private sealed record CachedQuery(QueryResult Result, DateTimeOffset CachedAt, DateTimeOffset ExpiresAt)
    {
        public string CachedAtIso => CachedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        public string ExpiresAtIso => ExpiresAt.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}

// ── Result types ──

public sealed class QueryResult
{
    public string Type { get; set; } = "";
    public string? Message { get; set; }
    public string? Field { get; set; }
    public string? FieldLabel { get; set; }
    public int? Count { get; set; }
    public double? Sum { get; set; }
    public double? Average { get; set; }
    public double? Value { get; set; }
    public string? Key { get; set; }
    public int? Counted { get; set; }
    public List<string>? OutputFields { get; set; }
    public List<object>? Columns { get; set; }
    public List<QueryEntryResult>? Entries { get; set; }
    public QueryPerformance? Performance { get; set; }
    public bool FromCache { get; set; }
    public string? CachedAt { get; set; }
    public string? ExpiresAt { get; set; }

    public QueryResult Clone()
    {
        var clone = (QueryResult)MemberwiseClone();
        return clone;
    }
}

public sealed class QueryEntryResult
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public object? Value { get; set; }
    [JsonPropertyName("data")] public object? Data { get; set; }
    [JsonPropertyName("outputValues")] public Dictionary<string, object?>? OutputValues { get; set; }
}

public sealed class QueryPerformance
{
    [JsonPropertyName("at")] public string? At { get; set; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
    [JsonPropertyName("keysScanned")] public int KeysScanned { get; set; }
    [JsonPropertyName("sources")] public List<QuerySourceInfo>? Sources { get; set; }
    [JsonPropertyName("computedFields")] public List<ComputedFieldDef>? ComputedFields { get; set; }
    [JsonPropertyName("computedErrors")] public int? ComputedErrors { get; set; }
}

public sealed class QuerySourceInfo
{
    [JsonPropertyName("collectionId")] public string CollectionId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("keysScanned")] public int KeysScanned { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

public sealed class ComputedFieldDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    public ComputedFieldDef() { }
    public ComputedFieldDef(string name, string label) { Name = name; Label = label; }
}
