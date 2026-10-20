using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.NetworkStorage.Secrets;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Scripted endpoints (<c>runtime: javascript | typescript</c>).
///
/// The handler runs synchronously inside a Jint engine, so every store read it makes must already be in memory.
/// A round runs the handler against the store state loaded so far. A read that is not loaded yet is recorded as a
/// miss and returns null. When a round has misses, they are loaded asynchronously and the handler re-runs from
/// scratch. Only a round with no misses commits, so a discarded round has no observable effect.
/// </summary>
public sealed partial class EndpointExecutor
{
    /// <summary>Handler runs allowed for one request (each run after the first loads the previous run's misses).</summary>
    public const int MaxScriptRounds = 8;

    /// <summary>Store records, scans, workflow definitions and global modules one request may load across all rounds.</summary>
    public const int MaxScriptStateLoads = 64;

    /// <summary>
    /// Locked passes one committing request may take. A pass that reaches a record outside the locked set widens the
    /// set and runs again. Past this many, the call answers 409 <c>CONFLICT</c> rather than hold locks longer.
    /// </summary>
    private const int MaxScriptLockAttempts = 3;

    /// <summary>Record writes one handler run may stage. Only the run that completes without misses commits.</summary>
    public const int MaxStagedWrites = 1000;

    /// <summary>Serialized bytes of record data one handler run may stage.</summary>
    public const long MaxStagedBytes = 1024 * 1024;

    /// <summary>
    /// The script runtimes this server offers. <paramref name="scriptsEnabled"/> is the <c>endpoints.scripts</c> kill
    /// switch: when false, a scripted endpoint fails with <c>SCRIPTS_DISABLED</c> instead of running.
    /// </summary>
    public static IReadOnlyList<IEndpointScriptRuntime> CreateScriptRuntimes(bool scriptsEnabled, ILogger<JavaScriptEndpointRuntime>? logger = null)
    {
        var javaScript = new JavaScriptEndpointRuntime(scriptsEnabled, EndpointScriptLimits.Defaults, logger);
        return [javaScript, new TypeScriptEndpointRuntime(javaScript)];
    }

    /// <summary>Everything the scripted branch of <see cref="TryExecuteAsync"/> resolved before running the handler.</summary>
    private sealed record ScriptInvocation(
        string ProjectId,
        string EndpointSlug,
        Dictionary<string, object?> EndpointDef,
        string RuntimeName,
        IReadOnlyDictionary<string, object?> Input,
        string SteamId,
        string UserId,
        string PlayerKey,
        IReadOnlyDictionary<string, object?> GameValues,
        bool HasSecretKey,
        bool IsDedicatedServer,
        DateTimeOffset ExecTime,
        bool LiveServe,
        bool LegacyPlayerProjections,
        Dictionary<string, string> CollectionNameToId,
        HashSet<string>? VersionCheckedCollections,
        Func<string, string> ResolveCollection,
        EndpointDryRunTrace? Trace,
        string Method,
        string? Revision);

    /// <summary>Store state loaded so far, shared by every round of one request.</summary>
    private sealed class ScriptState(EndpointReadSet? readSet, ScriptFetchSession fetch)
    {
        /// <summary>Protected-collection snapshots. Null when no read needs a version check.</summary>
        public EndpointReadSet? ReadSet { get; } = readSet;

        /// <summary>The outbound HTTP calls this request has made or queued. Shared by every round.</summary>
        public ScriptFetchSession Fetch { get; } = fetch;

