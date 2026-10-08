using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Native .NET port of the legacy Bun <c>observePlayerAnalytics</c>
/// (<c>services/network-storage-player-analytics.js</c>) ingestion path. Emits
/// normalized analytics events from the .NET data plane (endpoint execution,
/// storage CRUD, the explicit analytics-events endpoint) and maintains the
/// per-player profile/presence, per-player session, and project issues indexes
/// in ScyllaDB — replacing the Bunny fast-edge index files
/// (<c>analytics/recent.json</c>, <c>players/{steamId}.json</c>,
/// <c>events/{steamId}/{date}.json</c>, <c>sessions/{steamId}/{sessionId}.json</c>,
/// <c>issues|incidents/recent.json</c>) that Bun maintained.
///
/// All ingestion is best-effort: a failure is logged and swallowed so it never
/// breaks the originating storage/endpoint operation.
/// </summary>
public sealed class PlayerAnalyticsIngester(
    INetworkStorageStore store,
    TimeProvider time,
    AnalyticsIngestionFailureTracker failureTracker,
    ILogger<PlayerAnalyticsIngester> logger) : IPlayerAnalyticsService
{
    private const int OnlineStaleSeconds = 60;
    private const int MaxImpliedPlaytimeDeltaSeconds = 600;
    private const int MaxLabelLength = 120;
    private const int MaxEndpointSlugLength = 96;
    private const int MaxRecentIncidents = 250;
    private const int IncidentWindowDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// A reusable empty-object JsonElement. Callers that record an event without a
    /// payload pass <c>null</c>, which becomes <c>default(JsonElement)</c>
    /// (ValueKind=Undefined). Serializing an Undefined element throws
    /// <see cref="InvalidOperationException"/> ("Operation is not valid due to the
    /// current state of the object"), which previously aborted the whole ingest
    /// (dropping save-all / endpoint-call events and their ledger deltas). Using a
    /// concrete empty object instead keeps the payload serializable.
    /// </summary>
    private static readonly JsonElement EmptyObjectElement = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // The legacy storage-CRUD path passes the player steamId as RecordKey.
            var steamId = request.RecordKey;
            if (string.IsNullOrWhiteSpace(steamId) || !IsPlausibleSteamId(steamId)) return;

            var payload = ToJsonElement(request.Payload);
            var type = request.EventType;
            var ts = time.GetUtcNow().ToUnixTimeMilliseconds();
            var playerName = ReadString(payload, "playerName", "player_name") ?? string.Empty;
            var sessionId = ReadString(payload, "sessionId", "session_id");
            var label = ReadString(payload, "label") ?? DeriveLabel(type);
            var source = NormalizeSource(ReadString(payload, "source"));
            var endpointSlug = ReadString(payload, "endpointSlug", "endpoint_slug");
            var innerPayload = ExtractObject(payload, "payload") ?? payload;

            await IngestAsync(
                request.ProjectId, steamId, type, label, source, endpointSlug,
                request.CollectionId, sessionId, playerName, ts, innerPayload,
                trackedFieldDeltas: null, cancellationToken);
            if (failureTracker.RecordSuccess())
                logger.LogInformation("Analytics ingestion recovered after sustained failure for project {ProjectId}", request.ProjectId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to record player analytics event to ScyllaDB for project {ProjectId}", request.ProjectId);
            if (failureTracker.RecordFailure())
                _ = failureTracker.FireTransitionAlertAsync(request.ProjectId, failureTracker.ConsecutiveFailures, CancellationToken.None);
        }
    }

    public async Task RecordEndpointEventAsync(
        string projectId,
        string steamId,
        string endpointSlug,
        string eventType,
        IReadOnlyDictionary<string, object>? payload,
        IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(steamId) || !IsPlausibleSteamId(steamId)) return;
        try
        {
            var ts = time.GetUtcNow().ToUnixTimeMilliseconds();
            var payloadElement = payload is null
                ? EmptyObjectElement
                : JsonSerializer.SerializeToElement(payload, JsonOptions);
            var label = payload is not null && payload.TryGetValue("label", out var l) && l is string s
                ? s
                : DeriveLabel(eventType);

            await IngestAsync(
                projectId, steamId, eventType, label,
                source: "network-storage-library",
                endpointSlug: endpointSlug,
                collectionId: null,
                sessionId: payload is not null && payload.TryGetValue("sessionId", out var sid) && sid is string ss ? ss : null,
                playerName: payload is not null && payload.TryGetValue("playerName", out var pn) && pn is string pns ? pns : string.Empty,
                ts: ts,
                innerPayload: payloadElement,
                trackedFieldDeltas: trackedFieldDeltas,
                cancellationToken);
            if (failureTracker.RecordSuccess())
                logger.LogInformation("Analytics ingestion recovered after sustained failure for project {ProjectId}", projectId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to record endpoint analytics event to ScyllaDB for project {ProjectId} endpoint {Slug}", projectId, endpointSlug);
            if (failureTracker.RecordFailure())
                _ = failureTracker.FireTransitionAlertAsync(projectId, failureTracker.ConsecutiveFailures, CancellationToken.None);
        }
    }

    private async Task IngestAsync(
        string projectId,
        string steamId,
        string type,
        string label,
        string source,
        string? endpointSlug,
        string? collectionId,
        string? sessionId,
        string playerName,
        long ts,
        JsonElement innerPayload,
        IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas,
        CancellationToken ct)
    {
        var category = EventCategory(type);
        // Backfill the session id from the player's active session for events
        // that arrive without one (e.g. endpoint.call from save-all/load-player,
        // whose request body carries no sessionId). Without this they are stored
        // under "no-session" and render as "Activity outside sessions" instead of
        // grouping with the heartbeats/joins of the same play session. Mirrors the
        // Bun ingester, which stamps sessionless non-session events with the
        // profile's currentSessionId.
        if (string.IsNullOrEmpty(sessionId) && category != "session")
        {
            var activeProfile = await store.ReadPlayerProfileAsync(projectId, steamId, ct);
            if (activeProfile is { ValueKind: JsonValueKind.Object } ap
                && ap.TryGetProperty("current_session_id", out var csid)
                && csid.ValueKind == JsonValueKind.String)
            {
                var cs = csid.GetString();
                if (!string.IsNullOrEmpty(cs)) sessionId = cs;
            }
        }
        var eventId = Guid.NewGuid().ToString("N");
        var tsIso = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        var eventPayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["steamId"] = steamId,
            ["type"] = type,
            ["ts"] = tsIso,
            ["label"] = Truncate(label, MaxLabelLength),
            ["source"] = source,
            ["category"] = category,
            ["endpointSlug"] = endpointSlug,
            ["collectionId"] = collectionId,
            ["sessionId"] = sessionId,
            ["payload"] = BoundPayload(innerPayload, category),
        }, JsonOptions);

        // Append the event to the per-player timeline.
        await store.InsertPlayerAnalyticsEventV2Async(
            projectId, steamId, ts, eventId, type, category,
            Truncate(label, MaxLabelLength),
            Truncate(endpointSlug ?? string.Empty, MaxEndpointSlugLength),
            collectionId ?? string.Empty,
            eventPayload, ct);

        // Maintain the per-player profile/presence and session row.
        await UpdateProfileAsync(projectId, steamId, playerName, type, category,
            sessionId, endpointSlug, ts, innerPayload, ct);
        if (category == "session" && !string.IsNullOrEmpty(sessionId))
        {
            await UpdateSessionAsync(projectId, steamId, sessionId!, type, ts, innerPayload, ct);
        }

        // Project issues/incidents index (errors, warnings, performance, network, voice, endpoint failures).
        var incidentKey = IncidentKeyFor(type, category, innerPayload);
        if (incidentKey is not null)
        {
            var bucketDate = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await store.InsertProjectIssueAsync(
                projectId, bucketDate, ts, eventId, steamId, incidentKey.Category,
                type, Truncate(incidentKey.Label, MaxLabelLength), eventPayload, ct);
        }

        // Tracked-field progression deltas → ledger_entries (for the dashboard ledger chart).
        if (trackedFieldDeltas is { Count: > 0 })
        {
            foreach (var delta in trackedFieldDeltas)
            {
                if (Math.Abs(delta.Delta) < double.Epsilon) continue;
                var entry = JsonSerializer.SerializeToElement(new
                {
                    field = delta.Field,
                    collection_id = delta.CollectionId,
                    before = delta.Before,
                    after = delta.After,
                    delta = delta.Delta,
                    source = delta.Source ?? endpointSlug ?? type,
                    steam_id = steamId,
                    ts = tsIso,
                    event_id = eventId,
                }, JsonOptions);
                await store.InsertLedgerEntryAsync(
                    projectId, delta.CollectionId, steamId,
                    sequence: ts, // monotonic per (project, collection, steamId)
                    entryJson: entry, ct);
            }
        }
    }

    private async Task UpdateProfileAsync(
        string projectId, string steamId, string playerName, string type, string category,
        string? sessionId, string? endpointSlug, long ts, JsonElement payload, CancellationToken ct)
    {
        var existing = await store.ReadPlayerProfileAsync(projectId, steamId, ct);
        var p = (existing is { } ex && ex.ValueKind == JsonValueKind.Object) ? ex : default;
        // JsonElement.TryGetProperty throws on a default/Undefined instance; guard with ValueKind.
        bool Has(in JsonElement el, string name, out JsonElement v)
        {
            if (el.ValueKind != JsonValueKind.Object) { v = default; return false; }
            return el.TryGetProperty(name, out v);
        }
        bool isOnline = Has(p, "is_online", out var io) && io.ValueKind == JsonValueKind.True;
        long? onlineSince = Has(p, "online_since_unix_ms", out var os) && os.ValueKind == JsonValueKind.Number ? os.GetInt64() : null;
        long lastSeen = Has(p, "last_seen_unix_ms", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt64() : ts;
        long? lastHeartbeat = Has(p, "last_heartbeat_unix_ms", out var lh) && lh.ValueKind == JsonValueKind.Number ? lh.GetInt64() : null;
        string? currentSessionId = Has(p, "current_session_id", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString() : null;
        long currentSessionLastSeconds = Has(p, "current_session_last_seconds", out var css) && css.ValueKind == JsonValueKind.Number ? css.GetInt64() : 0;
        long totalSeconds = Has(p, "total_seconds", out var ts2) && ts2.ValueKind == JsonValueKind.Number ? ts2.GetInt64() : 0;
        long sessionCount = Has(p, "session_count", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt64() : 0;
        var managed = Has(p, "managed_counters_json", out var mc)
            ? (mc.ValueKind == JsonValueKind.String
                ? ParseCounters(mc.GetString())
                : mc.ValueKind == JsonValueKind.Object
                    ? ParseCounters(mc.GetRawText())
                    : new Dictionary<string, long>(StringComparer.Ordinal))
            : new Dictionary<string, long>(StringComparer.Ordinal);
        var lastEventType = Has(p, "last_event_type", out var le) && le.ValueKind == JsonValueKind.String ? le.GetString() : null;
        var lastEndpointSlug = Has(p, "last_endpoint_slug", out var les) && les.ValueKind == JsonValueKind.String ? les.GetString() : null;

        var name = string.IsNullOrEmpty(playerName)
            ? (Has(p, "player_name", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null)
            : playerName;

        // Implied playtime (only when the player is already online and this isn't a leave).
        if (type != "session.leave")
        {
            var implied = Math.Min(MaxImpliedPlaytimeDeltaSeconds, (ts - lastSeen) / 1000);
            if (implied > 0 && isOnline && type != "session.join") totalSeconds += implied;
        }

        // Session-specific transitions.
        if (category == "session")
        {
            var incomingSessionId = sessionId ?? currentSessionId;
            var previousSessionId = currentSessionId;
            var sessionSeconds = ReadLong(payload, "sessionSeconds", "session_seconds");
            var durationSeconds = ReadLong(payload, "durationSeconds", "duration_seconds");
            var previousSessionSeconds = incomingSessionId is not null && previousSessionId is not null && incomingSessionId != previousSessionId ? 0 : currentSessionLastSeconds;
            var explicitSeconds = Math.Max(Math.Max(sessionSeconds, durationSeconds), 0);
            long playtimeDelta;
            if (explicitSeconds > previousSessionSeconds)
            {
                playtimeDelta = explicitSeconds - previousSessionSeconds;
                currentSessionLastSeconds = explicitSeconds;
            }
            else if (type != "session.join")
            {
                playtimeDelta = Math.Min(MaxImpliedPlaytimeDeltaSeconds, (ts - lastSeen) / 1000);
                currentSessionLastSeconds = previousSessionSeconds + playtimeDelta;
            }
            else
            {
                playtimeDelta = 0;
                currentSessionLastSeconds = explicitSeconds;
            }
            if (playtimeDelta > 0) totalSeconds += playtimeDelta;
            currentSessionId = incomingSessionId;

            if (type == "session.join")
            {
                sessionCount++;
                isOnline = true;
                onlineSince ??= ts;
            }
            else if (type == "session.leave")
            {
                isOnline = false;
                currentSessionId = null;
                currentSessionLastSeconds = 0;
                onlineSince = null;
            }
            else if (type == "session.heartbeat")
            {
                // A heartbeat with a session id that differs from the profile's
                // prior current_session_id means a new session started without
                // an explicit session.join (common for dedicated servers and
                // for clients that reconnect after a leave). Count it as a
                // session start so the dashboard's session count and session
                // journey reflect reality, not just explicit joins.
                if (!string.IsNullOrEmpty(incomingSessionId)
                    && incomingSessionId != previousSessionId)
                {
                    sessionCount++;
                }
                isOnline = true;
                onlineSince ??= ts;
                lastHeartbeat = ts;
            }
        }
        else if (type != "session.leave")
        {
            isOnline = true;
            onlineSince ??= ts;
            currentSessionId ??= sessionId ?? $"{steamId}:{ts}";
        }

        // Online staleness: flip offline if no heartbeat for > OnlineStaleSeconds.
        if (isOnline && lastHeartbeat is { } hb && (ts - hb) / 1000 > OnlineStaleSeconds)
        {
            isOnline = false;
        }

        // Managed counters (local errors, endpoint failures, performance, network, voice).
        var incidentKey = IncidentKeyFor(type, category, payload);
        if (incidentKey is not null)
        {
            managed[incidentKey.Key] = (managed.TryGetValue(incidentKey.Key, out var c) ? c : 0) + 1;
        }
        // Total event count for the dashboard's "N events" badge on each player
        // card. Stored in managed_counters_json under a reserved key so it
        // rides on the existing upsert without a schema change.
        managed["__totalEvents"] = (managed.TryGetValue("__totalEvents", out var ev) ? ev : 0) + 1;

        await store.UpsertPlayerProfileAsync(
            projectId, steamId, name ?? string.Empty, isOnline, onlineSince,
            ts, lastHeartbeat, currentSessionId,
            currentSessionLastSeconds == 0 ? null : currentSessionLastSeconds,
            totalSeconds, sessionCount, type, endpointSlug,
            JsonSerializer.Serialize(managed, JsonOptions),
            ts, ct);
    }

    private async Task UpdateSessionAsync(
        string projectId, string steamId, string sessionId, string type, long ts, JsonElement payload, CancellationToken ct)
    {
        var existing = await store.ReadPlayerSessionAsync(projectId, steamId, sessionId, ct);
        var s = existing is { ValueKind: JsonValueKind.Object } ? existing.Value : default;
        // JsonElement.TryGetProperty throws on a default/Undefined instance;
        // guard so a first-ever session row (existing is null) doesn't throw.
        long? startedAt = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("started_at_unix_ms", out var sa) && sa.ValueKind == JsonValueKind.Number ? sa.GetInt64() : null;
        long? lastHeartbeat = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("last_heartbeat_at_unix_ms", out var lh) && lh.ValueKind == JsonValueKind.Number ? lh.GetInt64() : null;
        long? endedAt = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("ended_at_unix_ms", out var ea) && ea.ValueKind == JsonValueKind.Number ? ea.GetInt64() : null;

        if (type == "session.join") startedAt ??= ts;
        if (type == "session.heartbeat")
        {
            // A heartbeat may arrive without a prior session.join (dedicated
            // servers, reconnects). If no session row exists yet, treat this
            // heartbeat as the session start so the session journey has a
            // meaningful started_at instead of null.
            startedAt ??= ts;
            lastHeartbeat = ts;
        }
        if (type == "session.leave") endedAt = ts;

        string? fpsMetrics = null;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("fps", out var fps) && fps.ValueKind != JsonValueKind.Undefined)
                fpsMetrics = fps.GetRawText();
            else if (payload.TryGetProperty("performance", out var perf) && perf.ValueKind != JsonValueKind.Undefined)
                fpsMetrics = perf.GetRawText();
        }

        await store.InsertPlayerSessionAsync(
            projectId, steamId, sessionId, startedAt, lastHeartbeat, endedAt,
            fpsMetrics, summaryJson: null, ct);
    }

    // ── Normalization (faithful port of the Bun helpers) ─────────────

    private static string EventCategory(string type)
    {
        var raw = string.IsNullOrEmpty(type) ? "custom" : type;
        if (raw.StartsWith("session.", StringComparison.Ordinal)) return "session";
        if (raw.StartsWith("endpoint.", StringComparison.Ordinal)) return "endpoint";
        if (raw.StartsWith("storage.", StringComparison.Ordinal)) return "storage";
        if (raw.StartsWith("tracked_field.", StringComparison.Ordinal)) return "tracked_field";
        if (raw.StartsWith("warning.", StringComparison.Ordinal)) return "warning";
        if (raw.StartsWith("error.", StringComparison.Ordinal)) return "error";
        if (raw.StartsWith("performance.", StringComparison.Ordinal)) return "performance";
        if (raw.StartsWith("network.", StringComparison.Ordinal) || raw.StartsWith("networkroster.", StringComparison.Ordinal)) return "network";
        if (raw.StartsWith("voice.", StringComparison.Ordinal)) return "voice";
        if (raw.StartsWith("custom.", StringComparison.Ordinal)) return "custom";
        var dot = raw.IndexOf('.');
        return dot > 0 ? raw[..dot] : "custom";
    }

    private static string NormalizeSource(string? source)
    {
        var raw = (source ?? "server").Trim().ToLowerInvariant();
        return raw is "server" or "network-storage-library" or "manual" or "api" ? raw : "server";
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

    private static string DeriveLabel(string type)
    {
        if (string.IsNullOrEmpty(type)) return "event";
        var parts = type.Split('.');
        return parts.Length > 1 ? string.Join(' ', parts[1..]) : type;
    }

    /// <summary>Strip stacks unless the event is an explicit error/diagnostic (parity with Bun boundAnalyticsPayload).</summary>
    private static JsonElement BoundPayload(JsonElement payload, string category)
    {
        // An Undefined element (from a null payload) is not serializable — collapse
        // it to an empty object so the enclosing event payload never throws.
        if (payload.ValueKind == JsonValueKind.Undefined) return EmptyObjectElement;
        if (payload.ValueKind != JsonValueKind.Object) return payload;
        var includeStack = category is "error" or "warning";
        if (includeStack) return payload;
        // Shallow-copy without "stack" to bound payload size for non-error events.
        var copy = new Dictionary<string, JsonElement>();
        foreach (var prop in payload.EnumerateObject())
        {
            if (prop.NameEquals("stack") || prop.NameEquals("stackTrace")) continue;
            copy[prop.Name] = prop.Value;
        }
        return JsonSerializer.SerializeToElement(copy, JsonOptions);
    }

    private static IncidentKey? IncidentKeyFor(string type, string category, JsonElement payload)
    {
        var t = (type ?? string.Empty).ToLowerInvariant();
        if (category == "warning") return new("localWarnings", "warning", t);
        if (t.StartsWith("endpoint.client", StringComparison.Ordinal) || t.StartsWith("endpoint.diagnostic", StringComparison.Ordinal))
            return new("endpointFailures", "endpoint", t);
        if (t == "performance.low_fps" || t == "performance.stutter" || t == "session.low_fps.summary")
            return new("performanceIssues", "performance", t);
        if (t == "network.poor_quality") return new("networkQuality", "network", t);
        if (t == "voice.activity") return new("voiceActivity", "voice", t);
        if (category == "error") return new("localErrors", "error", t);
        // Outdated revision is handled by callers that carry revision state (not
        // available on the .NET data plane yet); skip to avoid false positives.
        return null;
    }

    private static bool IsPlausibleSteamId(string s) => s.Length >= 4 && s.Length <= 32 && s.All(char.IsDigit);

    private static JsonElement ToJsonElement(object? payload)
    {
        if (payload is JsonElement je) return je;
        if (payload is null) return EmptyObjectElement;
        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }

    private static string? ReadString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static long ReadLong(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number)
            {
                if (p.TryGetInt64(out var l)) return l;
            }
        }
        return 0;
    }

    private static JsonElement? ExtractObject(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
    }

    private static Dictionary<string, long> ParseCounters(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v))
                    result[prop.Name] = v;
            }
            return result;
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private sealed record IncidentKey(string Key, string Category, string Label);
}
