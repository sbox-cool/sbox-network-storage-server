using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Application-layer orchestrator that loads a Network Storage endpoint definition
/// and its read-step record data via <see cref="IEndpointShadowDataSource"/>, then
/// runs the <see cref="EndpointStepExecutor"/> natively. Returns null when the
/// endpoint uses features the native executor does not yet reproduce, so the
/// caller can safely fall back to the authoritative Bun runtime.
///
/// This is the integration seam between the deterministic-expression/step-engine
/// (verified in isolation against golden oracles) and the live data-plane
/// (endpoint definitions + record data stored in ScyllaDB).
/// </summary>
public sealed class NativeEndpointShadowExecutor
{
    private readonly IEndpointShadowDataSource _dataSource;
    private readonly IEndpointWebhookSender? _webhookSender;
    private readonly IPlayerAnalyticsService? _analyticsService;
    private readonly EndpointStepExecutor _executor = new();

    public NativeEndpointShadowExecutor(
        IEndpointShadowDataSource dataSource,
        IEndpointWebhookSender? webhookSender = null,
        IPlayerAnalyticsService? analyticsService = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _webhookSender = webhookSender;
        _analyticsService = analyticsService;
    }

    /// <summary>
    /// Execute an endpoint natively using the deterministic executor.
    /// Returns <c>null</c> when the endpoint is not found, uses unsupported features,
    /// or the data source is unavailable — indicating the caller should delegate to Bun.
    /// </summary>
    public async Task<EndpointExecutionResult?> TryExecuteAsync(
        string projectId,
        string endpointSlug,
        IReadOnlyDictionary<string, object?> input,
        string steamId,
        string userId,
        IReadOnlyDictionary<string, object?> gameValues,
        bool hasSecretKey,
        bool isDedicatedServer,
        CancellationToken ct,
        bool liveServe = false,
        string? playerKeyMode = null,
        bool enforcePublicAccessGates = false)
    {
        // 1. Load endpoint definition from ScyllaDB.
        Dictionary<string, object?>? endpointDef;
        try
        {
            endpointDef = await _dataSource.ReadEndpointDefinitionAsync(projectId, endpointSlug, ct);
        }
        catch
        {
            return null; // data source unavailable → safe fallback
        }

        if (endpointDef == null)
            return null; // endpoint not found → caller handles the Bun path

        // Endpoint-level access gates for PUBLIC HTTP callers. These were enforced by
        // the Bun runtime (controllers/endpoint-modules/execution-routes.js) but were
        // dropped when execution moved to the native executor, so an endpoint marked
        // `requiresSecretKey: true` would happily execute for a plain public key.
        // Checked here, where the definition is already in hand, so the request path
        // takes no extra read.
        //
        // Opt-in, because not every caller is a public HTTP request: the dashboard's
        // endpoint test runner and the shadow-comparison path deliberately execute
        // internal and disabled endpoints, and must not be gated.
        if (enforcePublicAccessGates)
        {
            var denial = CheckPublicAccess(endpointDef, endpointSlug, hasSecretKey);
            if (denial is not null) return denial;
        }

        // Live cutover serving has no Bun in the loop. Record writes/deletes the
        // endpoint queues are flushed durably below, so they are safe to serve. The
        // only operation we cannot fulfill without help is a Discord webhook: if the
        // endpoint may dispatch one (a webhook step, or a workflow whose sub-steps we
        // can't statically inspect) and no live sender is wired, defer to the
        // authoritative Bun path so a notification can never be silently dropped.
        // (Shadow mode never refuses: it dry-runs webhooks and keeps writes in-memory,
        // which is exactly what a read-only parity comparison needs.)
        if (liveServe && _webhookSender is null && EndpointStepExecutor.RequiresLiveWebhookSender(endpointDef))
            return null;
        var execTime = DateTimeOffset.UtcNow;
        // Build the player key from the project's key mode, matching the Bun
        // endpoint-runner logic (tools/sbox/endpoint-runner.js:332-335):
        //   playerSave mode + saveId input → "{steamId}_{saveId}"
        //   otherwise → steamId
        // Before this fix the .NET executor always used bare steamId, which
        // mismatched keys Bun wrote under playerSave mode — the root cause of
        // "no player found" for players whose saves were authored by Bun with
        // composite keys.
        var keyMode = playerKeyMode ?? "player";
        string playerKey;
        if (keyMode == "playerSave"
            && input.TryGetValue("saveId", out var saveIdObj)
            && saveIdObj is string saveIdStr
            && !string.IsNullOrEmpty(saveIdStr))
        {
            playerKey = $"{steamId}_{saveIdStr}";
        }
        else
        {
            playerKey = steamId;
        }

        // 2. Build a minimal context for read-target discovery (matches buildBuiltinContext
        //    key layout so that keys like {{steamId}} resolve correctly).
        var discoveryContext = new Dictionary<string, object?>
        {
            ["input"] = input as Dictionary<string, object?> ?? new Dictionary<string, object?>(input),
            ["steamId"] = steamId,
            ["playerKey"] = playerKey,
            ["_hasSecretKey"] = hasSecretKey,
            ["_isDedicatedServer"] = isDedicatedServer || hasSecretKey,
            ["now"] = execTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["values"] = gameValues as Dictionary<string, object?> ?? new Dictionary<string, object?>(gameValues),
            ["projectId"] = projectId,
            ["userId"] = userId,
        };
        // Merge endpoint-level `let` aliases into discovery context.
        if (endpointDef.GetValueOrDefault("let") is Dictionary<string, object?> aliases && aliases.Count > 0)
            discoveryContext["_aliases"] = aliases;

        // 3. Load collection metadata and build name→ID resolver.
        // Endpoint YAML references collections by NAME (e.g. "players", "skills"),
        // but ScyllaDB stores records under collection ID (e.g. "97b7e26e949d43b4").
        // Bun's findCollection() resolves names→IDs at every step. The .NET executor
        // must do the same or reads return null and writes go to non-existent
        // collection IDs. (tools/sbox/endpoint-runner.js:3038-3045)
        Dictionary<string, string> collectionNameToId;
        try
        {
            var collections = await _dataSource.ListCollectionsAsync(projectId, ct);
            collectionNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in collections)
            {
                var id = col.GetValueOrDefault("collection_id") as string;
                var name = col.GetValueOrDefault("name") as string;
                if (!string.IsNullOrEmpty(id))
                {
                    collectionNameToId[id] = id;
                    if (!string.IsNullOrEmpty(name))
                        collectionNameToId[name] = id;
                }
            }
        }
        catch
        {
            collectionNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        string ResolveCollection(string collection) =>
            collectionNameToId.TryGetValue(collection, out var id) ? id : collection;

        // 4. Discover read targets and pre-fetch records from ScyllaDB.
        // Keys are used EXACTLY as authored in the endpoint YAML — no _default
        // stripping, no key transformation. The endpoint definition is the source
        // of truth for record keys. If the YAML says {{steamId}}, the store key is
        // the bare steamId. If it says {{steamId}}_default, the store key is
        // "{steamId}_default". This matches how the .NET record-CRUD data plane
        // and the ScyllaDB records table work.
        IReadOnlyList<(string CollectionId, string Key)> readTargets;
        try
        {
            readTargets = EndpointStepExecutor.DiscoverReadTargets(endpointDef, discoveryContext);
        }
        catch
        {
            readTargets = Array.Empty<(string, string)>();
        }
        var prefetched = new Dictionary<string, object?>();
        foreach (var (collection, key) in readTargets)
        {
            var collectionId = ResolveCollection(collection);
            var resolvedKey = ResolveRecordKey(key);
            var cacheKey = $"{collectionId}:{resolvedKey}";
            if (prefetched.ContainsKey(cacheKey)) continue;
            try
            {
                prefetched[cacheKey] = await _dataSource.ReadRecordAsync(projectId, collectionId, resolvedKey, ct);
            }
            catch
            {
                prefetched[cacheKey] = null;
            }
        }

        // 4a. LOAD SELF-HEAL (spec: cross-collection-save-consistency — load self-heal).
        //    When a player's `players` cache is ahead of the `skills`/`kills` source
        //    (cross-collection time-skew), the client recomputes a lower input from
        //    the stale source → permanent 409 SAVE_REGRESSION_BLOCKED. To break the
        //    loop at load: detect cache > source and patch the IN-MEMORY source to
        //    match the header (so the client's recomputation equals the header, the
        //    guard passes, and the save persists). This does NOT modify the stored
        //    data — it only heals the in-memory view the executor serves. The stored
        //    source is healed by the recovery tool (Phase 2) or by the save itself
        //    (once the guard passes, the write flushes the consistent values).
        //    Gated strictly: only triggers when cache > source, never on healthy loads.
        if (liveServe && steamId != "anonymous" && collectionNameToId.TryGetValue("players", out var _playersCid))
        {
            TrySelfHealLoad(prefetched, collectionNameToId, steamId);
        }
        // 4b. Pre-fetch collection scans for lookup/filter/lookup_many/random_select steps.
        var scanCollections = EndpointStepExecutor.DiscoverScanCollections(endpointDef);
        var scannedCollections = new Dictionary<string, List<object?>>();
        foreach (var collection in scanCollections)
        {
            var collectionId = ResolveCollection(collection);
            if (scannedCollections.ContainsKey(collectionId)) continue;
            try
            {
                var records = await _dataSource.ScanCollectionAsync(projectId, collectionId, ct);
                scannedCollections[collectionId] = records as List<object?> ?? new List<object?>(records);
            }
            catch
            {
                scannedCollections[collectionId] = new List<object?>();
            }
        }

        // 5. Build the sync request with a record resolver that serves from the
        //    pre-fetched cache (first choice) or hits the data source on-demand
        //    (for keys that weren't discovered, e.g. keys that depend on prior step
        //    results — these will be blocked by the synchronous executor throwing
        //    Unsupported since the async data source can't be called synchronously
        //    from the lock-free step loop, but that's rare for the deterministic
        //    subset we currently support).
        var onDemandCache = new Dictionary<string, object?>(prefetched);
        object? ReadRecord(string collection, string key)
        {
            var cid = ResolveCollection(collection);
            var rk = ResolveRecordKey(key);
            var ck = $"{cid}:{rk}";
            if (onDemandCache.TryGetValue(ck, out var cached)) return cached;
            return null;
        }

        // Live serve captures the endpoint's queued writes/deletes in order so they
        // can be durably flushed to the authoritative store after execution. In shadow
        // mode the list is null: writes only touch the in-memory cache (so later reads
        // in the same execution see them) and Bun's shadow adapter owns the real
        // ScyllaDB write — we never double-write.
        var durableWrites = liveServe
            ? new List<(string Collection, string Key, Dictionary<string, object?>? Data, bool IsDelete)>()
            : null;
        void WriteRecord(string collection, string key, Dictionary<string, object?> data)
        {
            var cid = ResolveCollection(collection);
            var rk = ResolveRecordKey(key);
            onDemandCache[$"{cid}:{rk}"] = data;
            durableWrites?.Add((cid, rk, data, false));
        }
        void DeleteRecordAction(string collection, string key)
        {
            var cid = ResolveCollection(collection);
            var rk = ResolveRecordKey(key);
            onDemandCache[$"{cid}:{rk}"] = null;
            durableWrites?.Add((cid, rk, null, true));
        }

        List<object?> ScanCollection(string collection)
        {
            var cid = ResolveCollection(collection);
            return scannedCollections.TryGetValue(cid, out var list) ? list : new List<object?>();
        }

        // LoadWorkflow delegate: loads workflow definitions from ScyllaDB.
        async Task<Dictionary<string, object?>?> LoadWorkflow(string workflowId, CancellationToken loadCt)
        {
            try
            {
                return await _dataSource.ReadWorkflowDefinitionAsync(projectId, workflowId, loadCt);
            }
            catch
            {
                return null;
            }
        }

        // 4c. SAVE-GUARD INPUT HEAL (spec: cross-collection-save-consistency).
        //     Anti-rollback guards compare the client's recomputed input against a
        //     DENORMALIZED cache the server stores (e.g. players.totalLevel = the sum
        //     of the skills source). When per-collection migration time-skew leaves
        //     the cache ABOVE the source, the client's honest recompute is always
        //     "lower", so EVERY save-all 409s SAVE_REGRESSION_BLOCKED forever — the
        //     player can neither save nor recover (the rejected save is the very one
        //     that would heal the data; the 2026-06-19 "satu" deadlock). The load
        //     self-heal (4a) tries to fix this by patching the client's VIEW, but it
        //     depends on the client re-deriving its input from the patched source,
        //     which is NOT guaranteed — and in production it is not happening.
        //
        //     This breaks the loop authoritatively, server-side: for each assert of
        //     the anti-rollback shape `input.X >= <readStep>.X` (the SAME monotonic
        //     field on both sides), if the client's input.X is BELOW the stored value,
        //     raise input.X UP to the stored value. The guard then passes, the write
        //     persists the stored high-water mark (progress is NEVER lowered), and the
        //     rest of the save proceeds instead of being rejected wholesale.
        //
        //     Safety invariants (each verified by a test):
        //       * Only RAISES, never lowers — a player can never lose progress here.
        //       * Only fields in <see cref="s_monotonicGuardFields"/> are eligible, so
        //         spendable/non-monotonic fields (totalGold) and timestamps (savedAt /
        //         STALE_SAVE) are NEVER clamped, even with a `>=` guard.
        //       * No-op for healthy saves (input >= stored) and legitimate increases.
        //       * Unrecognized guard shapes fail safe to the existing behavior (block).
        IReadOnlyDictionary<string, object?> effectiveInput = input;
        if (liveServe && steamId != "anonymous")
        {
            var healedInput = input is Dictionary<string, object?> inDict
                ? new Dictionary<string, object?>(inDict)
                : new Dictionary<string, object?>(input);
            // Heals are deliberately NOT emitted as analytics events: a drifted
            // player heals on EVERY autosave (the heal unblocks the save but does
            // not change the underlying cache>source drift), so per-save events
            // would flood player_analytics_events. Operators detect the fix by the
            // cessation of SAVE_REGRESSION_BLOCKED 409s and locate players still
            // needing source recovery via diagnose-player-saves.yml (cache vs sum).
            TryHealRegressionGuards(endpointDef, healedInput, discoveryContext, ReadRecord);
            effectiveInput = healedInput;
        }

        var request = new EndpointExecutionRequest(
            Input: effectiveInput,
            SteamId: steamId,
            PlayerKey: playerKey,
            GameValues: gameValues,
            ProjectId: projectId,
            UserId: userId,
            HasSecretKey: hasSecretKey,
            IsDedicatedServer: isDedicatedServer,
            ExecTime: execTime,
            ReadRecord: ReadRecord,
            WriteRecord: WriteRecord,
            DeleteRecord: DeleteRecordAction,
            ScanCollection: ScanCollection,
            LoadWorkflow: LoadWorkflow,
            // Live serve sends real Discord webhooks via the injected sender; shadow
            // mode dry-runs them (deterministic, for parity). SkipSleep is honored only
            // when serving live traffic.
            ExecuteWebhookAsync: liveServe && _webhookSender is not null
                ? (url, payload, wct) => _webhookSender.SendAsync(url, payload, wct)
                : null,
            SkipWebhooks: !liveServe || _webhookSender is null,
            SkipSleep: !liveServe);

        // 5. Execute natively via async path (supports workflows + routed flows).
        //    UnsupportedException → null (caller falls back to Bun).
        EndpointExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(endpointDef, request, ct);
        }
        catch (EndpointExecutionUnsupportedException)
        {
            return null;
        }