        /// <summary>Loaded records keyed <c>collectionId:key</c>. A missing record is stored as null.</summary>
        public Dictionary<string, object?> Records { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded collection scans keyed by collection id.</summary>
        public Dictionary<string, List<object?>> Scans { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded workflow definitions keyed by workflow id. A missing workflow is stored as null.</summary>
        public Dictionary<string, Dictionary<string, object?>?> Workflows { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded global module sources (JavaScript) keyed by endpoint id. A missing or disabled global is stored as null.</summary>
        public Dictionary<string, string?> Globals { get; } = new(StringComparer.Ordinal);

        /// <summary>Project secrets loaded so far, keyed by name. A missing secret is stored as null.</summary>
        public Dictionary<string, string?> Secrets { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> WorkflowIds { get; set; } = [];

        public IReadOnlyList<string> GlobalIds { get; set; } = [];

        public int Loads { get; set; }
    }

    /// <summary>One handler run. Writes stay here until the run completes without misses.</summary>
    private sealed class ScriptRound
    {
        /// <summary>This run's writes keyed <c>collectionId:key</c>. A delete is stored as null.</summary>
        public Dictionary<string, object?> Writes { get; } = new(StringComparer.Ordinal);

        public List<(string Collection, string Key, Dictionary<string, object?>? Data, bool IsDelete)> DurableWrites { get; } = [];

        public List<EndpointTraceWrite> TraceWrites { get; } = [];

        /// <summary>Serialized bytes of record data this run staged, checked against <see cref="MaxStagedBytes"/>.</summary>
        public long StagedBytes { get; set; }

        public List<EndpointStepTrace> TraceSteps { get; } = [];

        public Dictionary<string, (string Collection, string Key)> MissedRecords { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedScans { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedWorkflows { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedGlobals { get; } = new(StringComparer.Ordinal);

        /// <summary>Secret names a run read that are not loaded yet. The run reruns once they are.</summary>
        public HashSet<string> MissedSecrets { get; } = new(StringComparer.Ordinal);

        /// <summary>Fetch calls this round queued that still need a network result. They are loaded, then the round re-runs.</summary>
        public int MissedFetches { get; set; }

        public int Misses => MissedRecords.Count + MissedScans.Count + MissedWorkflows.Count + MissedGlobals.Count + MissedSecrets.Count + MissedFetches;
    }

    private sealed record ScriptRun(EndpointScriptHost Host, EndpointScriptOutcome Outcome);

    /// <summary>One pass over the handler: its last run and round, the state it read, and any denial that ended it early.</summary>
    private sealed record ScriptAttempt(ScriptState State, ScriptRound Round, ScriptRun Run, EndpointExecutionResult? Denial);

    /// <summary>Runs a scripted endpoint. The caller has already applied the public gates and resolved collections.</summary>
    private async Task<EndpointExecutionResult> ExecuteScriptEndpointAsync(ScriptInvocation inv, CancellationToken ct)
    {
        var runtime = _scriptRuntimes.FirstOrDefault(candidate => candidate.Language == inv.RuntimeName);
        if (runtime is null)
            return ScriptFailure(501, "RUNTIME_UNSUPPORTED", $"The \"{inv.RuntimeName}\" runtime is not available on this server.", null);
        if (!runtime.Enabled)
            return Denied(503, "SCRIPTS_DISABLED", "Scripted endpoints are turned off on this server.");

        var source = ScriptStringField(inv.EndpointDef, "script");
        if (source is null)
            return ScriptFailure(500, "SCRIPT_ERROR", "The endpoint has no script.", null);
        if (Encoding.UTF8.GetByteCount(source) > runtime.Limits.MaxSourceBytes)
            return ScriptFailure(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint script is larger than this server allows.", null);

        if (inv.LiveServe && inv.VersionCheckedCollections is not null && _dataSource is not IEndpointSnapshotDataSource)
            return Denied(500, "STORAGE_WRITE_FAILED", "This provider cannot protect endpoint record versions.");

        // Admission comes before any store read or script work. A busy project or server gets a bounded wait; past it
        // the request is refused, never run, and answered 429 with a Retry-After hint.
        using var slot = await _scriptGate.EnterAsync(inv.ProjectId, ct);
        if (slot is null)
            return ScriptBusy();

        // The allow-list is read once per request, after admission. Every pass of this request shares one fetch
        // session, so a call that an earlier pass sent is answered from its stored result, never sent again.
        var fetch = new ScriptFetchSession(await _dataSource.GetEndpointFetchPolicyAsync(inv.ProjectId, ct), _fetchTransport);

        // The first pass runs without record locks. A preview, a failed run, or a run that writes nothing answers from
        // it; only a live run that stages writes goes on to commit under the locks.
        var first = await RunScriptRoundsAsync(runtime, source, inv, fetch, ct);
        if (first.Denial is not null) return first.Denial;
        if (!inv.LiveServe || !CommitsWrites(first))
            return await FinishScriptRunAsync(inv, first.State, first.Round, first.Run, ct);
        return await CommitScriptUnderLocksAsync(runtime, source, inv, first, fetch, ct);
    }

    /// <summary>
    /// Runs the handler until a run finishes without misses, loading what each miss needed. Each pass gets its own
    /// budget. Its clock starts when user code runs, so time spent waiting for a record lock is never charged to the
    /// handler.
    /// </summary>
    private async Task<ScriptAttempt> RunScriptRoundsAsync(
        IEndpointScriptRuntime runtime,
        string source,
        ScriptInvocation inv,
        ScriptFetchSession fetch,
        CancellationToken ct)
    {
        // One budget for the whole pass: every round shares its time, statement and workflow limits.
        using var budget = new ScriptRunBudget(runtime.Limits, ct);

        var protectedCollections = inv.LiveServe ? inv.VersionCheckedCollections : null;
        var state = new ScriptState(protectedCollections is null ? null : new EndpointReadSet(protectedCollections), fetch);
        // Catalogs are loaded whole, once per pass. A failed load propagates: a script must not run against a
        // silently shorter catalog.
        state.WorkflowIds = await _dataSource.ListWorkflowIdsAsync(inv.ProjectId, ct);
        state.GlobalIds = await _dataSource.ListEndpointIdsAsync(inv.ProjectId, ct);

        for (var round = 1; ; round++)
        {
            var attempt = new ScriptRound();
            var run = RunScriptRound(runtime, source, inv, state, attempt, budget, ct);
            if (attempt.Misses == 0)
                return new ScriptAttempt(state, attempt, run, null);
            if (round >= MaxScriptRounds)
                return new ScriptAttempt(state, attempt, run,
                    ScriptFailure(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint needs more reads than one request may make.", null));

            var loadDenial = await LoadScriptMissesAsync(inv, state, attempt, budget, ct);
            if (loadDenial is not null) return new ScriptAttempt(state, attempt, run, loadDenial);
        }
    }

    /// <summary>A committing run is one that reached no failure and staged record writes.</summary>
    private static bool CommitsWrites(ScriptAttempt attempt) =>
        attempt.Run.Host.Failure is null
        && attempt.Run.Outcome.Kind is not (EndpointScriptOutcomeKind.LimitExceeded or EndpointScriptOutcomeKind.ScriptError)
        && attempt.Round.DurableWrites.Count > 0;

    /// <summary>
    /// Runs a committing handler again while holding the write queue's locks on the records it writes and on the
    /// versioned records it reads. The first pass only discovers those records: its snapshots can be stale once a lock
    /// is held, so the committed result always comes from a pass made under the locks. A pass that reaches a record
    /// outside the locked set widens the set and runs again. After <see cref="MaxScriptLockAttempts"/> passes the call
    /// answers 409 <c>CONFLICT</c>.
    /// </summary>
    private async Task<EndpointExecutionResult> CommitScriptUnderLocksAsync(
        IEndpointScriptRuntime runtime,
        string source,
        ScriptInvocation inv,
        ScriptAttempt discovered,
        ScriptFetchSession fetch,
        CancellationToken ct)
    {
        var locked = new HashSet<(string Collection, string Key)>();
        AddCommitRecords(locked, discovered, inv);
        for (var pass = 1; ; pass++)
        {
            EndpointWriteQueue.RecordLease? lease;
            try
            {
                lease = await _writeQueue.AcquireAsync(inv.ProjectId, locked, ct);
            }
            catch (EndpointWriteQueueException ex)
            {
                return WriteQueueDenied(ex);
            }
            // The locks cover this pass and the flush inside FinishScriptRunAsync, so the version check sees no interleaving.
            using var held = lease;

            var attempt = await RunScriptRoundsAsync(runtime, source, inv, fetch, ct);
            if (attempt.Denial is not null) return attempt.Denial;
            if (!CommitsWrites(attempt))
                return await FinishScriptRunAsync(inv, attempt.State, attempt.Round, attempt.Run, ct);

            var needed = new HashSet<(string Collection, string Key)>();
            AddCommitRecords(needed, attempt, inv);
            if (needed.IsSubsetOf(locked))
                return await FinishScriptRunAsync(inv, attempt.State, attempt.Round, attempt.Run, ct);
            if (pass >= MaxScriptLockAttempts)
                return Denied(409, "CONFLICT", "Endpoint state changed during execution. Reload before retrying.");
            locked.UnionWith(needed);
        }
    }

    /// <summary>The records a committing pass locks: the records it writes and the versioned records it read.</summary>
    private static void AddCommitRecords(
        HashSet<(string Collection, string Key)> records, ScriptAttempt attempt, ScriptInvocation inv)
    {
        foreach (var write in attempt.Round.DurableWrites)
            records.Add((write.Collection, write.Key));
        if (attempt.State.ReadSet is null) return;
        foreach (var (collection, key) in attempt.State.ReadSet.Records.Keys)
        {
            if (inv.VersionCheckedCollections?.Contains(collection) == true)
                records.Add((collection, key));
        }
    }

    /// <summary>Runs the handler once against the state loaded so far. Every store access is synchronous.</summary>
    private ScriptRun RunScriptRound(
        IEndpointScriptRuntime runtime,
        string source,
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        ScriptRunBudget budget,
        CancellationToken ct)
    {
        state.Fetch.BeginRound();
        string Cid(string collection) => inv.ResolveCollection(collection);

        object? ReadRecord(string collection, string key)
        {
            var cid = Cid(collection);
            var rk = ResolveRecordKey(key);
            var cacheKey = RecordCacheKey(cid, rk);
            if (round.Writes.TryGetValue(cacheKey, out var written)) return written;
            if (state.Records.TryGetValue(cacheKey, out var stored)) return stored;
            round.MissedRecords.TryAdd(cacheKey, (cid, rk));
            return null;
        }

        void Stage(string collection, string key, Dictionary<string, object?>? data, bool isDelete)
        {
            var cid = Cid(collection);
            var rk = ResolveRecordKey(key);
            if (round.DurableWrites.Count >= MaxStagedWrites)
                throw new ScriptLimitExceededException($"The endpoint wrote more than {MaxStagedWrites} records in one run.");
            if (data is not null)
            {
                round.StagedBytes += JsonSerializer.SerializeToUtf8Bytes(data).LongLength;
                if (round.StagedBytes > MaxStagedBytes)
                    throw new ScriptLimitExceededException($"The endpoint staged more than {MaxStagedBytes} bytes of record data in one run.");
            }
            round.Writes[RecordCacheKey(cid, rk)] = data;
            round.DurableWrites.Add((cid, rk, data, isDelete));
            round.TraceWrites.Add(new EndpointTraceWrite(collection, key, data, isDelete));
        }

        List<object?> ScanCollection(string collection)
        {
            var cid = Cid(collection);
            if (state.Scans.TryGetValue(cid, out var rows)) return new List<object?>(rows);
            round.MissedScans.Add(cid);
            return [];
        }

        Dictionary<string, object?>? LoadWorkflow(string id)
        {
            if (state.Workflows.TryGetValue(id, out var definition)) return definition;
            round.MissedWorkflows.Add(id);
            return null;
        }

        string? GlobalSource(string id)
        {
            if (state.Globals.TryGetValue(id, out var source)) return source;
            round.MissedGlobals.Add(id);
            return null;
        }

        string? SecretGet(string name)
        {
            if (state.Secrets.TryGetValue(name, out var value)) return value;
            round.MissedSecrets.Add(name);
            return null;
        }

        var request = new EndpointExecutionRequest(
            Input: inv.Input,
            SteamId: inv.SteamId,
            PlayerKey: inv.PlayerKey,
            GameValues: inv.GameValues,
            ProjectId: inv.ProjectId,
            UserId: inv.UserId,
            HasSecretKey: inv.HasSecretKey,
            IsDedicatedServer: inv.IsDedicatedServer,
            ExecTime: inv.ExecTime,
            ReadRecord: ReadRecord,
            WriteRecord: (collection, key, data) => Stage(collection, key, data, isDelete: false),
            DeleteRecord: (collection, key) => Stage(collection, key, null, isDelete: true),
            ScanCollection: ScanCollection,
            LoadWorkflow: (id, _) => Task.FromResult(LoadWorkflow(id)),
            // Workflow webhook steps are refused for scripts (see EndpointStepExecutor), so nothing is sent or queued.
            SkipWebhooks: true,
            SkipSleep: true,
            OnStep: round.TraceSteps.Add,
            ScriptBudget: budget.Workflows);

        object? RunWorkflow(string id, Dictionary<string, object?> input)
        {
            if (!state.Workflows.TryGetValue(id, out var definition))
            {
                round.MissedWorkflows.Add(id);
                return null;
            }
            if (definition is null)
                throw new EndpointScriptFailureException(500, "WORKFLOW_NOT_FOUND", $"Workflow \"{id}\" was not found.");
            return _executor.RunWorkflowForScript(
                id, definition, input, EndpointStepExecutor.CreateScriptContext(request), request, ct);
        }

        var bindings = new EndpointScriptBindings(
            ReadRecord: ReadRecord,
            WriteRecord: (collection, key, data) => Stage(collection, key, data, isDelete: false),
            DeleteRecord: (collection, key) => Stage(collection, key, null, isDelete: true),
            ScanCollection: ScanCollection,
            GameValues: inv.GameValues,
            Now: inv.ExecTime,
            RunWorkflow: RunWorkflow,
            WorkflowIds: () => state.WorkflowIds,
            EndpointIds: () => state.GlobalIds,
            GlobalSource: GlobalSource,
            Secret: SecretGet,
            Fetch: state.Fetch);

        var host = new EndpointScriptHost(bindings, runtime.Limits.MaxLogBytes, _logger);
        var scriptRequest = new EndpointScriptRequest(
            inv.Input,
            inv.SteamId == "anonymous" || inv.HasSecretKey ? null : inv.SteamId,
            inv.Method,
            inv.EndpointSlug,
            inv.Revision);
        var outcome = runtime.Execute(source, scriptRequest, host, budget);
        round.MissedFetches = state.Fetch.PendingCount;
        return new ScriptRun(host, outcome);
    }

    /// <summary>Loads the records, scans, workflows and globals one run missed. Returns a denial when a load fails closed.</summary>
    private async Task<EndpointExecutionResult?> LoadScriptMissesAsync(
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        ScriptRunBudget budget,
        CancellationToken ct)
    {
        var projectId = inv.ProjectId;
        var checkedSource = _dataSource as IEndpointSnapshotDataSource;
        var secretSource = _dataSource as IEndpointSecretSource;
        // A fetch built with provisional nulls is not a real request. Resolve state, discard unsent calls,
        // then let the next round build its request from the resolved values.
        var unresolvedState = round.MissedRecords.Count > 0 || round.MissedScans.Count > 0
            || round.MissedWorkflows.Count > 0 || round.MissedGlobals.Count > 0 || round.MissedSecrets.Count > 0;
        if (unresolvedState) state.Fetch.DiscardPending();

        foreach (var (cacheKey, (cid, rk)) in round.MissedRecords)
        {
            if (++state.Loads > MaxScriptStateLoads) return ScriptLoadLimit();
            try
            {
                if (state.ReadSet is not null)
                {
                    var snapshot = await checkedSource!.ReadRecordSnapshotAsync(projectId, cid, rk, ct);
                    state.ReadSet.Records[(cid, rk)] = snapshot;
                    state.Records[cacheKey] = snapshot.Value;
                }
                else
                    state.Records[cacheKey] = await _dataSource.ReadRecordAsync(projectId, cid, rk, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException) when (state.ReadSet is not null)
            {
                state.ReadSet.Records[(cid, rk)] = new EndpointRecordSnapshot(null, null, InvalidTarget: true);
                state.Records[cacheKey] = null;
            }
            catch
            {
                if (state.ReadSet is not null)
                    return Denied(500, "STORAGE_WRITE_FAILED", "Protected endpoint state could not be read.");
                state.Records[cacheKey] = null;
            }
        }

        foreach (var cid in round.MissedScans)
        {
            if (++state.Loads > MaxScriptStateLoads) return ScriptLoadLimit();
            if (await ScanExceedsCapAsync(projectId, cid, ct))
                return Denied(400, "SCAN_TOO_LARGE", $"Collection scan exceeded the {MaxScanRows}-row limit.");
            List<object?> rows;
            if (state.ReadSet is not null)
            {
                var snapshots = await checkedSource!.ScanCollectionSnapshotsAsync(projectId, cid, ct);
                state.ReadSet.Scans[cid] = snapshots;
                rows = snapshots.Where(read => read.Snapshot.Value is not null).Select(read => read.Snapshot.Value).ToList();
            }
            else
                rows = (await _dataSource.ScanCollectionAsync(projectId, cid, ct)).ToList();
            if (rows.Count > MaxScanRows)
                return Denied(400, "SCAN_TOO_LARGE", $"Collection scan exceeded the {MaxScanRows}-row limit.");
            state.Scans[cid] = rows;
        }

        foreach (var id in round.MissedWorkflows)
        {
            if (++state.Loads > MaxScriptStateLoads) return ScriptLoadLimit();
            state.Workflows[id] = await ReadWorkflowDefinitionOrNullAsync(projectId, id, ct);
        }

        foreach (var id in round.MissedGlobals)
        {
            if (++state.Loads > MaxScriptStateLoads) return ScriptLoadLimit();
            state.Globals[id] = await ReadGlobalModuleSourceAsync(projectId, id, ct);
        }

        foreach (var name in round.MissedSecrets)
        {
            if (++state.Loads > MaxScriptStateLoads) return ScriptLoadLimit();
            if (secretSource is null)
                return ScriptFailure(500, "SECRET_UNAVAILABLE", "A project secret is unavailable on this server.", "The data source cannot read project secrets.");
            try
            {
                state.Secrets[name] = await secretSource.ReadProjectSecretAsync(projectId, name, ct);
            }
            catch (ProjectSecretUnavailableException error)
            {
                return ScriptFailure(500, "SECRET_UNAVAILABLE", "A project secret is unavailable on this server.", error.Message);
            }
        }

        if (!unresolvedState && state.Fetch.PendingCount > 0)
        {
            budget.Suspend();
            try
            {
                await state.Fetch.LoadPendingAsync(ct);
            }
            finally
            {
                budget.Resume();
            }
        }

        return null;
    }

    /// <summary>
    /// True when the source can count the collection and it holds more rows than one scan may return. Counting
    /// first refuses an over-cap collection before any of its rows are materialised.
    /// </summary>
    private async Task<bool> ScanExceedsCapAsync(string projectId, string collectionId, CancellationToken ct)
        => _dataSource is IEndpointScanCounter counter
           && await counter.CountCollectionAsync(projectId, collectionId, ct) > MaxScanRows;

    private async Task<Dictionary<string, object?>?> ReadWorkflowDefinitionOrNullAsync(string projectId, string id, CancellationToken ct)
    {
        try
        {
            return await _dataSource.ReadWorkflowDefinitionAsync(projectId, id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The JavaScript source of an enabled <c>scope: global</c> endpoint, or null when no such global exists. The
    /// source is what importers evaluate, so a TypeScript global is stripped here.
    /// </summary>
    private async Task<string?> ReadGlobalModuleSourceAsync(string projectId, string id, CancellationToken ct)
    {
        Dictionary<string, object?>? definition;
        try
        {
            definition = await _dataSource.ReadEndpointDefinitionAsync(projectId, id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        if (definition is null
            || ScriptStringField(definition, "scope") != "global"
            || IsFalse(definition.GetValueOrDefault("enabled"))
            || ScriptStringField(definition, "script") is not { } source
            || FindScriptRuntime(definition) is not { } runtime)
            return null;
        return runtime.ToJavaScript(source);
    }

    /// <summary>Applies the secret policy to every terminal output, including errors and blocked-fetch diagnostics.</summary>
    private async Task<EndpointExecutionResult> FinishScriptRunAsync(
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        ScriptRun run,
        CancellationToken ct)
    {
        var redact = new SecretRedactor(state.Secrets);
        try
        {
            var result = await FinishScriptRunCoreAsync(inv, state, round, run, ct);
            return result with
            {
                FetchBlocked = state.Fetch.Blocked.Select(call =>
                    call with { Host = redact.Text(call.Host), Reason = redact.Text(call.Reason) }).ToList(),
            };
        }
        catch (SecretOutputCollisionException)
        {
            inv.Trace?.Steps.Clear();
            inv.Trace?.PendingWrites.Clear();
            inv.Trace?.ScriptLogs.Clear();
            return ScriptFailure(500, "SECRET_OUTPUT_REJECTED", "Secret redaction would make the output ambiguous.", null);
        }
    }

    /// <summary>One trace step per fetch call, for the try-it trace and the owner's per-call view.</summary>
    private static IEnumerable<EndpointStepTrace> FetchTraceSteps(ScriptFetchSession fetch, SecretRedactor redact) =>
        fetch.Trace.Select(entry => new EndpointStepTrace(
            $"fetch#{entry.Ordinal}",
            "fetch",
            new { host = redact.Text(entry.Host), method = redact.Text(entry.Method), status = entry.Status, error = entry.ErrorCode, reason = redact.Text(entry.Reason) },
            Passed: entry.ErrorCode is null));

    /// <summary>Maps a finished run to the response, committing its writes when it succeeded.</summary>
    private async Task<EndpointExecutionResult> FinishScriptRunCoreAsync(
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        ScriptRun run,
        CancellationToken ct)
    {
        var redact = new SecretRedactor(state.Secrets);
        var logs = redact.Logs(run.Host.LogValues);
        // Record data can later reach clients without loading the secret, and projections can persist it in
        // telemetry. Reject it rather than merely hiding the preview of a write that would leak later.
        if (round.DurableWrites.Any(write => redact.Contains(write.Collection) || redact.Contains(write.Key) || redact.Contains(write.Data)))
            return ScriptFailure(400, "SECRET_WRITE_FORBIDDEN", "Project secrets cannot be stored in records.", null);
        if (inv.Trace is not null)
        {
            inv.Trace.Steps.AddRange(round.TraceSteps.Select(redact.Step));
            inv.Trace.Steps.AddRange(FetchTraceSteps(state.Fetch, redact));
            inv.Trace.PendingWrites.AddRange(round.TraceWrites.Select(redact.Write));
            inv.Trace.ScriptLogs.AddRange(logs);
        }

        if (run.Host.Failure is { } failure)
            return ScriptFailure(failure.Status, redact.Contains(failure.Code) ? "SECRET_ERROR_REDACTED" : failure.Code,
                redact.Text(failure.Message), ScriptOwnerDetail(redact.Text(failure.OwnerDetail), logs));

        var outcome = run.Outcome;
        switch (outcome.Kind)
        {
            case EndpointScriptOutcomeKind.LimitExceeded:
                return ScriptFailure(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint exceeded a limit.", ScriptOwnerDetail(redact.Text(outcome.OwnerDetail), logs));
            case EndpointScriptOutcomeKind.ScriptError:
                if (outcome.ErrorCode is { } fetchCode)
                    return ScriptFailure(502, fetchCode, "An outbound request failed.", ScriptOwnerDetail(redact.Text(outcome.OwnerDetail), logs));
                return ScriptFailure(500, "SCRIPT_ERROR", "The endpoint script failed.", ScriptOwnerDetail(redact.Text(outcome.OwnerDetail), logs));
        }
        var responseBody = outcome.Body is { } body ? redact.Value(ScriptJson.ToPlain(body)) : null;

        if (inv.LiveServe && round.DurableWrites.Count > 0)
        {
            var denial = await FlushDurableWritesAsync(inv.ProjectId, round.DurableWrites, state.ReadSet, inv.VersionCheckedCollections, ct);
            if (denial is not null) return denial with { Body = redact.Value(denial.Body), OwnerDetail = redact.Text(denial.OwnerDetail) };
            // Best-effort, like the YAML path: a projection failure never fails the committed call.
            if (inv.LegacyPlayerProjections && inv.SteamId != "anonymous")
                await ApplyPostFlushProjectionsAsync(inv.ProjectId, round.DurableWrites, state.Records, inv.CollectionNameToId, inv.EndpointSlug, ct);
        }

        return new EndpointExecutionResult(true, 200, responseBody);
    }

    private static EndpointExecutionResult ScriptLoadLimit() =>
        new(false, 500, Denied(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint read more data than one request may load.").Body);

    private static EndpointExecutionResult ScriptFailure(int status, string code, string message, string? ownerDetail) =>
        new(false, status, Denied(status, code, message).Body, ownerDetail);

    /// <summary>The gate stayed full past its wait. The request never ran, so the caller may resend it unchanged.</summary>
    private EndpointExecutionResult ScriptBusy() =>
        new(false, 429, new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = "SCRIPT_BUSY",
                ["message"] = "Too many scripted requests are running on this server. Try again shortly.",
            },
            ["retryAfterSeconds"] = _scriptGate.RetryAfterSeconds,
            ["retrySafe"] = true,
        }, null, _scriptGate.RetryAfterSeconds);

    private static string? ScriptOwnerDetail(string? detail, IReadOnlyList<string> logs)
    {
        if (logs.Count == 0) return detail;
        var logText = "Log:\n" + string.Join('\n', logs);
        return string.IsNullOrEmpty(detail) ? logText : detail + "\n" + logText;
    }

    private static string RecordCacheKey(string collectionId, string key) => $"{collectionId}:{key}";

    private static string? ScriptStringField(Dictionary<string, object?> definition, string name) =>
        definition.GetValueOrDefault(name) switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

    /// <summary>The runtime an endpoint definition names, or null for a YAML endpoint.</summary>
    private IEndpointScriptRuntime? FindScriptRuntime(Dictionary<string, object?> definition) =>
        ScriptStringField(definition, "runtime") is { } name
            ? _scriptRuntimes.FirstOrDefault(candidate => candidate.Language == name)
            : null;

    /// <summary>
    /// Durably writes a successful run's writes and deletes in one store transaction. Returns a denial or failure
    /// when the commit does not happen, or null when every write committed.
    /// </summary>
    private async Task<EndpointExecutionResult?> FlushDurableWritesAsync(
        string projectId,
        IReadOnlyList<(string Collection, string Key, Dictionary<string, object?>? Data, bool IsDelete)> durableWrites,
        EndpointReadSet? readSet,
        HashSet<string>? versionCheckedCollections,
        CancellationToken ct)
    {
        var checkedSource = _dataSource as IEndpointSnapshotDataSource;
        if (readSet is null && versionCheckedCollections is not null
            && durableWrites.Any(write => versionCheckedCollections.Contains(write.Collection)))
            return Denied(409, "CONFLICT", "A protected endpoint write has no observed record snapshot.");

        // 0. Per-record size gate: reject an over-limit persisted record as 413 BEFORE anything is written
        //    (zero mutation). Each record is measured individually against the store's actual limit.
        if (_dataSource is IEndpointRecordSizeLimit sizeLimit)
        {
            foreach (var (collection, key, data, isDelete) in durableWrites)
            {
                if (isDelete || data is null) continue;
                var recordBytes = Encoding.UTF8.GetByteCount(JsonSerializer.SerializeToElement(data).GetRawText());
                if (recordBytes > sizeLimit.MaxPayloadBytes)
                    return Denied(413, "PAYLOAD_TOO_LARGE",
                        $"Payload exceeds the {sizeLimit.MaxPayloadBytes}-byte limit (endpoint record {collection}/{key}).");
            }
        }

        try
        {
            await using var transaction = readSet is not null
                ? await checkedSource!.BeginCheckedWriteTransactionAsync(projectId, readSet, ct)
                : await _dataSource.BeginWriteTransactionAsync(ct);
            foreach (var (collection, key, data, isDelete) in durableWrites)
            {
                if (isDelete)
                    await transaction.DeleteRecordAsync(projectId, collection, key, ct);
                else
                    await transaction.WriteRecordAsync(projectId, collection, key, data!, ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch (EndpointWriteConflictException)
        {
            return Denied(409, "CONFLICT", "Endpoint state changed during execution. Reload before retrying.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Disposing the uncommitted transaction discarded every write. Fail-closed: surface a 500.
            return new EndpointExecutionResult(false, 500, new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = "STORAGE_WRITE_FAILED",
                    ["message"] = "Endpoint record write failed to persist; all changes were rolled back.",
                },
            });
        }
        return null;
    }
}
