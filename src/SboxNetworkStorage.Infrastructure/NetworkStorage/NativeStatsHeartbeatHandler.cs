using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Native heartbeat handler that writes player-stats through the
/// <see cref="INetworkStorageDataPlane"/> and emits a session.heartbeat analytics
/// event so the Game Analytics dashboard reflects live presence. Writes are
/// store-authoritative (fail-closed) with workspace fallback on read miss.
/// Serves <c>POST /v3/storage/:projectId/stats/heartbeat</c>.
/// </summary>
public sealed class NativeStatsHeartbeatHandler(
    IStorageApiKeyResolver apiKeyResolver,
    INetworkStorageDataPlane dataPlane,
    IPlayerAnalyticsService analytics,
    HeartbeatFailureThrottle failureThrottle,
    HeartbeatAnalyticsGate analyticsGate)
{
    /// <summary>Collection ID for player-stats records in the data plane.</summary>
    private const string StatsCollectionId = "player-stats";

    /// <summary>
    /// Validates the API key from the request headers before the body is read.
    /// Failed attempts are counted per (client IP, project) and blocked once the
    /// shared throttle trips, whatever Steam id the caller claims.
    /// </summary>
    public async Task<HeartbeatAuthentication> AuthenticateAsync(
        string projectId, string? apiKey, string clientIp, CancellationToken ct)
    {
        if (failureThrottle.IsBlocked(clientIp, projectId))
            return HeartbeatAuthentication.Reject(429, "RATE_LIMITED", "Too many failed attempts. Try again later.");

        if (string.IsNullOrEmpty(apiKey))
        {
            failureThrottle.RecordFailure(clientIp, projectId);
            return HeartbeatAuthentication.Reject(401, "UNAUTHORIZED", "Missing apiKey");
        }

        var auth = await apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, ct);
        if (auth is null || !auth.Enabled)
        {
            failureThrottle.RecordFailure(clientIp, projectId);
            return HeartbeatAuthentication.Reject(401, "UNAUTHORIZED", "Invalid or disabled API key");
        }

        return new HeartbeatAuthentication(auth, null);
    }

    public async Task<NativeHeartbeatResult> ExecuteAsync(
        string projectId, StorageApiKeyAuthResult auth, string? steamId, JsonElement? body, CancellationToken ct)
    {
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
        // The record key is the bare steamId. It MUST satisfy the store
        // record-key grammar (^[a-zA-Z0-9_:-]{1,256}$, see
        // INetworkStorageStore.ValidateRecordKey) — no '.' is allowed. The
        // legacy workspace-era "{steamId}.json" filename carries a '.', so with the
        // store as the data plane every heartbeat read+write would throw
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
            // Read existing stats through the data plane (store-first with
            // workspace fallback).
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

            // Write through the data plane (store-authoritative,
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
        // The analytics service only queues the event for the background writer, so awaiting it
        // never waits on the database and cannot fail the heartbeat.
        await EmitThrottledAnalyticsEventAsync(projectId, steamId, body, ct);

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
    /// per <see cref="HeartbeatAnalyticsGate.Interval"/> per player. The ingester writes
    /// the event to player_analytics_events and upserts player_profiles
    /// (last_seen_unix_ms, is_online, last_heartbeat_unix_ms).
    /// </summary>
    private async Task EmitThrottledAnalyticsEventAsync(
        string projectId, string steamId, JsonElement? body, CancellationToken ct)
    {
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
        if (eventType == "session.heartbeat" && !analyticsGate.TryAcquire(projectId, steamId))
            return;

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
            // The queued write is in-memory and non-blocking; use a non-cancellable token so a
            // completed request cannot cancel the analytics event before the writer drains it.
            await analytics.RecordEndpointEventAsync(
                projectId,
                steamId,
                endpointSlug: "stats-heartbeat",
                eventType: eventType,
                payload: payload,
                trackedFieldDeltas: null,
                cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best-effort: analytics must never break the heartbeat path.
            // Reset the throttle so the next heartbeat retries.
            if (eventType == "session.heartbeat") analyticsGate.Release(projectId, steamId);
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

/// <summary>Outcome of <see cref="NativeStatsHeartbeatHandler.AuthenticateAsync"/>: the key, or the rejection to send.</summary>
public sealed record HeartbeatAuthentication(StorageApiKeyAuthResult? Auth, NativeHeartbeatResult? Rejection)
{
    public static HeartbeatAuthentication Reject(int statusCode, string code, string message)
        => new(null, new NativeHeartbeatResult(statusCode, new { error = new { code, message } }));
}

