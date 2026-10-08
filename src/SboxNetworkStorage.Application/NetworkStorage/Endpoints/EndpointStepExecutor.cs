using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Native .NET executor for the deterministic, linear subset of Network Storage
/// endpoint definitions, built on the parity-verified <see cref="EndpointExpression"/>.
///
/// Supported step types: <c>read</c> (via an injected record resolver),
/// <c>condition</c>, <c>assert</c>, <c>transform</c>, <c>object</c>, <c>array</c>,
/// <c>merge</c>, <c>sort</c>, <c>switch</c>, <c>compute</c>, <c>block</c>, and the
/// <c>response</c> no-op. Anything outside this subset — routed/modern flows,
/// <c>write</c>/<c>delete</c>, <c>random</c>, <c>lookup</c>/<c>filter</c>/
/// <c>lookup_many</c>/<c>random_select</c>, <c>workflow</c>, <c>webhook</c>, or a
/// <c>condition</c> using skip/clamp/goto/run fail-routes — raises
/// <see cref="EndpointExecutionUnsupportedException"/> so the caller falls back to
/// the authoritative Bun runtime. This keeps the incremental port fail-safe: it
/// only ever serves endpoints it can reproduce exactly.
///
/// Output (<see cref="EndpointExecutionResult"/>) mirrors Bun's
/// <c>executeEndpoint</c> success/condition-reject/fail shapes.
/// </summary>
public sealed class EndpointStepExecutor
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "read", "condition", "assert", "transform", "object", "array",
        "merge", "sort", "switch", "compute", "block", "response",
        "write", "delete", "random",
        "lookup", "filter", "lookup_many", "random_select",
        "webhook", "sleep",
    };

    private static readonly HashSet<string> RouteContinueOrReject = new(StringComparer.Ordinal) { "continue", "reject", "skip" };

    public async Task<EndpointExecutionResult> ExecuteAsync(Dictionary<string, object?> endpointDef, EndpointExecutionRequest request, CancellationToken ct = default)
    {
        var steps = AsList(endpointDef.GetValueOrDefault("steps")) ?? new List<object?>();
        var context = BuildContext(endpointDef, request);
        var pendingWrites = new List<PendingWrite>();
        var stepIndex = BuildStepIndex(steps);
        int pc = 0;
        var visitCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        while (pc < steps.Count)
        {
            ct.ThrowIfCancellationRequested();
            if (steps[pc] is not Dictionary<string, object?> step)
                throw new EndpointExecutionUnsupportedException("malformed step");

            var stepKey = (step.GetValueOrDefault("as") ?? step.GetValueOrDefault("id")) as string ?? $"step_{pc}";
            var visitKey = $"{stepKey}";
            visitCounts[visitKey] = visitCounts.TryGetValue(visitKey, out var vc) ? vc + 1 : 1;
            if (visitCounts[visitKey] > MaxStepVisits)
                return Fail(400, $"Step \"{stepKey}\" exceeded visit limit ({MaxStepVisits}).", step);

            var terminal = await ExecuteStepAsync(step, context, request, pendingWrites, ct);

            // If the step returned a route outcome, apply it.
            if (terminal is RouteOutcome route)
            {
                if (route.Done)
                    return route.TerminalResult ?? BuildResponse(endpointDef, context);
                if (route.TargetStep != null && stepIndex.TryGetValue(route.TargetStep, out var gotoPc))
                    pc = gotoPc;
                else if (route.SkipCount > 0)
                    pc += route.SkipCount + 1;
                else if (route.NextPc >= 0)
                    pc = route.NextPc;
                else
                    pc++;
                continue;
            }

            // If the step returned a terminal result (reject/error), return it.
            if (terminal is EndpointExecutionResult result)
                return result;

            pc++;
        }

        // Execute all deferred writes (matching Bun: writes execute after all
        // steps, before response building).
        foreach (var pw in pendingWrites)
        {
            if (pw.IsDelete)
                request.DeleteRecord?.Invoke(pw.Collection, pw.Key);
            else
                request.WriteRecord?.Invoke(pw.Collection, pw.Key, pw.Data!);
        }

        return BuildResponse(endpointDef, context);
    }

    // Backwards-compatible sync wrapper. Routed/modern flows still throw
    // UnsupportedException so legacy callers fall back to Bun.
    public EndpointExecutionResult Execute(Dictionary<string, object?> endpointDef, EndpointExecutionRequest request)
    {
        var steps = AsList(endpointDef.GetValueOrDefault("steps")) ?? new List<object?>();
        if (RouteRequiresModernExecution(steps))
            throw new EndpointExecutionUnsupportedException("endpoint uses routed/modern flow execution");
        return ExecuteAsync(endpointDef, request).GetAwaiter().GetResult();
    }

    private const int MaxStepVisits = 100;
    private const int MaxWorkflowDepth = 5;

    /// <summary>Outcome of a route action applied after a condition or routed step.</summary>
    private sealed record RouteOutcome(bool Done, int NextPc, EndpointExecutionResult? TerminalResult = null, string? TargetStep = null, int SkipCount = 0);

    private static Dictionary<string, int> BuildStepIndex(IReadOnlyList<object?> steps)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is Dictionary<string, object?> step)
            {
                var key = (step.GetValueOrDefault("as") ?? step.GetValueOrDefault("id")) as string;
                if (key != null && !index.ContainsKey(key))
                    index[key] = i;
            }
        }
        return index;
    }

    private sealed record PendingWrite(string Collection, string Key, Dictionary<string, object?>? Data, bool IsDelete);

    // ── Context ──

    private static Dictionary<string, object?> BuildContext(Dictionary<string, object?> endpointDef, EndpointExecutionRequest request)
    {
        var t = request.ExecTime.ToUniversalTime();
        var iso = t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var context = new Dictionary<string, object?>
        {
            ["input"] = ToValueDict(request.Input),
            ["steamId"] = request.SteamId,
            ["playerKey"] = request.PlayerKey,
            ["_hasSecretKey"] = request.HasSecretKey,
            ["_isDedicatedServer"] = request.IsDedicatedServer || request.HasSecretKey,
            ["now"] = iso,
            ["_unixMs"] = (double)t.ToUnixTimeMilliseconds(),
            ["_unixS"] = (double)t.ToUnixTimeSeconds(),
            ["_dateUTC"] = iso[..10],
            ["_timeUTC"] = iso.Substring(11, 8),
            ["_datetimeUTC"] = iso[..19] + "Z",
            ["_year"] = (double)t.Year,
            ["_month"] = (double)t.Month,
            ["_day"] = (double)t.Day,
            ["_hour"] = (double)t.Hour,
            ["_minute"] = (double)t.Minute,
            ["_dayOfWeek"] = (double)(int)t.DayOfWeek,
            ["values"] = ToValueDict(request.GameValues),
            ["projectId"] = request.ProjectId,
            ["userId"] = request.UserId,
            ["_skipWebhooks"] = request.SkipWebhooks,
            ["_skipSleep"] = request.SkipSleep,
        };

        if (endpointDef.GetValueOrDefault("let") is Dictionary<string, object?> aliases && aliases.Count > 0)
            context["_aliases"] = aliases;

        return context;
    }

    // ── Step dispatch ── returns null (continue), EndpointExecutionResult (terminal), or RouteOutcome (route action).

    private async Task<object?> ExecuteStepAsync(Dictionary<string, object?> step, Dictionary<string, object?> context, EndpointExecutionRequest request, List<PendingWrite> pendingWrites, CancellationToken ct)
    {
        var type = step.GetValueOrDefault("type") as string ?? "";
        if (!SupportedTypes.Contains(type) && type != "workflow")
            throw new EndpointExecutionUnsupportedException($"step type '{type}'");

        var id = step.GetValueOrDefault("id") as string ?? "";
        var asKey = step.GetValueOrDefault("as") as string;

        // Per-step `when` gating (blocks gate inside the block handler).
        if (type != "block" && step.GetValueOrDefault("when") is Dictionary<string, object?> when)
        {
            if (!EndpointExpression.EvaluateCondition(when, context)) return null;
        }

        // Merge per-step `let` aliases.
        if (step.GetValueOrDefault("let") is Dictionary<string, object?> stepLet && stepLet.Count > 0)
        {
            if (context.GetValueOrDefault("_aliases") is not Dictionary<string, object?> existing)
                context["_aliases"] = existing = new Dictionary<string, object?>();
            foreach (var (k, v) in stepLet) existing[k] = v;
        }

        try
        {
            switch (type)
            {
                case "block":
                {
                    if (step.GetValueOrDefault("when") is Dictionary<string, object?> blockWhen
                        && !EndpointExpression.EvaluateCondition(blockWhen, context))
                        return null;
                    var children = AsList(step.GetValueOrDefault("steps")) ?? new List<object?>();
                    foreach (var rawChild in children)
                    {
                        if (rawChild is not Dictionary<string, object?> child)
                            throw new EndpointExecutionUnsupportedException("malformed block child");
                        var terminal = await ExecuteStepAsync(child, context, request, pendingWrites, ct);
                        if (terminal != null) return terminal;
                    }
                    return null;
                }

                case "read":
                {
                    var collection = step.GetValueOrDefault("collection") as string ?? "";
                    var key = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("key"), context));
                    var record = request.ReadRecord(collection, key);
                    var data = EndpointExpression.IsNullish(record) ? null : record;
                    var requiredError = CheckRequiredResult(step, data, "read", context, id);
                    if (requiredError != null) return requiredError;
                    context[asKey ?? id] = data;
                    return null;
                }

                case "condition":
                {
                    var condResult = await ExecuteConditionStepAsync(step, context, id, request, pendingWrites, ct);
                    return condResult;
                }

                case "assert":
                {
                    var check = step.GetValueOrDefault("check") as Dictionary<string, object?>;
                    if (check == null)
                        return Fail(400, $"Assert \"{id}\": check must be a condition object.", StepCtx(step, errorCode: "INVALID_ASSERT"));
                    if (EndpointExpression.EvaluateCondition(check, context))
                    {
                        context[id] = true;
                        return null;
                    }
                    var status = ToInt(step.GetValueOrDefault("status"), 400);
                    var errorCode = (step.GetValueOrDefault("errorCode") ?? step.GetValueOrDefault("error")) as string ?? "ASSERTION_FAILED";
                    var message = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(
                        step.GetValueOrDefault("message") ?? step.GetValueOrDefault("errorMessage") ?? "Assertion failed.", context));
                    return Fail(status, message, StepCtx(step, errorCode: errorCode));
                }

                case "transform":
                {
                    object? data;
                    if (step.GetValueOrDefault("expression") is string exprStr)
                        data = EndpointExpression.EvaluateExpression(exprStr, context);
                    else
                        data = EndpointExpression.ResolveTemplate(step.GetValueOrDefault("value"), context);
                    context[id] = data;
                    return null;
                }

                case "object":
                {
                    var fields = step.GetValueOrDefault("fields") ?? step.GetValueOrDefault("value");
                    if (fields is not Dictionary<string, object?>)
                        return Fail(400, $"Object \"{id}\": fields must be an object.", StepCtx(step));
                    context[id] = EndpointExpression.ResolveDeep(fields, context);
                    return null;
                }

                case "array":
                {
                    var items = step.GetValueOrDefault("items") ?? new List<object?>();
                    if (items is not List<object?>)
                        return Fail(400, $"Array \"{id}\": items must be an array.", StepCtx(step));
                    context[id] = EndpointExpression.ResolveDeep(items, context);
                    return null;
                }

                case "merge":
                {
                    var sources = step.GetValueOrDefault("sources") ?? new List<object?>();
                    if (sources is not List<object?>)
                        return Fail(400, $"Merge \"{id}\": sources must be an array.", StepCtx(step));
                    var resolved = (List<object?>)EndpointExpression.ResolveDeep(sources, context)!;
                    var merged = new Dictionary<string, object?>();
                    for (var i = 0; i < resolved.Count; i++)
                    {
                        if (resolved[i] is not Dictionary<string, object?> src)
                            return Fail(400, $"Merge \"{id}\": source[{i}] must resolve to an object.", StepCtx(step));
                        foreach (var (k, v) in src) merged[k] = v;
                    }
                    context[id] = merged;
                    return null;
                }

                case "sort":
                    return ExecuteSortStep(step, context, id);

                case "switch":
                    return ExecuteSwitchStep(step, context, id);

                case "compute":
                    return ExecuteComputeStep(step, context, id);

                case "write":
                {
                    var collection = step.GetValueOrDefault("collection") as string ?? "";
                    var key = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("key"), context));
                    if (string.IsNullOrEmpty(key))
                        return Fail(400, $"Step \"{id}\": key resolved to empty.", StepCtx(step));

                    // Load existing record for ops resolution
                    var existing = request.ReadRecord(collection, key);
                    var existingDict = existing as Dictionary<string, object?> ?? new Dictionary<string, object?>();

                    // Resolve and apply ops (templates resolved now, execution deferred)
                    var rawOps = step.GetValueOrDefault("ops");
                    var ops = rawOps is List<object?> opsList ? opsList : new List<object?>();
                    var resolvedOps = ResolveWriteOps(ops, context, $"Write \"{id}\"");
                    var filteredOps = FilterWriteOps(resolvedOps, context, $"Write \"{id}\"");

                    if (filteredOps.Count == 0) return null;

                    var applyResult = RecordOperations.Apply(existingDict, filteredOps);
                    if (!applyResult.Ok)
                        return Fail(400, $"Write \"{id}\": {applyResult.Error}", StepCtx(step));

                    // Queue the write — executed after all steps complete (matching Bun)
                    pendingWrites.Add(new PendingWrite(collection, key, applyResult.Data, false));
                    return null;
                }

                case "delete":
                {
                    var collection = step.GetValueOrDefault("collection") as string ?? "";
                    var key = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("key"), context));
                    if (string.IsNullOrEmpty(key))
                        return Fail(400, $"Step \"{id}\": key resolved to empty.", StepCtx(step));

                    // Queue the delete — executed after all steps complete
                    pendingWrites.Add(new PendingWrite(collection, key, null, true));
                    return null;
                }

                case "random":
                {
                    var mode = step.GetValueOrDefault("mode") as string ?? "int";
                    switch (mode)
                    {
                        case "int":
                        {
                            var min = Math.Floor(ResolveNum(step.GetValueOrDefault("min"), context, 0));
                            var max = Math.Floor(ResolveNum(step.GetValueOrDefault("max"), context, 100));
                            context[id] = RandomValueGenerator.RandomInt(min, max);
                            return null;
                        }
                        case "float":
                        {
                            var min = ResolveNum(step.GetValueOrDefault("min"), context, 0);
                            var max = ResolveNum(step.GetValueOrDefault("max"), context, 1);
                            var precision = step.GetValueOrDefault("precision") is double p ? (int)p : 2;
                            context[id] = Math.Round(RandomValueGenerator.RandomFloat(min, max), precision);
                            return null;
                        }
                        case "weighted":
                        {
                            var items = ResolveRandomItems(step, context);
                            if (items == null || items.Count == 0)
                                return Fail(400, $"Random \"{id}\": could not resolve item source.", StepCtx(step));
                            var whereFiltered = ApplyWhereFilter(items, step.GetValueOrDefault("where"), context);
                            var selected = RandomValueGenerator.WeightedSelect(whereFiltered, step.GetValueOrDefault("weightField") as string ?? "weight");
                            context[id] = selected;
                            return null;
                        }
                        case "beta":
                        {
                            var alpha = ResolveNum(step.GetValueOrDefault("alpha"), context, 1);
                            var beta = ResolveNum(step.GetValueOrDefault("beta"), context, 1);
                            var min = ResolveNum(step.GetValueOrDefault("min"), context, 0);
                            var max = ResolveNum(step.GetValueOrDefault("max"), context, 1);
                            var precision = step.GetValueOrDefault("precision") is double bp ? (int)bp : 2;
                            context[id] = Math.Round(RandomValueGenerator.BetaSample(alpha, beta, min, max), precision);
                            return null;
                        }
                        default:
                            return Fail(400, $"Random \"{id}\": unknown mode \"{mode}\".", StepCtx(step));
                    }
                }

                case "lookup":
                {
                    var where = step.GetValueOrDefault("where");
                    if (!IsWhereClauseUsable(where))
                        return Fail(400, $"Step \"{id}\": lookup requires a \"where\" clause.", StepCtx(step));

                    var records = ResolveScanSource(step, context, request);
                    if (records == null)
                        return Fail(400, $"Step \"{id}\": could not resolve scan source.", StepCtx(step));

                    foreach (var record in records)
                    {
                        if (EvaluateWhereClause(where, record, context))
                        {
                            context[id] = record;
                            return null;
                        }
                    }

                    // No match — check required
                    var reqErr = CheckRequiredResult(step, null, "lookup", context, id);
                    if (reqErr != null) return reqErr;
                    context[id] = null;
                    return null;
                }

                case "filter":
                {
                    var where = step.GetValueOrDefault("where");
                    if (!IsWhereClauseUsable(where))
                        return Fail(400, $"Step \"{id}\": filter requires a \"where\" clause.", StepCtx(step));

                    var records = ResolveScanSource(step, context, request);
                    if (records == null)
                        return Fail(400, $"Step \"{id}\": could not resolve scan source.", StepCtx(step));

                    var matches = records.Where(r => EvaluateWhereClause(where, r, context)).ToList();
                    var reqErr = CheckRequiredResult(step, matches, "filter", context, id);
                    if (reqErr != null) return reqErr;
                    context[id] = matches;
                    return null;
                }

                case "lookup_many":
                {
                    var keyField = step.GetValueOrDefault("keyField") as string ?? "id";
                    var rawKeys = step.ContainsKey("keys") ? step.GetValueOrDefault("keys") : step.GetValueOrDefault("values");
                    var resolvedKeys = rawKeys is List<object?> keysList
                        ? EndpointExpression.ResolveDeep(keysList, context) as List<object?>
                        : rawKeys != null ? EndpointExpression.ResolveTemplate(rawKeys, context) as List<object?> : null;

                    if (resolvedKeys == null || resolvedKeys.Count == 0)
                        return Fail(400, $"Step \"{id}\": lookup_many requires a non-empty \"keys\" array.", StepCtx(step));

                    var records = ResolveScanSource(step, context, request);
                    if (records == null)
                        return Fail(400, $"Step \"{id}\": could not resolve scan source.", StepCtx(step));

                    var byKey = new Dictionary<string, object?>();
                    foreach (var record in records)
                    {
                        var keyValue = EndpointExpression.GetNestedValue(record, keyField);
                        if (!EndpointExpression.IsNullish(keyValue))
                            byKey[EndpointExpression.JsToString(keyValue)] = record;
                    }

                    var asMap = step.GetValueOrDefault("asMap") is not false;
                    if (asMap)
                    {
                        var map = new Dictionary<string, object?>();
                        foreach (var key in resolvedKeys)
                        {
                            var ks = EndpointExpression.JsToString(key);
                            map[ks] = byKey.TryGetValue(ks, out var rec) ? rec : null;
                        }
                        context[id] = map;
                    }
                    else
                    {
                        var rows = new List<object?>();
                        foreach (var key in resolvedKeys)
                        {
                            var ks = EndpointExpression.JsToString(key);
                            rows.Add(byKey.TryGetValue(ks, out var rec) ? rec : null);
                        }
                        context[id] = rows;
                    }
                    return null;
                }

                case "random_select":
                {
                    var where = step.GetValueOrDefault("where");
                    if (!IsWhereClauseUsable(where))
                        return Fail(400, $"Step \"{id}\": random_select requires a \"where\" clause.", StepCtx(step));

                    var records = ResolveScanSource(step, context, request);
                    if (records == null)
                        return Fail(400, $"Step \"{id}\": could not resolve scan source.", StepCtx(step));

                    var matches = records.Where(r => EvaluateWhereClause(where, r, context)).ToList();
                    if (matches.Count == 0)
                    {
                        var reqErr = CheckRequiredResult(step, null, "random_select", context, id);
                        if (reqErr != null) return reqErr;
                        context[id] = null;
                        return null;
                    }

                    var weightField = step.GetValueOrDefault("weightField") as string;
                    context[id] = weightField != null
                        ? RandomValueGenerator.WeightedSelect(matches, weightField)
                        : matches[RandomNumberGenerator.GetInt32(matches.Count)];
                    return null;
                }

                case "response":
                    return null;

                case "workflow":
                {
                    if (request.LoadWorkflow is null)
                        throw new EndpointExecutionUnsupportedException("workflow step requires LoadWorkflow delegate");
                    var wfId = step.GetValueOrDefault("workflow") as string ?? "";
                    if (string.IsNullOrEmpty(wfId))
                        return Fail(400, $"Workflow step \"{id}\": requires \"workflow\" field.", StepCtx(step));
                    if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                    var wfDef = await request.LoadWorkflow(wfId, ct);
                    if (wfDef == null)
                        return Fail(400, $"Workflow \"{wfId}\" not found.", StepCtx(step));
                    var wfResult = await ExecuteWorkflowAsync(wfDef, step, context, request, pendingWrites, 0, ct);
                    if (wfResult is EndpointExecutionResult wfErr && !wfErr.Ok)
                        return wfErr;
                    if (wfResult is Dictionary<string, object?> wfData)
                        context[asKey ?? id] = wfData;
                    else
                        context[asKey ?? id] = wfResult;
                    return null;
                }


                case "webhook":
                {
                    var webhookUrl = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("url"), context));
                    if (string.IsNullOrEmpty(webhookUrl)
                        || !(webhookUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)
                            || webhookUrl.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal)))
                    {
                        return Fail(400, $"Webhook step \"{id}\": invalid or missing Discord webhook URL.", StepCtx(step));
                    }
                    var title = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("title"), context));
                    var description = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("description"), context));
                    var color = step.GetValueOrDefault("color") is string colorStr && int.TryParse(colorStr, System.Globalization.NumberStyles.HexNumber, null, out var parsedColor) ? parsedColor : 0x5865f2;
                    var rawFields = step.GetValueOrDefault("fields") as List<object?> ?? new List<object?>();
                    var fields = new List<object?>();
                    foreach (var f in rawFields)
                    {
                        if (f is not Dictionary<string, object?> field) continue;
                        fields.Add(new Dictionary<string, object?>
                        {
                            ["name"] = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(field.GetValueOrDefault("name"), context)),
                            ["value"] = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(field.GetValueOrDefault("value"), context)),
                            ["inline"] = field.GetValueOrDefault("inline") is true,
                        });
                    }
                    var payload = new Dictionary<string, object?>
                    {
                        ["embeds"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["title"] = title,
                                ["description"] = description,
                                ["color"] = color,
                                ["fields"] = fields,
                                ["timestamp"] = request.ExecTime.ToString("o"),
                            }
                        }
                    };

                    // Dry-run: skip sending in test/shadow mode
                    if (context.GetValueOrDefault("_skipWebhooks") is true)
                    {
                        context[asKey ?? id] = new Dictionary<string, object?>
                        {
                            ["ok"] = true, ["status"] = 200, ["skipped"] = true, ["payload"] = payload
                        };
                        return null;
                    }

                    if (request.ExecuteWebhookAsync is null)
                    {
                        context[asKey ?? id] = new Dictionary<string, object?> { ["ok"] = false, ["status"] = 0, ["error"] = "No webhook executor" };
                        return null;
                    }

                    try
                    {
                        var (ok, status, error) = await request.ExecuteWebhookAsync(webhookUrl, payload, ct);
                        context[asKey ?? id] = new Dictionary<string, object?> { ["ok"] = ok, ["status"] = status };
                        if (error != null) ((Dictionary<string, object?>)context[asKey ?? id]!)["error"] = error;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        context[asKey ?? id] = new Dictionary<string, object?> { ["ok"] = false, ["status"] = 0, ["error"] = ex.Message };
                    }
                    return null;
                }

                case "sleep":
                {
                    var durationMs = step.GetValueOrDefault("durationMs") ?? step.GetValueOrDefault("ms") ?? step.GetValueOrDefault("sleepMs") ?? 0d;
                    var duration = durationMs is double d ? d : 0d;
                    if (!double.IsFinite(duration) || duration < 0 || duration > 300_000)
                        return Fail(400, $"Sleep step \"{id}\" duration {duration}ms exceeds max 300000ms.", StepCtx(step));
                    // Shadow/discovery comparison records the deterministic result without
                    // blocking a background task for up to 5 minutes; live serve honors the delay.
                    if (duration > 0 && context.GetValueOrDefault("_skipSleep") is not true)
                        await Task.Delay(TimeSpan.FromMilliseconds(duration), ct);
                    context[asKey ?? id] = new Dictionary<string, object?> { ["sleptMs"] = duration };
                    return null;
                }
                default:
                    throw new EndpointExecutionUnsupportedException($"step type '{type}'");
            }
        }
        catch (ExpressionException ex)
        {
            return Fail(400, $"{type} step \"{id}\" failed: {ex.Message}", StepCtx(step));
        }
    }

    private EndpointExecutionResult? ExecuteConditionStep(Dictionary<string, object?> step, Dictionary<string, object?> context, string id)
    {
        if (step.GetValueOrDefault("workflow") is string)
            throw new EndpointExecutionUnsupportedException("condition references a saved workflow");

        var check = step.GetValueOrDefault("check") as Dictionary<string, object?>;
        if (check == null)
            throw new ExpressionException($"Step \"{id}\": condition requires \"check\" or \"workflow\".");

        var passed = EndpointExpression.EvaluateCondition(check, context);
        if (passed)
        {
            context[id] = true;
            return null;
        }

        var failActions = ResolveFalseRoute(step);
        var action = failActions.GetValueOrDefault("action") as string ?? "reject";
        if (action == "skip" || failActions.GetValueOrDefault("clamp") is true)
            throw new EndpointExecutionUnsupportedException("condition uses skip/clamp fail-route");

        var reject = !(failActions.GetValueOrDefault("reject") is false);
        if (!reject)
        {
            context[id] = false;
            return null;
        }

        var rawMsg = (failActions.GetValueOrDefault("errorMessage") ?? failActions.GetValueOrDefault("message")) as string
                     ?? "Condition check failed.";
        var resolvedMsg = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(rawMsg, context));
        var errorCode = (failActions.GetValueOrDefault("errorCode") ?? failActions.GetValueOrDefault("error")) as string ?? "CONDITION_FAILED";
        var status = ToInt(failActions.GetValueOrDefault("status"), 400);
        var flag = failActions.GetValueOrDefault("flag");

        return new EndpointExecutionResult(false, status, new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?> { ["code"] = errorCode, ["message"] = resolvedMsg },
            ["flagged"] = flag is true,
            ["severity"] = failActions.GetValueOrDefault("severity"),
            ["conditionRejection"] = true,
            ["fireWebhook"] = failActions.GetValueOrDefault("webhook") is true,
        });
    }

    // ── Async condition step with route handling ──

    private async Task<object?> ExecuteConditionStepAsync(Dictionary<string, object?> step, Dictionary<string, object?> context, string id, EndpointExecutionRequest request, List<PendingWrite> pendingWrites, CancellationToken ct)
    {
        // If referencing a saved workflow, load and evaluate it.
        if (step.GetValueOrDefault("workflow") is string wfId && request.LoadWorkflow != null)
        {
            var wfDef = await request.LoadWorkflow(wfId, ct);
            if (wfDef == null) throw new ExpressionException($"Workflow \"{wfId}\" not found.");
            var bindings = step.GetValueOrDefault("params") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            var wfContext = new Dictionary<string, object?>(context, StringComparer.Ordinal);
            foreach (var (k, v) in bindings) wfContext[k] = EndpointExpression.ResolveTemplate(v, context);
            var wfCheck = wfDef.GetValueOrDefault("check") as Dictionary<string, object?> ?? wfDef.GetValueOrDefault("condition") as Dictionary<string, object?>;
            if (wfCheck == null) throw new ExpressionException($"Workflow \"{wfId}\" has no condition/check.");
            var wfPassed = EndpointExpression.EvaluateCondition(wfCheck, wfContext);
            if (!wfPassed)
            {
                var failActions = wfDef.GetValueOrDefault("onFail") as Dictionary<string, object?>;
                var rawMsg = (failActions?.GetValueOrDefault("errorMessage") ?? failActions?.GetValueOrDefault("message")) as string ?? "Workflow condition failed.";
                var resolvedMsg = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(rawMsg, wfContext));
                var code = (failActions?.GetValueOrDefault("errorCode")) as string ?? "WORKFLOW_FAILED";
                var status = ToInt(failActions?.GetValueOrDefault("status"), 400);
                return Fail(status, resolvedMsg, StepCtx(step, errorCode: code));
            }
            context[id] = true;
            return null;
        }

        var check = step.GetValueOrDefault("check") as Dictionary<string, object?>;
        if (check == null)
            throw new ExpressionException($"Step \"{id}\": condition requires \"check\" or \"workflow\".");

        var passed = EndpointExpression.EvaluateCondition(check, context);
        context[id] = passed;

        // Normalize routes using the existing dictionary-based normalizer.
        var normalizedRoutes = NormalizeRoutes(step);
        var routeSource = passed ? normalizedRoutes.GetValueOrDefault("true") : normalizedRoutes.GetValueOrDefault("false");
        var routeDict = routeSource as Dictionary<string, object?> ?? new Dictionary<string, object?> { ["action"] = passed ? "continue" : "reject" };
        var routeAction = routeDict.GetValueOrDefault("action") as string ?? "continue";

        if (routeAction == "continue") return null;
        if (routeAction == "reject")
        {
            var rawMsg = (routeDict.GetValueOrDefault("errorMessage") ?? routeDict.GetValueOrDefault("message") ?? "Condition check failed.") as string;
            var resolvedMsg = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(rawMsg, context));
            var errorCode = (routeDict.GetValueOrDefault("errorCode") ?? routeDict.GetValueOrDefault("error") ?? "CONDITION_FAILED") as string;
            var status = ToInt(routeDict.GetValueOrDefault("status"), 400);
            var flag = routeDict.GetValueOrDefault("flag");
            return new EndpointExecutionResult(false, status, new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?> { ["code"] = errorCode, ["message"] = resolvedMsg },
                ["flagged"] = flag is true,
                ["severity"] = routeDict.GetValueOrDefault("severity"),
                ["conditionRejection"] = true,
                ["fireWebhook"] = routeDict.GetValueOrDefault("webhook") is true,
            });
        }
        if (routeAction == "return")
            return new RouteOutcome(Done: true, NextPc: 0);
        if (routeAction == "goto")
        {
            var target = routeDict.GetValueOrDefault("step") as string ?? routeDict.GetValueOrDefault("target") as string ?? "";
            return new RouteOutcome(Done: false, NextPc: -1, TargetStep: target);
        }
        if (routeAction == "skip")
        {
            var count = routeDict.GetValueOrDefault("count") is double c ? (int)c : 1;
            return new RouteOutcome(Done: false, NextPc: -1, SkipCount: count);
        }
        if (routeAction == "run" && request.LoadWorkflow != null)
        {
            var flowId = routeDict.GetValueOrDefault("flow") as string
                ?? routeDict.GetValueOrDefault("workflow") as string
                ?? routeDict.GetValueOrDefault("endpoint") as string
                ?? routeDict.GetValueOrDefault("target") as string ?? "";
            if (!string.IsNullOrEmpty(flowId))
            {
                var flowDef = await request.LoadWorkflow(flowId, ct);
                if (flowDef != null)
                {
                    var runResult = await ExecuteWorkflowAsync(flowDef, step, context, request, pendingWrites, 1, ct);
                    var resultKey = routeDict.GetValueOrDefault("as") as string ?? $"{id}_run";
                    if (runResult is EndpointExecutionResult runErr && !runErr.Ok)
                        return runErr;
                    context[resultKey] = runResult;
                }
            }
            return null; // continue to next step after run
        }
        return null;
    }

    // ── Workflow execution ──

    private async Task<object?> ExecuteWorkflowAsync(Dictionary<string, object?> wfDef, Dictionary<string, object?> step, Dictionary<string, object?> parentContext, EndpointExecutionRequest request, List<PendingWrite> pendingWrites, int depth, CancellationToken ct)
    {
        if (depth >= MaxWorkflowDepth)
            return Fail(400, $"Workflow nesting depth exceeded (max {MaxWorkflowDepth}).", StepCtx(step));

        var steps = AsList(wfDef.GetValueOrDefault("steps"));
        if (steps == null || steps.Count == 0)
            return new Dictionary<string, object?>();

        // Build sub-context: inherit builtins, resolve params from parent context.
        var subContext = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["input"] = parentContext.GetValueOrDefault("input"),
            ["steamId"] = parentContext.GetValueOrDefault("steamId"),
            ["playerKey"] = parentContext.GetValueOrDefault("playerKey"),
            ["_hasSecretKey"] = parentContext.GetValueOrDefault("_hasSecretKey"),
            ["_isDedicatedServer"] = parentContext.GetValueOrDefault("_isDedicatedServer"),
            ["now"] = parentContext.GetValueOrDefault("now"),
            ["_unixMs"] = parentContext.GetValueOrDefault("_unixMs"),
            ["_unixS"] = parentContext.GetValueOrDefault("_unixS"),
            ["_dateUTC"] = parentContext.GetValueOrDefault("_dateUTC"),
            ["_timeUTC"] = parentContext.GetValueOrDefault("_timeUTC"),
            ["_datetimeUTC"] = parentContext.GetValueOrDefault("_datetimeUTC"),
            ["values"] = parentContext.GetValueOrDefault("values"),
            ["projectId"] = parentContext.GetValueOrDefault("projectId"),
            ["userId"] = parentContext.GetValueOrDefault("userId"),
        };

        // Resolve params and inject into sub-context.
        var @params = step.GetValueOrDefault("params") as Dictionary<string, object?>;
        if (@params != null)
        {
            foreach (var (paramName, paramTemplate) in @params)
                subContext[paramName] = EndpointExpression.ResolveTemplate(paramTemplate, parentContext);
        }

        // Merge workflow-level let aliases.
        if (wfDef.GetValueOrDefault("let") is Dictionary<string, object?> wfAliases && wfAliases.Count > 0)
            subContext["_aliases"] = new Dictionary<string, object?>(wfAliases, StringComparer.Ordinal);

        // Execute workflow steps sequentially using the same async step dispatcher.
        foreach (var rawWfStep in steps)
        {
            if (rawWfStep is not Dictionary<string, object?> wfStep)
                continue;
            var terminal = await ExecuteStepAsync(wfStep, subContext, request, pendingWrites, ct);
            if (terminal is EndpointExecutionResult err && !err.Ok)
                return err;
        }

        // Map the workflow's returns block into the result (Bun resolves each
        // returns template against the sub-context). Without a returns block,
        // return the full sub-context so step ids stay addressable.
        if (wfDef.GetValueOrDefault("returns") is Dictionary<string, object?> returnsDef)
        {
            var mapped = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, template) in returnsDef)
                mapped[key] = EndpointExpression.ResolveTemplate(template, subContext);
            return mapped;
        }
        return subContext;
    }


    private EndpointExecutionResult? ExecuteSortStep(Dictionary<string, object?> step, Dictionary<string, object?> context, string id)
    {
        var source = EndpointExpression.ResolveTemplate(step.GetValueOrDefault("source"), context);
        if (source is not List<object?> list)
            return Fail(400, $"Sort \"{id}\": source must resolve to an array.", StepCtx(step));
        var direction = string.Equals(step.GetValueOrDefault("direction") as string, "desc", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var indexed = list.Select((item, index) => (item, index, key: ResolveSortKey(step, item, context, index))).ToList();
        indexed.Sort((l, r) =>
        {
            var cmp = CompareSortValues(l.key, r.key);
            if (cmp != 0) return cmp * direction;
            return l.index - r.index;
        });
        context[id] = indexed.Select(e => e.item).ToList();
        return null;
    }

    private static object? ResolveSortKey(Dictionary<string, object?> step, object? item, Dictionary<string, object?> context, int index)
    {
        var itemContext = new Dictionary<string, object?>(context) { ["item"] = item, ["index"] = (double)index };
        if (step.GetValueOrDefault("expression") is string expr)
            return EndpointExpression.EvaluateMath(expr, itemContext);
        if (step.GetValueOrDefault("by") is string by)
        {
            if (by.Contains("{{", StringComparison.Ordinal)) return EndpointExpression.ResolveTemplate(by, itemContext);
            if (by.Trim().Length > 0) return EndpointExpression.GetNestedValue(item, by.Trim());
        }
        return item;
    }

    private static int CompareSortValues(object? left, object? right)
    {
        var ln = EndpointExpression.IsNullish(left);
        var rn = EndpointExpression.IsNullish(right);
        if (ln && rn) return 0;
        if (ln) return 1;
        if (rn) return -1;
        if (left is double a && right is double b) return a.CompareTo(b);
        if (left is bool lb && right is bool rb) return (lb ? 1 : 0).CompareTo(rb ? 1 : 0);
        if (left is string ls && right is string rs) return string.Compare(ls, rs, StringComparison.Ordinal);
        var la = EndpointExpression.JsNumber(left);
        var ra = EndpointExpression.JsNumber(right);
        if (double.IsFinite(la) && double.IsFinite(ra)) return la.CompareTo(ra);
        return string.Compare(EndpointExpression.JsToString(left), EndpointExpression.JsToString(right), StringComparison.Ordinal);
    }

    private EndpointExecutionResult? ExecuteSwitchStep(Dictionary<string, object?> step, Dictionary<string, object?> context, string id)
    {
        if (step.GetValueOrDefault("cases") is not List<object?> cases)
            return Fail(400, $"Switch \"{id}\": cases must be an array.", StepCtx(step));
        for (var i = 0; i < cases.Count; i++)
        {
            if (cases[i] is not Dictionary<string, object?> entry)
                return Fail(400, $"Switch \"{id}\": case[{i}] must be an object.", StepCtx(step));
            if (entry.GetValueOrDefault("when") is not Dictionary<string, object?> when)
                return Fail(400, $"Switch \"{id}\": case[{i}] requires a \"when\" condition object.", StepCtx(step));
            if (EndpointExpression.EvaluateCondition(when, context))
            {
                context[id] = EndpointExpression.ResolveDeep(entry.GetValueOrDefault("then"), context);
                return null;
            }
        }
        context[id] = step.ContainsKey("default")
            ? EndpointExpression.ResolveDeep(step.GetValueOrDefault("default"), context)
            : null;
        return null;
    }

    private EndpointExecutionResult? ExecuteComputeStep(Dictionary<string, object?> step, Dictionary<string, object?> context, string id)
    {
        var defs = (step.GetValueOrDefault("values") ?? step.GetValueOrDefault("fields")) as Dictionary<string, object?>;
        if (defs == null)
            return Fail(400, $"Compute \"{id}\": values must be an object.", StepCtx(step));

        var output = new Dictionary<string, object?>();
        var localContext = new Dictionary<string, object?>(context) { [id] = output };
        var pending = new HashSet<string>(defs.Keys);
        var maxPasses = pending.Count + 1;
        var mode = step.GetValueOrDefault("mode") as string;

        for (var pass = 0; pass < maxPasses && pending.Count > 0; pass++)
        {
            var resolved = new List<string>();
            foreach (var key in pending)
            {
                var def = defs[key];
                try
                {
                    if (def is Dictionary<string, object?> defObj)
                    {
                        if (defObj.TryGetValue("expression", out var ex) && ex is not null)
                            output[key] = EndpointExpression.EvaluateExpression(EndpointExpression.JsToString(ex), localContext);
                        else if (defObj.TryGetValue("value", out var val))
                            output[key] = EndpointExpression.ResolveTemplate(val, localContext);
                        else
                            output[key] = EndpointExpression.ResolveDeep(def, localContext);
                    }
                    else if (def is string defStr)
                    {
                        output[key] = mode == "template"
                            ? EndpointExpression.ResolveTemplate(defStr, localContext)
                            : EndpointExpression.EvaluateExpression(defStr, localContext);
                    }
                    else
                    {
                        output[key] = def;
                    }
                    resolved.Add(key);
                }
                catch (ExpressionException ex)
                {
                    if (!ex.Message.Contains("Unresolved variable", StringComparison.Ordinal)
                        && !ex.Message.Contains("has no property", StringComparison.Ordinal))
                        throw;
                }
            }
            if (resolved.Count == 0 && pending.Count > 0)
            {
                var first = pending.First();
                return Fail(400, $"Compute \"{id}\": Cannot resolve \"{first}\": circular or missing dependency", StepCtx(step));
            }
            foreach (var key in resolved) pending.Remove(key);
        }

        if (mode == null && step.GetValueOrDefault("output") as string == "scalars")
        {
            foreach (var (k, v) in output) context[k] = v;
        }
        else if (step.GetValueOrDefault("output") as string == "scalars")
        {
            foreach (var (k, v) in output) context[k] = v;
        }
        else
        {
            context[id] = output;
        }
        return null;
    }

    // ── Response building ──

    private static EndpointExecutionResult BuildResponse(Dictionary<string, object?> endpointDef, Dictionary<string, object?> context)
    {
        var responseDef = endpointDef.GetValueOrDefault("response") as Dictionary<string, object?>
                          ?? new Dictionary<string, object?> { ["status"] = 200d, ["body"] = new Dictionary<string, object?> { ["ok"] = true } };

        object? body;
        if (responseDef.GetValueOrDefault("echo") is List<object?> echo)
        {
            var baseBody = EndpointExpression.ResolveDeep(responseDef.GetValueOrDefault("body") ?? new Dictionary<string, object?>(), context) as Dictionary<string, object?>
                           ?? new Dictionary<string, object?>();
            foreach (var (k, v) in BuildEchoResponse(echo, context)) baseBody[k] = v;
            body = baseBody;
        }
        else
        {
            body = EndpointExpression.ResolveDeep(
                responseDef.GetValueOrDefault("body") ?? new Dictionary<string, object?> { ["ok"] = true }, context);
        }

        return new EndpointExecutionResult(true, ToInt(responseDef.GetValueOrDefault("status"), 200), body);
    }

    private static readonly Regex EchoTokenRegex = new("\\{\\{\\s*([a-zA-Z0-9_.$]+)\\s*\\}\\}", RegexOptions.Compiled);

    private static Dictionary<string, object?> BuildEchoResponse(List<object?> echo, Dictionary<string, object?> context)
    {
        var body = new Dictionary<string, object?>();
        foreach (var entry in echo)
        {
            if (entry is not string tpl || !tpl.Contains("{{", StringComparison.Ordinal)) continue;
            var match = EchoTokenRegex.Match(tpl);
            if (!match.Success) continue;
            var path = match.Groups[1].Value;
            var key = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
            if (key.Length == 0) continue;
            body[key] = EndpointExpression.ResolveTemplate(tpl, context);
        }
        return body;
    }

    // ── required / fail helpers ──

    private static EndpointExecutionResult? CheckRequiredResult(Dictionary<string, object?> step, object? result, string stepType, Dictionary<string, object?> context, string id)
    {
        if (step.GetValueOrDefault("required") is not true) return null;
        var isMissing = EndpointExpression.IsNullish(result) || (result is List<object?> l && l.Count == 0);
        if (!isMissing) return null;
        var onMissing = step.GetValueOrDefault("onMissing") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var status = ToInt(onMissing.GetValueOrDefault("status"), 400);
        var errorCode = onMissing.GetValueOrDefault("errorCode") as string ?? "NOT_FOUND";
        var message = onMissing.GetValueOrDefault("message") is string m
            ? EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(m, context))
            : $"Step \"{id}\": required {stepType} returned no result.";
        return Fail(status, message, StepCtx(step, errorCode: errorCode));
    }

    private static EndpointExecutionResult Fail(int status, string message, Dictionary<string, object?> stepInfo)
    {
        string code;
        if (stepInfo.GetValueOrDefault("errorCode") is string ec) code = ec;
        else if (stepInfo.GetValueOrDefault("type") is string t) code = $"ENDPOINT_ERROR.{t.ToUpperInvariant()}";
        else code = "ENDPOINT_ERROR";

        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["docsUrl"] = "https://sboxcool.com/wiki/network-storage-v3/endpoints#error-handling",
            ["step"] = stepInfo,
        };
        return new EndpointExecutionResult(false, status, new Dictionary<string, object?> { ["error"] = error });
    }

    private static Dictionary<string, object?> StepCtx(Dictionary<string, object?> step, string? errorCode = null)
    {
        var info = new Dictionary<string, object?>
        {
            ["id"] = step.GetValueOrDefault("id"),
            ["type"] = step.GetValueOrDefault("type"),
        };
        if (step.GetValueOrDefault("collection") is { } c) info["collection"] = c;
        if (step.GetValueOrDefault("expression") is { } e) info["expression"] = e;
        if (step.GetValueOrDefault("key") is { } k) info["key"] = k;
        if (step.GetValueOrDefault("table") is { } tb) info["table"] = tb;
        if (errorCode != null) info["errorCode"] = errorCode;
        return info;
    }

    // ── Routing (linear-path subset) ──

    private static bool RouteRequiresModernExecution(List<object?> steps)
    {
        foreach (var raw in steps)
        {
            if (raw is not Dictionary<string, object?> step) continue;
            if (step.GetValueOrDefault("type") as string == "sleep") continue; // handled natively
            if (step.ContainsKey("retry") || step.ContainsKey("onTrue") || step.ContainsKey("onFalse")) return true;
            if (step.GetValueOrDefault("routes") is Dictionary<string, object?>)
            {
                var routes = NormalizeRoutes(step);
                foreach (var route in new[] { routes.GetValueOrDefault("true") as Dictionary<string, object?>, routes.GetValueOrDefault("false") as Dictionary<string, object?> })
                {
                    var action = route?.GetValueOrDefault("action") as string;
                    if (action != null && !RouteContinueOrReject.Contains(action)) return true;
                    if (action == "skip" && (route!.ContainsKey("step") || route.ContainsKey("goto") || route.ContainsKey("target"))) return true;
                }
            }
            if (AsList(step.GetValueOrDefault("steps")) is { } children && RouteRequiresModernExecution(children)) return true;
        }
        return false;
    }

    // Resolve the normalized `false` route for a condition step (fail-actions source).
    private static Dictionary<string, object?> ResolveFalseRoute(Dictionary<string, object?> step)
    {
        var routes = NormalizeRoutes(step);
        return routes.GetValueOrDefault("false") as Dictionary<string, object?> ?? new Dictionary<string, object?> { ["action"] = "reject", ["reject"] = true };
    }

    private static Dictionary<string, object?> NormalizeRoutes(Dictionary<string, object?> step)
    {
        var existing = step.GetValueOrDefault("routes") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var trueRoute = NormalizeRouteOutcome(step.GetValueOrDefault("onTrue") ?? existing.GetValueOrDefault("true") ?? existing.GetValueOrDefault("pass"), "continue");
        var falseSource = step.GetValueOrDefault("onFalse") ?? existing.GetValueOrDefault("false") ?? existing.GetValueOrDefault("fail")
                          ?? LegacyOnFailToRoute(step.GetValueOrDefault("onFail"));
        var falseRoute = NormalizeRouteOutcome(falseSource, "reject");
        return new Dictionary<string, object?> { ["true"] = trueRoute, ["false"] = falseRoute };
    }

    private static readonly HashSet<string> RouteActions = new(StringComparer.Ordinal) { "continue", "return", "reject", "goto", "run" };

    private static Dictionary<string, object?> NormalizeRouteOutcome(object? route, string defaultAction)
    {
        if (route is null || route is bool bt && bt) return new Dictionary<string, object?> { ["action"] = defaultAction };
        if (route is bool bf && !bf) return new Dictionary<string, object?> { ["action"] = "reject" };
        if (route is string s)
        {
            if (RouteActions.Contains(s)) return new Dictionary<string, object?> { ["action"] = s };
            if (s == "skip") return new Dictionary<string, object?> { ["action"] = "skip", ["count"] = 1d };
            return new Dictionary<string, object?> { ["action"] = "goto", ["step"] = s };
        }
        if (route is not Dictionary<string, object?> obj) return new Dictionary<string, object?> { ["action"] = defaultAction };
        var result = new Dictionary<string, object?>(obj);
        var rawAction = (obj.GetValueOrDefault("action") ?? obj.GetValueOrDefault("type")) as string ?? defaultAction;
        if (rawAction == "step") { result["action"] = "goto"; result["step"] = obj.GetValueOrDefault("step") ?? obj.GetValueOrDefault("target") ?? obj.GetValueOrDefault("goto"); return result; }
        if (rawAction is "workflow" or "flow" or "endpoint") { result["action"] = "run"; result["flow"] = obj.GetValueOrDefault("flow") ?? obj.GetValueOrDefault("workflow") ?? obj.GetValueOrDefault("endpoint") ?? obj.GetValueOrDefault("target"); return result; }
        if (rawAction == "skip") { result["action"] = "skip"; return result; }
        result["action"] = RouteActions.Contains(rawAction) ? rawAction : defaultAction;
        return result;
    }

    private static object? LegacyOnFailToRoute(object? onFail)
    {
        if (onFail is string of && of == "skip") return new Dictionary<string, object?> { ["action"] = "skip", ["count"] = 1d };
        if (onFail is not Dictionary<string, object?> o) return new Dictionary<string, object?> { ["action"] = "reject" };
        var skip = o.GetValueOrDefault("skip") ?? o.GetValueOrDefault("skipSteps") ?? o.GetValueOrDefault("steps");
        if (o.GetValueOrDefault("action") as string == "skip" || skip != null)
        {
            if (skip is string ss && ss != "next") return new Dictionary<string, object?> { ["action"] = "goto", ["step"] = ss, ["legacySkip"] = true };
            return new Dictionary<string, object?> { ["action"] = "skip", ["skip"] = skip, ["count"] = skip is double d ? d : 1d, ["legacySkip"] = true };
        }
        var result = new Dictionary<string, object?>(o);
        result["action"] = o.GetValueOrDefault("reject") is false ? "continue" : "reject";
        return result;
    }

    // ── small helpers ──

    private static List<object?>? AsList(object? v) => v as List<object?>;

    // ── Write ops resolution (mirrors resolveWriteOps + filterWriteOps in JS) ──

    private static List<object?> ResolveWriteOps(List<object?> ops, Dictionary<string, object?> context, string label)
    {
        var resolved = new List<object?>(ops.Count);
        foreach (var raw in ops)
        {
            if (raw is not Dictionary<string, object?> op) { resolved.Add(raw); continue; }

            // Extract special fields before template resolution
            var hasTemplatedValue = op.TryGetValue("value", out var tv) && tv is string ts && ts.Contains("{{", StringComparison.Ordinal);
            var expression = op.GetValueOrDefault("valueExpression") ?? op.GetValueOrDefault("expression");
            var whenCondition = op.GetValueOrDefault("when") ?? op.GetValueOrDefault("if");

            // Clone without specials, then resolve templates
            var clone = new Dictionary<string, object?>();
            foreach (var (k, v) in op)
                if (k is not ("valueExpression" or "expression" or "when" or "if"))
                    clone[k] = EndpointExpression.ResolveDeep(v, context);

            if (hasTemplatedValue && !clone.ContainsKey("value"))
                clone["_skipIfResolvedValueMissing"] = true;

            // Evaluate expression as value
            if (expression != null)
            {
                clone["value"] = expression is double dn
                    ? dn
                    : EndpointExpression.EvaluateMath(EndpointExpression.JsToString(expression), context);
            }

            // Store condition for later evaluation in FilterWriteOps
            if (whenCondition != null)
                clone["_whenCondition"] = whenCondition;

            resolved.Add(clone);
        }
        return resolved;
    }

    private static List<object?> FilterWriteOps(List<object?> ops, Dictionary<string, object?> context, string label)
    {
        var filtered = new List<object?>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is not Dictionary<string, object?> op) { filtered.Add(ops[i]); continue; }

            // Evaluate when/if condition
            var condition = op.GetValueOrDefault("_whenCondition");
            if (condition != null)
            {
                var condResult = condition switch
                {
                    true => true,
                    false => false,
                    Dictionary<string, object?> condObj => EndpointExpression.EvaluateCondition(condObj, context),
                    _ => throw new ExpressionException($"{label} ops[{i + 1}]: \"when\" must be a condition object or boolean."),
                };
                if (!condResult) continue;
            }

            // Remove internal fields
            var next = new Dictionary<string, object?>();
            foreach (var (k, v) in op)
                if (k is not ("_whenCondition" or "_skipIfResolvedValueMissing"))
                    next[k] = v;

            // Skip ops where template-resolved value is missing
            if (op.GetValueOrDefault("_skipIfResolvedValueMissing") is true && !next.ContainsKey("value"))
                continue;

            filtered.Add(next);
        }
        return filtered;
    }

    // ── Scan step helpers (lookup/filter/lookup_many/random_select) ──

    private static bool IsWhereClauseUsable(object? where)
    {
        if (where is not Dictionary<string, object?> w) return false;
        if (w.ContainsKey("field") || w.ContainsKey("left")) return true;
        if (w.ContainsKey("all") || w.ContainsKey("any")) return true;
        return false;
    }

    private static bool EvaluateWhereClause(object? where, object? record, Dictionary<string, object?> context)
    {
        if (where is List<object?> arr)
            return arr.All(clause => EvaluateWhereClause(clause, record, context));

        if (where is Dictionary<string, object?> w)
        {
            if (w.GetValueOrDefault("all") is List<object?> all)
                return all.All(clause => EvaluateWhereClause(clause, record, context));
            if (w.GetValueOrDefault("any") is List<object?> any)
                return any.Any(clause => EvaluateWhereClause(clause, record, context));

            var rawField = (w.GetValueOrDefault("field") ?? w.GetValueOrDefault("left")) as string;
            var rawValue = w.ContainsKey("value") ? w.GetValueOrDefault("value") : w.GetValueOrDefault("right");
            if (rawField == null) return false;

            var field = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(rawField, context));
            var left = EndpointExpression.GetNestedValue(record, field);
            var right = EndpointExpression.ResolveTemplate(rawValue, context);
            var op = w.GetValueOrDefault("op") as string;
            if (op == null) return false;
            return EndpointExpression.Compare(left, EndpointExpression.NormalizeOp(op), right);
        }

        return false;
    }

    private static List<object?>? ResolveScanSource(Dictionary<string, object?> step, Dictionary<string, object?> context, EndpointExecutionRequest request)
    {
        // source:"values" — scan from game value tables (in-memory)
        if (step.GetValueOrDefault("source") as string == "values")
        {
            var tableName = (step.GetValueOrDefault("table") ?? step.GetValueOrDefault("collection")) as string;
            if (tableName == null) return null;
            var values = context.GetValueOrDefault("values") as Dictionary<string, object?>;
            if (values != null && values.TryGetValue(tableName, out var table) && table is List<object?> tableList)
                return tableList;
            return null;
        }

        // Default: scan from a collection via the ScanCollection delegate
        var collection = step.GetValueOrDefault("collection") as string;
        if (string.IsNullOrEmpty(collection)) return null;
        if (request.ScanCollection == null) return null;
        return request.ScanCollection(collection);
    }


    // ── Random step helpers ──

    private static double ResolveNum(object? val, Dictionary<string, object?> context, double defaultVal)
    {
        if (val == null || EndpointExpression.IsUndefined(val)) return defaultVal;
        var resolved = EndpointExpression.ResolveTemplate(val, context);
        var num = EndpointExpression.JsNumber(resolved);
        return double.IsNaN(num) ? defaultVal : num;
    }

    private static List<object?>? ResolveRandomItems(Dictionary<string, object?> step, Dictionary<string, object?> context)
    {
        // Inline items
        if (step.GetValueOrDefault("items") is List<object?> items)
            return items.Select(i => EndpointExpression.ResolveDeep(i, context)).ToList();

        // From context variable
        if (step.GetValueOrDefault("from") is string from)
        {
            var resolved = EndpointExpression.ResolveTemplate(from, context);
            if (resolved is List<object?> fromList) return fromList;
        }

        // Source: "values" + table reference
        if (step.GetValueOrDefault("source") as string == "values" && step.GetValueOrDefault("table") is string table)
        {
            var values = context.GetValueOrDefault("values") as Dictionary<string, object?>;
            if (values != null && values.TryGetValue(table, out var tableVal) && tableVal is List<object?> tableList)
                return tableList;
        }

        return null;
    }

    private static List<object?> ApplyWhereFilter(List<object?> items, object? whereObj, Dictionary<string, object?> context)
    {
        if (whereObj is not Dictionary<string, object?> where || where.Count == 0) return items;
        var field = where.GetValueOrDefault("field") as string ?? where.GetValueOrDefault("left") as string;
        var op = where.GetValueOrDefault("op") as string;
        var value = where.GetValueOrDefault("value");
        if (field == null || op == null) return items;
        var normalizedOp = EndpointExpression.NormalizeOp(op);
        return items.Where(item =>
        {
            var itemContext = new Dictionary<string, object?>(context) { ["item"] = item };
            var left = EndpointExpression.ResolveFieldForComparison(field, itemContext);
            var right = EndpointExpression.ResolveTemplate(value, itemContext);
            return EndpointExpression.Compare(left, normalizedOp, right);
        }).ToList();
    }

    private static int ToInt(object? v, int fallback)
    {
        if (v is double d && d != 0 && !double.IsNaN(d)) return (int)d;
        if (v is double z && z == 0) return fallback; // JS `x || fallback` treats 0 as falsy
        var n = EndpointExpression.JsNumber(v);
        return double.IsFinite(n) && n != 0 ? (int)n : fallback;
    }

    private static Dictionary<string, object?> ToValueDict(IReadOnlyDictionary<string, object?> source)
        => source as Dictionary<string, object?> ?? new Dictionary<string, object?>(source);
    /// <summary>
    /// Extracts all <c>(collectionId, key)</c> pairs for <c>read</c> steps whose
    /// key template resolves successfully from the initial context.  Used by the
    /// async orchestrator to pre-fetch records from the data store before handing
    /// a sync <c>Func&lt;string, string, object?&gt;</c> to <see cref="Execute"/>.
    /// Unresolvable keys are silently skipped (the read step will return null,
    /// or the executor will raise <see cref="EndpointExecutionUnsupportedException"/>
    /// if the step is <c>required</c>).
    /// </summary>
    public static IReadOnlyList<(string CollectionId, string Key)> DiscoverReadTargets(
        Dictionary<string, object?> endpointDef,
        IDictionary<string, object?> context)
    {
        var targets = new List<(string, string)>();
        var steps = AsList(endpointDef.GetValueOrDefault("steps")) ?? new List<object?>();
        DiscoverReadTargetsWalk(steps, context, targets);
        return targets;
    }

    private static void DiscoverReadTargetsWalk(
        List<object?> steps, IDictionary<string, object?> context, List<(string, string)> targets)
    {
        foreach (var raw in steps)
        {
            if (raw is not Dictionary<string, object?> step) continue;
            if (step.GetValueOrDefault("type") as string == "block")
            {
                var children = AsList(step.GetValueOrDefault("steps")) ?? new List<object?>();
                DiscoverReadTargetsWalk(children, context, targets);
                continue;
            }
            if (step.GetValueOrDefault("type") as string is not ("read" or "write" or "delete")) continue;
            var collection = step.GetValueOrDefault("collection") as string;
            if (string.IsNullOrEmpty(collection)) continue;
            try
            {
                var key = EndpointExpression.JsToString(EndpointExpression.ResolveTemplate(step.GetValueOrDefault("key"), context));
                if (!string.IsNullOrEmpty(key))
                    targets.Add((collection, key));
            }
            catch { /* key template didn't resolve from initial context; skip */ }
        }
    }

    /// <summary>
    /// Extracts collection IDs referenced by lookup/filter/lookup_many/random_select
    /// steps. Used by the orchestrator to pre-fetch all records in those collections
    /// from ScyllaDB before running the sync executor.
    /// </summary>
    public static IReadOnlyList<string> DiscoverScanCollections(Dictionary<string, object?> endpointDef)
    {
        var collections = new HashSet<string>(StringComparer.Ordinal);
        var steps = AsList(endpointDef.GetValueOrDefault("steps")) ?? new List<object?>();
        DiscoverScanCollectionsWalk(steps, collections);
        return collections.ToList();
    }

    private static void DiscoverScanCollectionsWalk(List<object?> steps, HashSet<string> collections)
    {
        foreach (var raw in steps)
        {
            if (raw is not Dictionary<string, object?> step) continue;
            var type = step.GetValueOrDefault("type") as string;
            if (type == "block")
            {
                var children = AsList(step.GetValueOrDefault("steps")) ?? new List<object?>();
                DiscoverScanCollectionsWalk(children, collections);
                continue;
            }
            if (type is not ("lookup" or "filter" or "lookup_many" or "random_select")) continue;
            if (step.GetValueOrDefault("source") as string == "values") continue;
            var collection = step.GetValueOrDefault("collection") as string;
            if (!string.IsNullOrEmpty(collection))
                collections.Add(collection);
        }
    }
    private static readonly HashSet<string> WebhookCapableStepTypes =
        new(StringComparer.Ordinal) { "webhook", "workflow" };

    /// <summary>
    /// True if the endpoint may dispatch a Discord webhook when served live: it has a
    /// <c>webhook</c> step, or references a saved <c>workflow</c> (whose sub-steps are
    /// not statically visible here and may themselves webhook). Record writes/deletes
    /// are NOT flagged — the live-serve path flushes them durably to the authoritative
    /// store. A live-serve caller without a real webhook sender MUST fall back to Bun
    /// for these endpoints so a notification can never be silently dropped; with a
    /// sender wired they serve natively. Deep-scans the whole definition so nested/
    /// blocked/routed steps are not missed; conservative (a false positive merely
    /// defers to Bun).
    /// </summary>
    public static bool RequiresLiveWebhookSender(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> dict:
                if (dict.GetValueOrDefault("type") is string t && WebhookCapableStepTypes.Contains(t)) return true;
                if (dict.ContainsKey("workflow")) return true; // condition/step referencing a saved workflow
                foreach (var v in dict.Values)
                    if (RequiresLiveWebhookSender(v)) return true;
                return false;
            case List<object?> list:
                foreach (var item in list)
                    if (RequiresLiveWebhookSender(item)) return true;
                return false;
            default:
                return false;
        }
    }
}