        // 6. Live serve only: durably flush the writes/deletes the endpoint queued.
        //    The executor invokes the write/delete delegates ONLY on successful
        //    end-of-steps completion, so durableWrites is empty for error/early-return
        //    results — matching the executor's own deferred-write semantics.
        //
        //    ATOMIC MULTI-COLLECTION FLUSH (spec: cross-collection-save-consistency).
        //    A player's save spans multiple collections (players, skills, kills, …).
        //    A partial flush that persists some writes but not others leaves the
        //    collections at different points in time — the exact cross-collection
        //    time-skew that stranded cerbralone/Sherwood (players header newer than
        //    skills source). To prevent this, the flush is all-or-nothing:
        //      1. Snapshot the pre-write value of each (collection,key) from the
        //         prefetched cache (the pre-read values).
        //      2. Apply all writes/deletes.
        //      3. On ANY failure, rollback: re-apply the pre-write values (upsert the
        //         old value, or delete if it didn't exist before). Since upserts are
        //         idempotent by primary key, rollback restores the exact prior state.
        //      4. After rollback, surface a 500 (fail-closed).
        if (durableWrites is { Count: > 0 })
        {
            // 0. Per-record size gate: reject an over-limit persisted record as
            //    413 BEFORE anything is written (zero mutation). Each record is
            //    measured individually against the store's actual limit — the
            //    whole endpoint request is NOT capped, so a valid multi-record
            //    batch whose total exceeds the single-record limit still commits.
            //    The payload is serialized exactly as the datasource persists it,
            //    so the measured size is the actual persisted size. Genuine
            //    store failures still fall through to the 500 rollback below.
            if (_dataSource is IEndpointRecordSizeLimit sizeLimit)
            {
                foreach (var (collection, key, data, isDelete) in durableWrites)
                {
                    if (isDelete || data is null) continue;
                    var recordBytes = Encoding.UTF8.GetByteCount(
                        JsonSerializer.SerializeToElement(data).GetRawText());
                    if (recordBytes > sizeLimit.MaxPayloadBytes)
                    {
                        return Denied(413, "PAYLOAD_TOO_LARGE",
                            $"Payload exceeds the {sizeLimit.MaxPayloadBytes}-byte limit (endpoint record {collection}/{key}).");
                    }
                }
            }

            // 1. Snapshot pre-write state for each key we're about to touch.
            //    prefetched holds the original pre-read values; missing = didn't exist.
            var preWriteSnapshot = new List<(string Collection, string Key, object? PreValue, bool Existed)>();
            foreach (var (collection, key, _, _) in durableWrites)
            {
                var ck = $"{collection}:{key}";
                if (prefetched.TryGetValue(ck, out var preVal))
                {
                    preWriteSnapshot.Add((collection, key, preVal, true));
                }
                else
                {
                    preWriteSnapshot.Add((collection, key, null, false));
                }
            }

            var applied = new List<(string Collection, string Key, bool IsDelete)>();
            try
            {
                // 2. Apply all writes/deletes.
                foreach (var (collection, key, data, isDelete) in durableWrites)
                {
                    if (isDelete)
                        await _dataSource.DeleteRecordAsync(projectId, collection, key, ct);
                    else
                        await _dataSource.WriteRecordAsync(projectId, collection, key, data!, ct);
                    applied.Add((collection, key, isDelete));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception flushEx)
            {
                // 3. ROLLBACK: re-apply the pre-write values for all keys we already
                //    touched, restoring the exact prior state. Idempotent upserts make
                //    this safe — re-applying the old value overwrites the partial new
                //    write. A key that didn't exist before is deleted.
                foreach (var (collection, key, preValue, existed) in preWriteSnapshot)
                {
                    try
                    {
                        if (!existed || preValue is null)
                            await _dataSource.DeleteRecordAsync(projectId, collection, key, ct);
                        else if (preValue is Dictionary<string, object?> preDict)
                            await _dataSource.WriteRecordAsync(projectId, collection, key, preDict, ct);
                    }
                    catch { /* best-effort rollback; the primary failure is reported below */ }
                }

                // 4. Fail-closed: surface a 500. The caller reports it to
                //    /admin/errors + Discord (Status >= 500 branch).
                return new EndpointExecutionResult(false, 500, new Dictionary<string, object?>
                {
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = "STORAGE_WRITE_FAILED",
                        ["message"] = "Endpoint record write failed to persist; all changes were rolled back.",
                    },
                });
            }
        }

