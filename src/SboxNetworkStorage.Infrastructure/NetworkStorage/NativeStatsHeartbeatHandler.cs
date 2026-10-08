using System.Collections.Concurrent;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Native heartbeat handler that writes player-stats through the
/// <see cref="INetworkStorageDataPlane"/> and emits a session.heartbeat analytics
/// event so the Game Analytics dashboard reflects live presence. When ScyllaDB
/// is primary, writes are ScyllaDB-authoritative (fail-closed) with Bunny
/// fallback on read miss; when not primary, the legacy Bunny data plane is used
/// unchanged. Serves <c>POST /v3/storage/:projectId/stats/heartbeat</c>.
/// </summary>
public sealed class NativeStatsHeartbeatHandler(
    IStorageApiKeyResolver apiKeyResolver,
    INetworkStorageDataPlane dataPlane,
    IPlayerAnalyticsService analytics)
{
    /// <summary>Collection ID for player-stats records in the data plane.</summary>
    private const string StatsCollectionId = "player-stats";

    /// <summary>
    /// Analytics events are throttled to one flush per player per 10 seconds.
    /// The game client sends heartbeats every ~2s; flushing an analytics event
    /// on every heartbeat would write 30 events/minute/player to ScyllaDB
    /// unnecessarily. The dashboard's <c>OnlineStaleSeconds</c> window is 60s,
    /// so a 10s flush interval keeps presence fresh without flooding.
    /// </summary>
    private static readonly TimeSpan AnalyticsFlushInterval = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, long> _lastAnalyticsFlushPerSteam = new();

    public async Task<NativeHeartbeatResult> ExecuteAsync(
        string projectId, string? apiKey, string? steamId, JsonElement? body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            return new NativeHeartbeatResult(401, new { error = new { code = "UNAUTHORIZED", message = "Missing apiKey" } });

        var auth = await apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, ct);
        if (auth is null || !auth.Enabled)
            return new NativeHeartbeatResult(401, new { error = new { code = "UNAUTHORIZED", message = "Invalid or disabled API key" } });

        // Resolve steamId from body if not in query
        if (string.IsNullOrEmpty(steamId) && body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("steamId", out var s) && s.ValueKind == JsonValueKind.String)
                steamId = s.GetString();
            else if (b.TryGetProperty("steamId", out var n) && n.ValueKind == JsonValueKind.Number)
                steamId = n.GetInt64().ToString();
        }

        if (string.IsNullOrEmpty(steamId))
            return new NativeHeartbeatResult(400, new { error = new { code = "MISSING_STEAM_ID", message = "steamId is required" } });

        // Heartbeats are a ~2s-interval best-effort presence ping. The store I/O
        // must never hold the request or surface a 500, so bound it tightly and
        // degrade to a soft response when the store is slow or unavailable.
        //
        // The record key is the bare steamId. It MUST satisfy the ScyllaDB
        // record-key grammar (^[a-zA-Z0-9_:-]{1,256}$, see
        // ScyllaDbResourceStore.ValidateRecordKey) — no '.' is allowed. The
        // legacy Bunny-era "{steamId}.json" filename carries a '.', so once
        // ScyllaDB became the primary data plane every heartbeat read+write threw
        // ArgumentException: the read missed (playtime reset to 2s) and the write
        // failed (persisted:false), silently freezing player-stats. Bare steamId
        // matches the key convention every other collection's records use.
        var recordKey = steamId;
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));

        object totalSeconds = 2L;
        var persisted = false;
        try
        {
            // Read existing stats through the data plane (ScyllaDB-first with
            // Bunny fallback when primary, or Bunny-only when not primary).
            JsonElement? existing = null;
            try
            {
                var readResult = await dataPlane.ReadRecordAsync(
                    auth.UserId, projectId, StatsCollectionId, recordKey, budget.Token);
                if (readResult.Found)
                    existing = readResult.Value;
            }
            catch when (!ct.IsCancellationRequested) { /* first heartbeat or transient read */ }

            var merged = new Dictionary<string, object?>
            {
                ["steamId"] = steamId,
                ["lastHeartbeat"] = now.ToString("O"),
                ["lastHeartbeatUnixMs"] = now.ToUnixTimeMilliseconds(),
            };

            if (existing is { ValueKind: JsonValueKind.Object } prev)
            {
                foreach (var prop in prev.EnumerateObject())
                {
                    if (!merged.ContainsKey(prop.Name))
                        merged[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }
                // Cadence-independent playtime accrual: credit the elapsed
                // time since the previous heartbeat, clamped to [0, 120]s to
                // bound credit across missed heartbeats/reconnects. Falls back
                // to +2 when no prior timestamp exists (legacy/first heartbeat).
                // Spec: game-client-analytics-telemetry.
                var prevTotal = prev.TryGetProperty("totalSeconds", out var ts) && ts.ValueKind == JsonValueKind.Number
                    ? ts.GetInt64() : 0;
                var prevMs = prev.TryGetProperty("lastHeartbeatUnixMs", out var lhm) && lhm.ValueKind == JsonValueKind.Number
                    ? lhm.GetInt64() : 0;
                var delta = prevMs > 0
                    ? Math.Clamp((nowMs - prevMs) / 1000, 0, 120)
                    : 2L;
                totalSeconds = prevTotal + delta;
            }
            else
            {
                merged["firstSeen"] = now.ToString("O");
            }
            merged["totalSeconds"] = totalSeconds;

            // Write through the data plane (ScyllaDB-authoritative when primary,
            // fail-closed: a write failure throws and the heartbeat degrades to
            // a soft response instead of silently persisting nowhere).
            var mergedElement = JsonSerializer.SerializeToElement(merged);
            await dataPlane.WriteRecordAsync(
                auth.UserId, projectId, StatsCollectionId, recordKey, mergedElement, budget.Token);
            persisted = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // honor a real client disconnect / request timeout
        }
        catch
        {
            // Bounded-timeout or store failure: presence is best-effort, so degrade
            // to a soft response instead of holding the worker or emitting a 500.
            persisted = false;
        }

        // Emit a throttled session.heartbeat analytics event so the Game
        // Analytics dashboard reflects live presence. The ingester updates
        // player_profiles (last_seen, is_online) and the per-player timeline.
        // Best-effort: a failure is logged inside the ingester and never
        // propagates to the heartbeat response.
        //
        // CRITICAL: use CancellationToken.None, NOT the request's ct. This is a
        // fire-and-forget write that must outlive the HTTP request. The request's
        // CancellationToken (context.RequestAborted) cancels the moment the 200
        // response is flushed — using it here cancelled the ScyllaDB analytics
        // write mid-commit, so heartbeats never landed in player_analytics_events
        // and the dashboard showed no activity. The endpoint-execution path
        // (EndpointExecutionEndpoints.ExecuteEndpointAsync) uses the same
        // fire-and-forget + CancellationToken.None pattern for the same reason.
        // The ingester swallows all non-cancellation exceptions, so there is no
        // unhandled-task risk.
        _ = EmitThrottledAnalyticsEventAsync(projectId, steamId, body, now, CancellationToken.None);

        return new NativeHeartbeatResult(200, new
        {
            ok = true,
            persisted,
            steamId,
            projectId,
            totalSeconds,
            lastHeartbeat = now.ToString("O"),
        });
    }

    /// <summary>
    /// Fire-and-forget: emit a session.heartbeat analytics event at most once
    /// per <see cref="AnalyticsFlushInterval"/> per player. The ingester writes
    /// the event to player_analytics_events and upserts player_profiles
    /// (last_seen_unix_ms, is_online, last_heartbeat_unix_ms).
    /// </summary>
    private async Task EmitThrottledAnalyticsEventAsync(
        string projectId, string steamId, JsonElement? body, DateTimeOffset now, CancellationToken ct)
    {
        var nowMs = now.ToUnixTimeMilliseconds();

        // Honour the client's real session so heartbeats, the session.join and
        // (via the ingester's currentSessionId backfill) endpoint calls all group
        // into one coherent play session. Fall back to a stable per-player id only
        // when the client omits one. The client also reports the event kind
        // (join/leave/heartbeat) and the cumulative session seconds — both were
        // previously discarded (hardcoded to "hb:{steamId}" and 2s), which is why
        // the dashboard showed fragmented sessions and wrong playtime.
        var clientSessionId = ReadBodyString(body, "sessionId", "session_id");
        var sessionId = string.IsNullOrEmpty(clientSessionId) ? $"hb:{steamId}" : clientSessionId!;
        var eventKind = (ReadBodyString(body, "event") ?? "heartbeat").ToLowerInvariant();
        var eventType = eventKind == "join" ? "session.join"
            : eventKind is "leave" or "disconnect" ? "session.leave"
            : "session.heartbeat";
        var sessionSeconds = ReadBodyLong(body, "sessionSeconds", "session_seconds");

        // Join/leave are once-per-session lifecycle signals — never throttle them.
        // Heartbeats flush at most once per player per interval.
        var key = $"{projectId}:{steamId}";
        if (eventType == "session.heartbeat")
        {
            if (_lastAnalyticsFlushPerSteam.TryGetValue(key, out var lastMs)
                && nowMs - lastMs < (long)AnalyticsFlushInterval.TotalMilliseconds)
                return;
            _lastAnalyticsFlushPerSteam[key] = nowMs;
        }

        // Inner analytics payload. RecordEndpointEventAsync wraps this in the
        // event envelope itself, so keep the fields flat (no re-enveloping) — a
        // nested "payload" would double-nest sessionSeconds where the playtime
        // and session-journey duration builders cannot read it. Forward any
        // client-reported FPS / performance so the dashboard can surface it: a
        // scalar fps:60 is normalized to { average: 60 } (the shape the timeline
        // engine reads), an fps/performance object passes through as-is. These
        // were previously dropped, so heartbeat FPS never reached analytics.
        var payload = new Dictionary<string, object>
        {
            ["steamId"] = steamId,
            ["sessionId"] = sessionId,
            ["sessionSeconds"] = sessionSeconds,
        };
        // Forward playerName from the heartbeat body so the ingester's
        // name-preserving merge names the profile from heartbeats (spec:
        // game-client-analytics-telemetry). The client resolves it via
        // Connection.Local.DisplayName → Steam.PersonaName.
        var playerName = ReadBodyString(body, "playerName", "player_name");
        if (!string.IsNullOrEmpty(playerName)) payload["playerName"] = playerName!;
        var fps = ReadBodyFps(body);
        if (fps is not null) payload["fps"] = fps;
        var performance = ReadBodyObject(body, "performance");
        if (performance is not null) payload["performance"] = performance;

        try
        {
            await analytics.RecordEndpointEventAsync(
                projectId,
                steamId,
                endpointSlug: "stats-heartbeat",
                eventType: eventType,
                payload: payload,
                trackedFieldDeltas: null,
                cancellationToken: ct);
        }
        catch
        {
            // Best-effort: analytics must never break the heartbeat path.
            // Reset the throttle so the next heartbeat retries.
            if (eventType == "session.heartbeat") _lastAnalyticsFlushPerSteam.TryRemove(key, out _);
        }
    }

    private static string? ReadBodyString(JsonElement? body, params string[] names)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b) return null;
        foreach (var name in names)
            if (b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        return null;
    }

    private static long ReadBodyLong(JsonElement? body, params string[] names)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b) return 0;
        foreach (var name in names)
            if (b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt64(out var l)) return l;
                if (v.TryGetDouble(out var d)) return (long)d; // client reports cumulative seconds as a double
            }
        return 0;
    }

    /// <summary>
    /// Reads client-reported FPS from the heartbeat body. An object
    /// ({ average, min, max } / { fpsAverage, ... }) passes through; a scalar
    /// (fps: 60) is normalized to { average: 60 } so the timeline engine — which
    /// reads fps.average/avg — surfaces it on the session card and timeline.
    /// </summary>
    private static object? ReadBodyFps(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty("fps", out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Object) return v.Clone();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d > 0)
            return new Dictionary<string, object> { ["average"] = d };
        return null;
    }

    /// <summary>Reads a JSON object property from the heartbeat body, cloned for detached reuse.</summary>
    private static object? ReadBodyObject(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b
            || !b.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            return null;
        return v.Clone();
    }
}

public sealed record NativeHeartbeatResult(int StatusCode, object Body);