/// <summary>Endpoint execution result: HTTP status + JS-value-model body.</summary>
public sealed record EndpointExecutionResult(bool Ok, int Status, object? Body);

/// <summary>Inputs for a single endpoint execution.</summary>
public sealed record EndpointExecutionRequest(
    IReadOnlyDictionary<string, object?> Input,
    string SteamId,
    string PlayerKey,
    IReadOnlyDictionary<string, object?> GameValues,
    string ProjectId,
    string UserId,
    bool HasSecretKey,
    bool IsDedicatedServer,
    DateTimeOffset ExecTime,
    Func<string, string, object?> ReadRecord,
    Action<string, string, Dictionary<string, object?>>? WriteRecord = null,
    Action<string, string>? DeleteRecord = null,
    Func<string, List<object?>>? ScanCollection = null,
    Func<string, CancellationToken, Task<Dictionary<string, object?>?>>? LoadWorkflow = null,
    Func<string, Dictionary<string, object?>, CancellationToken, Task<(bool Ok, int Status, string? Error)>>? ExecuteWebhookAsync = null,
    bool SkipWebhooks = false,
    bool SkipSleep = false);

/// <summary>
/// Raised when an endpoint definition uses a feature the native executor does not
/// yet reproduce. The caller MUST fall back to the authoritative Bun runtime.
/// </summary>
public sealed class EndpointExecutionUnsupportedException : Exception
{
    public EndpointExecutionUnsupportedException(string reason) : base(reason) { }
}