        // 7. Post-flush projections (best-effort — never block the 200 response).
        //    a) Leaderboard: if this was a save-all that wrote the `players`
        //       collection, update `leaderboard_global/default.entriesByPlayer.{steamId}`
        //       incrementally so the in-game leaderboard stays current without the
        //       client calling the separate `save-stats` endpoint.
        //    b) Tracked-field deltas: emit `tracked_field.{field}` analytics events
        //       (before/after/delta) for the dashboard progression charts. Derived
        //       from the pre-read `existing` and post-write `players` values already
        //       in the prefetched cache + durableWrites — zero extra ScyllaDB reads.
        if (liveServe && durableWrites is { Count: > 0 } && steamId != "anonymous")
        {
            await ApplyPostFlushProjectionsAsync(
                projectId, durableWrites, prefetched,
                collectionNameToId, endpointSlug, ct);
        }

        return result;
    }

    /// <summary>
    /// Best-effort post-flush projections: leaderboard update + tracked-field
    /// analytics deltas. Both are derived from values already in memory (the
    /// pre-read cache + the just-flushed writes), so they add zero ScyllaDB
    /// round-trips for the save itself. Failures are swallowed (the save already
    /// succeeded; these are non-critical projections).
    /// </summary>
    private async Task ApplyPostFlushProjectionsAsync(
        string projectId,
        List<(string Collection, string Key, Dictionary<string, object?>? Data, bool IsDelete)> durableWrites,
        Dictionary<string, object?> prefetched,
        Dictionary<string, string> collectionNameToId,
        string endpointSlug,
        CancellationToken ct)
    {
        // Find the players collection write (if any) in this flush. The written
        // record's key is the player the projection describes; the requester can
        // differ (host/dedicated-server proxy saves another player's record).
        var playersCollectionId = collectionNameToId.GetValueOrDefault("players");
        Dictionary<string, object?>? writtenPlayers = null;
        string? playerId = null;
        foreach (var (collection, key, data, isDelete) in durableWrites)
        {
            if (collection == playersCollectionId && !isDelete && data is not null)
            {
                writtenPlayers = data;
                playerId = key;
                break;
            }
        }
        if (writtenPlayers is null || string.IsNullOrEmpty(playerId)) return; // no players write → no projection needed

        // ── a) Leaderboard projection (spec: leaderboard-durability) ──
        // Read the current leaderboard_global/default record, update only this
        // player's entry, write it back. Incremental — never replaces the whole map.
        try
        {
            if (collectionNameToId.TryGetValue("leaderboard_global", out var lbCollectionId))
            {
                var existingLb = await _dataSource.ReadGlobalRecordAsync(projectId, lbCollectionId, "default", ct);
                var lbDict = (existingLb as Dictionary<string, object?>) ?? new Dictionary<string, object?>();
                if (!lbDict.TryGetValue("entriesByPlayer", out var ebpObj) || ebpObj is not Dictionary<string, object?> ebp)
                {
                    ebp = new Dictionary<string, object?>(StringComparer.Ordinal);
                    lbDict["entriesByPlayer"] = ebp;
                }
                // The entry shape mirrors what save-stats writes: {steamId: {totalLevel, totalKills, totalGold, ...}}.
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (writtenPlayers.TryGetValue("totalLevel", out var tl)) entry["totalLevel"] = tl;
                if (writtenPlayers.TryGetValue("totalKills", out var tk)) entry["totalKills"] = tk;
                if (writtenPlayers.TryGetValue("totalGold", out var tg)) entry["totalGold"] = tg;
                if (writtenPlayers.TryGetValue("playerName", out var pn)) entry["playerName"] = pn;
                if (writtenPlayers.TryGetValue("savedAt", out var sa)) entry["savedAt"] = sa;
                ebp[playerId] = entry;
                lbDict["entriesByPlayer"] = ebp;
                await _dataSource.WriteGlobalRecordAsync(projectId, lbCollectionId, "default", lbDict, ct);
            }
        }
        catch { /* best-effort: never block the save response */ }

        // ── b) Tracked-field deltas (spec: network-storage-player-analytics) ──
        // Derive before/after for the known tracked fields from the pre-read
        // `existing` players value and the just-written value. Emit via the
        // analytics service so the dashboard progression chart populates.
        try
        {
            var preKey = $"{playersCollectionId}:{playerId}";
            var prePlayers = prefetched.TryGetValue(preKey, out var preVal)
                ? preVal as Dictionary<string, object?>
                : null;

            var deltas = new List<TrackedFieldDelta>();
            foreach (var field in s_trackedFields)
            {
                var before = ToDouble(prePlayers?.GetValueOrDefault(field));
                var after = ToDouble(writtenPlayers.GetValueOrDefault(field));
                if (Math.Abs(after - before) > double.Epsilon)
                {
                    deltas.Add(new TrackedFieldDelta(
                        Field: field,
                        CollectionId: playersCollectionId ?? "",
                        Before: before,
                        After: after,
                        Delta: after - before,
                        Source: endpointSlug));
                }
            }
            if (deltas.Count > 0)
            {
                // Include playerName from the written players record so the
                // ingester's name-preserving merge names the profile on every
                // save (spec: query-player-name-resolution). Previously null.
                IReadOnlyDictionary<string, object>? payload = writtenPlayers.TryGetValue("playerName", out var pn) && pn is string pns && !string.IsNullOrEmpty(pns)
                    ? new Dictionary<string, object> { ["playerName"] = pns }
                    : null;
                _analyticsService?.RecordEndpointEventAsync(
                    projectId, playerId, endpointSlug,
                    eventType: "session.heartbeat",
                    payload: payload,
                    trackedFieldDeltas: deltas,
                    CancellationToken.None);
            }
        }
        catch { /* best-effort */ }
    }

    private static readonly string[] s_trackedFields = { "totalLevel", "totalKills", "totalGold", "nodesMined" };

    /// <summary>
    /// Public-HTTP access gates, mirroring the Bun runtime's checks in
    /// <c>controllers/endpoint-modules/execution-routes.js</c>. Returns the denial to
    /// send, or null when the caller may proceed.
    /// </summary>
    private static EndpointExecutionResult? CheckPublicAccess(
        Dictionary<string, object?> endpointDef, string endpointSlug, bool hasSecretKey)
    {
        // Only an EXPLICIT false disables. A definition with no "enabled" key stays
        // enabled, so a source-compiled endpoint that never wrote the flag keeps working.
        if (IsFalse(endpointDef.GetValueOrDefault("enabled")))
            return Denied(403, "ENDPOINT_DISABLED", $"Endpoint \"{endpointSlug}\" is disabled.");

        if (IsTrue(endpointDef.GetValueOrDefault("deprecated")))
            return Denied(410, "ENDPOINT_DEPRECATED", $"Endpoint \"{endpointSlug}\" is deprecated and no longer accepts requests.");

        // `exposure: internal` (and the legacy `internal` / `internalOnly` flags) means
        // the endpoint is reachable only from other endpoints/workflows, never over HTTP.
        var exposure = endpointDef.GetValueOrDefault("exposure") as string;
        if (string.Equals(exposure, "internal", StringComparison.OrdinalIgnoreCase)
            || IsTrue(endpointDef.GetValueOrDefault("internal"))
            || IsTrue(endpointDef.GetValueOrDefault("internalOnly")))
        {
            return Denied(404, "ENDPOINT_NOT_FOUND", $"Endpoint \"{endpointSlug}\" was not found.");
        }

        if (IsTrue(endpointDef.GetValueOrDefault("requiresSecretKey")) && !hasSecretKey)
        {
            return Denied(401, "SECRET_KEY_REQUIRED",
                $"Endpoint \"{endpointSlug}\" requires a Network Storage secret key. Send it in the x-secret-key header.");
        }

        return null;
    }

    /// <summary>
    /// Endpoint definitions arrive from ScyllaDB/JSON where a flag may be a real bool,
    /// a boxed JsonElement, or a "true"/"false" string. Only an explicit truthy value
    /// counts — a missing flag is never treated as set.
    /// </summary>
    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) && parsed,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
        _ => false,
    };

    /// <summary>
    /// True only when the flag is explicitly false. A missing flag is NOT false — an
    /// endpoint definition without an "enabled" key stays enabled, matching Bun.
    /// </summary>
    private static bool IsFalse(object? value) => value switch
    {
        bool b => !b,
        string s => bool.TryParse(s, out var parsed) && !parsed,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => true,
        _ => false,
    };

    private static EndpointExecutionResult Denied(int status, string code, string message) =>
        new(false, status, new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        });

    private static double ToDouble(object? v)
    {
        if (v is null) return 0;
        if (v is double d) return d;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is decimal m) return (double)m;
        if (v is float f) return f;
        if (double.TryParse(v.ToString(), out var parsed)) return parsed;
        return 0;
    }

    /// <summary>
    /// Strip the "_default" suffix from endpoint step keys before they reach the
    /// store, matching Bun's resolveRecordKey (tools/sbox/endpoint-runner.js:3052).
    /// Endpoint YAML commonly uses "{{steamId}}_default" as a read/write key.
    /// Bun strips "_default" for single-save collections (maxRecords ≤ 1) — the
    /// vast majority — and resolves it via the record index for multi-save
    /// collections, falling back to the bare steamId when no records exist.
    /// In every code path the store key never contains "_default".
    /// </summary>
    private static string ResolveRecordKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        if (key.EndsWith("_default", StringComparison.Ordinal))
            return key[..^"_default".Length];
        return key;
    }

    /// <summary>
    /// Detect cross-collection time-skew (players cache ahead of skills/kills source)
    /// and patch the IN-MEMORY source to match the header so the client's recomputed
    /// input equals the header — breaking the permanent-409 loop. Does NOT modify
    /// stored data; only heals the in-memory view. Gated strictly: only triggers when
    /// cache > source, never on healthy loads. (spec: load self-heal)
    /// </summary>
    private static void TrySelfHealLoad(
        Dictionary<string, object?> prefetched,
        Dictionary<string, string> collectionNameToId,
        string steamId)
    {
        if (!collectionNameToId.TryGetValue("players", out var playersCid)) return;
        var playersKey = $"{playersCid}:{steamId}";
        if (!prefetched.TryGetValue(playersKey, out var playersObj) || playersObj is not Dictionary<string, object?> players) return;

        // Check skills: if players.totalLevel > sum(skills), patch skills in-memory.
        if (collectionNameToId.TryGetValue("skills", out var skillsCid))
        {
            var skillsKey = $"{skillsCid}:{steamId}";
            if (prefetched.TryGetValue(skillsKey, out var skillsObj) && skillsObj is Dictionary<string, object?> skills)
            {
                var cacheLevel = ToDouble(players.GetValueOrDefault("totalLevel"));
                var srcLevel = SumSkillLevels(skills);
                if (cacheLevel > srcLevel && srcLevel > 0)
                {
                    // Cache is ahead of source. Patch the in-memory skills to match the header
                    // so the client's recomputation (sum of skills) equals the cache. This makes
                    // the save guard pass (input >= existing) without lowering the stored header.
                    PatchSkillLevels(skills, (int)cacheLevel);
                }
            }
        }

        // Check kills: same pattern for totalKills.
        if (collectionNameToId.TryGetValue("kills", out var killsCid))
        {
            var killsKey = $"{killsCid}:{steamId}";
            if (prefetched.TryGetValue(killsKey, out var killsObj) && killsObj is Dictionary<string, object?> kills)
            {
                var cacheKills = ToDouble(players.GetValueOrDefault("totalKills"));
                var srcKills = SumKillCounts(kills);
                if (cacheKills > srcKills && srcKills > 0)
                {
                    PatchKillCounts(kills, (int)cacheKills);
                }
            }
        }
    }

    private static double SumSkillLevels(Dictionary<string, object?> skills)
    {
        if (!skills.TryGetValue("entries", out var entriesObj) || entriesObj is not Dictionary<string, object?> entries) return 0;
        double total = 0;
        foreach (var entry in entries.Values)
        {
            if (entry is Dictionary<string, object?> e && e.TryGetValue("level", out var levelObj))
                total += ToDouble(levelObj);
        }
        return total;
    }

    private static double SumKillCounts(Dictionary<string, object?> kills)
    {
        if (!kills.TryGetValue("counts", out var countsObj) || countsObj is not Dictionary<string, object?> counts) return 0;
        double total = 0;
        foreach (var count in counts.Values)
            total += ToDouble(count);
        return total;
    }

    /// <summary>
    /// Patch skill levels in-memory so they sum to the header value. Distributes the
    /// difference proportionally across existing skills (keeps relative progression).
    /// </summary>
    private static void PatchSkillLevels(Dictionary<string, object?> skills, int targetSum)
    {
        if (!skills.TryGetValue("entries", out var entriesObj) || entriesObj is not Dictionary<string, object?> entries) return;
        var currentSum = SumSkillLevels(skills);
        if (currentSum <= 0) return;
        var ratio = targetSum / currentSum;
        foreach (var (skillName, entryObj) in entries)
        {
            if (entryObj is not Dictionary<string, object?> entry) continue;
            if (entry.TryGetValue("level", out var levelObj))
            {
                var currentLevel = ToDouble(levelObj);
                entry["level"] = Math.Max(1, (int)Math.Round(currentLevel * ratio));
            }
        }
    }

    /// <summary>
    /// Patch kill counts in-memory so they sum to the header value. Distributes the
    /// difference proportionally across existing mob types.
    /// </summary>
    private static void PatchKillCounts(Dictionary<string, object?> kills, int targetSum)
    {
        if (!kills.TryGetValue("counts", out var countsObj) || countsObj is not Dictionary<string, object?> counts) return;
        var currentSum = SumKillCounts(kills);
        if (currentSum <= 0) return;
        var ratio = targetSum / currentSum;
        var patched = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (mob, count) in counts)
        {
            var current = ToDouble(count);
            patched[mob] = Math.Max(0, (int)Math.Round(current * ratio));
        }
        kills["counts"] = patched;
    }

    // ── Save-guard input heal (anti-rollback deadlock breaker) ──────────────

    /// <summary>
    /// Monotonic, server-owned progress counters that an anti-rollback guard may
    /// legitimately protect (they only ever increase). ONLY these fields are
    /// eligible for the save-time input heal, so spendable balances (e.g.
    /// <c>totalGold</c>) and timestamps (e.g. <c>savedAt</c> / STALE_SAVE guards)
    /// are never clamped upward — preventing the heal from defeating a legitimate
    /// "your save is stale/old" rejection or re-crediting spent currency.
    /// </summary>
    private static readonly HashSet<string> s_monotonicGuardFields =
        new(StringComparer.Ordinal) { "totalLevel", "totalKills", "nodesMined" };

    /// <summary>A single anti-rollback heal applied to the input before execution.</summary>
    private readonly record struct RegressionHeal(string Field, double From, double To);

    /// <summary>
    /// Detect anti-rollback guards in the endpoint definition and raise matching
    /// monotonic input fields up to the stored value so a player whose denormalized
    /// cache drifted above the client's recompute is never permanently blocked. Only
    /// the <c>input.X &gt;= &lt;readStep&gt;.X</c> shape (same field both sides) for a
    /// field in <see cref="s_monotonicGuardFields"/> is treated as a guard; the heal
    /// only raises, never lowers, and never touches any other field. Returns the list
    /// of heals applied (empty when none) and mutates <paramref name="healedInput"/>.
    /// </summary>
    private static List<RegressionHeal> TryHealRegressionGuards(
        Dictionary<string, object?> endpointDef,
        Dictionary<string, object?> healedInput,
        Dictionary<string, object?> discoveryContext,
        Func<string, string, object?> readRecord)
    {
        var heals = new List<RegressionHeal>();
        if (endpointDef.GetValueOrDefault("steps") is not List<object?> steps) return heals;

        // 1. Resolve each read step's record (the same collection/key the guard reads).
        var readRecords = new Dictionary<string, Dictionary<string, object?>?>(StringComparer.Ordinal);
        CollectReadStepRecords(steps, discoveryContext, readRecord, readRecords, 0);

        // 2. Walk asserts; heal each anti-rollback guard that would otherwise block.
        HealAssertSteps(steps, healedInput, readRecords, heals, 0);
        return heals;
    }

    private static void CollectReadStepRecords(
        List<object?> steps,
        Dictionary<string, object?> discoveryContext,
        Func<string, string, object?> readRecord,
        Dictionary<string, Dictionary<string, object?>?> readRecords,
        int depth)
    {
        if (depth > 8) return;
        foreach (var raw in steps)
        {
            if (raw is not Dictionary<string, object?> step) continue;
            var type = step.GetValueOrDefault("type") as string;
            if (type == "read")
            {
                var id = step.GetValueOrDefault("id") as string;
                var collection = step.GetValueOrDefault("collection") as string;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(collection)) continue;
                if (readRecords.ContainsKey(id)) continue;
                string key;
                try
                {
                    key = EndpointExpression.JsToString(
                        EndpointExpression.ResolveTemplate(step.GetValueOrDefault("key"), discoveryContext));
                }
                catch { continue; }
                Dictionary<string, object?>? record;
                try { record = readRecord(collection, key) as Dictionary<string, object?>; }
                catch { record = null; }
                readRecords[id] = record;
            }
            else if (type == "block" && step.GetValueOrDefault("steps") is List<object?> children)
            {
                CollectReadStepRecords(children, discoveryContext, readRecord, readRecords, depth + 1);
            }
        }
    }

    private static void HealAssertSteps(
        List<object?> steps,
        Dictionary<string, object?> healedInput,
        Dictionary<string, Dictionary<string, object?>?> readRecords,
        List<RegressionHeal> heals,
        int depth)
    {
        if (depth > 8) return;
        foreach (var raw in steps)
        {
            if (raw is not Dictionary<string, object?> step) continue;
            var type = step.GetValueOrDefault("type") as string;
            if (type == "assert")
                TryHealOneGuard(step, healedInput, readRecords, heals);
            else if (type == "block" && step.GetValueOrDefault("steps") is List<object?> children)
                HealAssertSteps(children, healedInput, readRecords, heals, depth + 1);
        }
    }

    private static void TryHealOneGuard(
        Dictionary<string, object?> assertStep,
        Dictionary<string, object?> healedInput,
        Dictionary<string, Dictionary<string, object?>?> readRecords,
        List<RegressionHeal> heals)
    {
        if (assertStep.GetValueOrDefault("check") is not Dictionary<string, object?> check) return;
        if (!TryParseAntiRollbackGuard(check, out var inputField, out var srcStep, out var srcField)) return;

        // Anti-rollback signature: the SAME monotonic field on both sides.
        if (!string.Equals(inputField, srcField, StringComparison.Ordinal)) return;
        if (!s_monotonicGuardFields.Contains(inputField)) return;

        if (!readRecords.TryGetValue(srcStep, out var srcRecord) || srcRecord is null) return;
        if (!srcRecord.TryGetValue(srcField, out var storedRaw)) return;
        var storedVal = EndpointExpression.JsNumber(storedRaw);
        if (!double.IsFinite(storedVal)) return;

        // Heal only when the input field is present + numeric + strictly below the
        // stored value. Absent/non-numeric input fails safe to the existing guard.
        if (!healedInput.TryGetValue(inputField, out var inputRaw) || inputRaw is null) return;
        var inputVal = EndpointExpression.JsNumber(inputRaw);
        if (!double.IsFinite(inputVal)) return;
        if (inputVal >= storedVal) return;

        // Raise to the stored high-water mark, preserving its representation so the
        // downstream write persists exactly the stored value (no precision change).
        healedInput[inputField] = storedRaw;
        heals.Add(new RegressionHeal(inputField, inputVal, storedVal));
    }

    /// <summary>
    /// Recognize an anti-rollback guard of the form <c>input.X &gt;= step.Y</c>,
    /// supporting both the <c>expression</c> form
    /// (<c>{{num(input.X,0)}} &gt;= {{num(step.Y,0)}}</c>) and the
    /// <c>field</c>/<c>op</c>/<c>value</c> form. Outputs the input field, the source
    /// read-step id, and the source field. Anything that is not an unambiguous,
    /// single <c>&gt;=</c> comparison of one <c>input.*</c> term against one
    /// <c>step.*</c> term returns false (no heal).
    /// </summary>
    private static bool TryParseAntiRollbackGuard(
        Dictionary<string, object?> check,
        out string inputField, out string srcStep, out string srcField)
    {
        inputField = srcStep = srcField = "";

        // Compound conditions are never a simple anti-rollback guard.
        if (check.ContainsKey("all") || check.ContainsKey("any")) return false;

        var expression = check.GetValueOrDefault("expression") as string;
        var hasField = (check.GetValueOrDefault("field") ?? check.GetValueOrDefault("left")) is string fc
            && fc.Length > 0;

        string? lhs;
        string? rhs;
        if (!string.IsNullOrWhiteSpace(expression) && !hasField)
        {
            // Split on the FIRST top-level ">=" — it never appears inside {{num(...)}}.
            var idx = expression.IndexOf(">=", StringComparison.Ordinal);
            if (idx < 0) return false;
            // A second ">=" means a compound expression — not a simple guard.
            if (expression.IndexOf(">=", idx + 2, StringComparison.Ordinal) >= 0) return false;
            lhs = expression[..idx];
            rhs = expression[(idx + 2)..];
        }
        else if (hasField)
        {
            var op = check.GetValueOrDefault("op") as string;
            if (!IsGreaterOrEqualOp(op)) return false;
            lhs = (check.GetValueOrDefault("field") ?? check.GetValueOrDefault("left")) as string;
            rhs = (check.GetValueOrDefault("value") ?? check.GetValueOrDefault("right")) as string;
        }
        else return false;

        if (lhs is null || rhs is null) return false;

        // Exactly one input.X on the LHS, none on the RHS (unambiguous direction).
        var inputMatches = s_inputRefRegex.Matches(lhs);
        if (inputMatches.Count != 1) return false;
        if (s_inputRefRegex.IsMatch(rhs)) return false;
        inputField = inputMatches[0].Groups[1].Value;

        // Exactly one step.field on the RHS (the stored value). Function names like
        // `num(` are followed by `(`, never `.`, so they never match.
        var srcMatches = s_stepRefRegex.Matches(rhs);
        if (srcMatches.Count != 1) return false;
        srcStep = srcMatches[0].Groups[1].Value;
        srcField = srcMatches[0].Groups[2].Value;
        return true;
    }

    private static bool IsGreaterOrEqualOp(string? op) =>
        op is ">=" or "gte" or "greaterThanOrEqual" or "\u2265";

    // input.<field> reference (the client's new value for the field).
    private static readonly Regex s_inputRefRegex =
        new(@"\binput\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    // <step>.<field> reference (a read-step record's stored value).
    private static readonly Regex s_stepRefRegex =
        new(@"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
}
