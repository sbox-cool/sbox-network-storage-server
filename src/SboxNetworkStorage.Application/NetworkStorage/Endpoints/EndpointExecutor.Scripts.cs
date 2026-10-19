using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

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
    public const int MaxScriptStateLoads = 512;

    private static IReadOnlyList<IEndpointScriptRuntime> DefaultScriptRuntimes()
    {
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults);
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
    private sealed class ScriptState(EndpointReadSet? readSet)
    {
        /// <summary>Protected-collection snapshots. Null when no read needs a version check.</summary>
        public EndpointReadSet? ReadSet { get; } = readSet;

        /// <summary>Loaded records keyed <c>collectionId:key</c>. A missing record is stored as null.</summary>
        public Dictionary<string, object?> Records { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded collection scans keyed by collection id.</summary>
        public Dictionary<string, List<object?>> Scans { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded workflow definitions keyed by workflow id. A missing workflow is stored as null.</summary>
        public Dictionary<string, Dictionary<string, object?>?> Workflows { get; } = new(StringComparer.Ordinal);

        /// <summary>Loaded global module sources (JavaScript) keyed by endpoint id. A missing or disabled global is stored as null.</summary>
        public Dictionary<string, string?> Globals { get; } = new(StringComparer.Ordinal);

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

        public List<EndpointStepTrace> TraceSteps { get; } = [];

        public Dictionary<string, (string Collection, string Key)> MissedRecords { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedScans { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedWorkflows { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MissedGlobals { get; } = new(StringComparer.Ordinal);

        public int Misses => MissedRecords.Count + MissedScans.Count + MissedWorkflows.Count + MissedGlobals.Count;
    }

    private sealed record ScriptRun(EndpointScriptHost Host, EndpointScriptOutcome Outcome);

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

        var protectedCollections = inv.LiveServe ? inv.VersionCheckedCollections : null;
        if (protectedCollections is not null && _dataSource is not IEndpointSnapshotDataSource)
            return Denied(500, "STORAGE_WRITE_FAILED", "This provider cannot protect endpoint record versions.");

        var state = new ScriptState(protectedCollections is null ? null : new EndpointReadSet(protectedCollections));
        // Catalogs are loaded whole, once per request. A failed load propagates: a script must not run against a
        // silently shorter catalog.
        state.WorkflowIds = await _dataSource.ListWorkflowIdsAsync(inv.ProjectId, ct);
        state.GlobalIds = await _dataSource.ListGlobalEndpointIdsAsync(inv.ProjectId, ct);

        for (var round = 1; ; round++)
        {
            var attempt = new ScriptRound();
            var run = RunScriptRound(runtime, source, inv, state, attempt, ct);
            if (attempt.Misses == 0)
                return await FinishScriptRunAsync(inv, state, attempt, run, ct);
            if (round >= MaxScriptRounds)
                return ScriptFailure(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint needs more reads than one request may make.", null);

            var loadDenial = await LoadScriptMissesAsync(inv, state, attempt, ct);
            if (loadDenial is not null) return loadDenial;
        }
    }

    /// <summary>Runs the handler once against the state loaded so far. Every store access is synchronous.</summary>
    private ScriptRun RunScriptRound(
        IEndpointScriptRuntime runtime,
        string source,
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        CancellationToken ct)
    {
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
            ScriptBudget: new ScriptWorkflowBudget(runtime.Limits.Timeout));

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
            GlobalSource: GlobalSource);

        var host = new EndpointScriptHost(bindings, runtime.Limits.MaxLogBytes);
        var scriptRequest = new EndpointScriptRequest(
            inv.Input,
            inv.SteamId == "anonymous" ? null : inv.SteamId,
            inv.Method,
            inv.EndpointSlug,
            inv.Revision);
        return new ScriptRun(host, runtime.Execute(source, scriptRequest, host, ct));
    }

    /// <summary>Loads the records, scans, workflows and globals one run missed. Returns a denial when a load fails closed.</summary>
    private async Task<EndpointExecutionResult?> LoadScriptMissesAsync(
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        CancellationToken ct)
    {
        var projectId = inv.ProjectId;
        var checkedSource = _dataSource as IEndpointSnapshotDataSource;

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

        return null;
    }

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

    /// <summary>Maps a finished run to the response, committing its writes when it succeeded.</summary>
    private async Task<EndpointExecutionResult> FinishScriptRunAsync(
        ScriptInvocation inv,
        ScriptState state,
        ScriptRound round,
        ScriptRun run,
        CancellationToken ct)
    {
        if (inv.Trace is not null)
        {
            inv.Trace.Steps.AddRange(round.TraceSteps);
            inv.Trace.PendingWrites.AddRange(round.TraceWrites);
            inv.Trace.ScriptLogs.AddRange(run.Host.Logs);
        }

        var logs = run.Host.Logs;
        if (run.Host.Failure is { } failure)
            return ScriptFailure(failure.Status, failure.Code, failure.Message, ScriptOwnerDetail(failure.OwnerDetail, logs));

        var outcome = run.Outcome;
        switch (outcome.Kind)
        {
            case EndpointScriptOutcomeKind.LimitExceeded:
                return ScriptFailure(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint exceeded a limit.", ScriptOwnerDetail(outcome.OwnerDetail, logs));
            case EndpointScriptOutcomeKind.ScriptError:
                return ScriptFailure(500, "SCRIPT_ERROR", "The endpoint script failed.", ScriptOwnerDetail(outcome.OwnerDetail, logs));
        }

        if (inv.LiveServe && round.DurableWrites.Count > 0)
        {
            var denial = await FlushDurableWritesAsync(inv.ProjectId, round.DurableWrites, state.ReadSet, inv.VersionCheckedCollections, ct);
            if (denial is not null) return denial;
            // Best-effort, like the YAML path: a projection failure never fails the committed call.
            if (inv.LegacyPlayerProjections && inv.SteamId != "anonymous")
                await ApplyPostFlushProjectionsAsync(inv.ProjectId, round.DurableWrites, state.Records, inv.CollectionNameToId, inv.EndpointSlug, ct);
        }

        return new EndpointExecutionResult(true, 200, outcome.Body is { } body ? ScriptJson.ToPlain(body) : null);
    }

    private static EndpointExecutionResult ScriptLoadLimit() =>
        new(false, 500, Denied(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint read more data than one request may load.").Body);

    private static EndpointExecutionResult ScriptFailure(int status, string code, string message, string? ownerDetail) =>
        new(false, status, Denied(status, code, message).Body, ownerDetail);

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

    private static bool IsFalse(object? value) =>
        value is false || value is JsonElement { ValueKind: JsonValueKind.False };

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
