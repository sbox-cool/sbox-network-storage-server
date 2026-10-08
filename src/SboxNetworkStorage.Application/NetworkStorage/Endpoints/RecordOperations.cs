using System.Globalization;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// C# port of <c>tools/sbox/operations.js</c> — the Network Storage operations
/// engine. Instead of replacing entire documents, clients send operation arrays:
/// <c>{ "ops": [{ "op": "set", "path": "playerName", "value": "CoolPlayer" }, ...] }</c>
///
/// Supported operations: <c>set</c>, <c>inc</c>, <c>push</c>, <c>pull</c>,
/// <c>remove</c> (alias <c>delete</c>), <c>merge</c>, <c>set_if_null</c>.
///
/// Returns <see cref="ApplyResult"/> containing the updated document, increment
/// deltas (for rate tracking), and per-field source/reason metadata.
/// </summary>
public static class RecordOperations
{
    private static readonly HashSet<string> ValidOps = new(StringComparer.Ordinal)
    {
        "set", "inc", "push", "pull", "remove", "delete", "merge", "set_if_null",
    };

    public sealed record ApplyResult(
        bool Ok,
        Dictionary<string, object?>? Data,
        Dictionary<string, double>? Increments,
        Dictionary<string, OpMetaEntry>? OpMeta,
        string? Error);

    public sealed record OpMetaEntry(string? Source, string? Reason);

    /// <summary>
    /// Apply an array of operations to an existing document. Matches the JS
    /// <c>applyOperations</c> semantics exactly (loose equality in pull/remove,
    /// near-zero snap in inc, set-if-null guard, increment tracking for rate limits).
    /// </summary>
    public static ApplyResult Apply(Dictionary<string, object?>? existing, List<object?> ops)
    {
        if (ops == null || ops.Count == 0)
            return new ApplyResult(false, null, null, null, "ops must be a non-empty array.");
        if (ops.Count > 100)
            return new ApplyResult(false, null, null, null, "Maximum 100 operations per request.");

        var data = DeepClone(existing ?? new Dictionary<string, object?>());
        var increments = new Dictionary<string, double>();
        var opMeta = new Dictionary<string, OpMetaEntry>();

        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is not Dictionary<string, object?> op)
                return new ApplyResult(false, null, null, null, $"ops[{i}]: invalid operation.");
            if (!op.TryGetValue("op", out var opType) || opType is not string opStr || !op.TryGetValue("path", out var pathObj) || pathObj is not string path)
                return new ApplyResult(false, null, null, null, $"ops[{i}]: missing \"op\" or \"path\".");
            if (!ValidOps.Contains(opStr))
                return new ApplyResult(false, null, null, null, $"ops[{i}]: invalid op \"{opStr}\". Must be: {string.Join(", ", ValidOps)}");

            switch (opStr)
            {
                case "set":
                {
                    if (!op.TryGetValue("value", out var value) || EndpointExpression.IsUndefined(value))
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"set\" requires a \"value\".");
                    if (value is double numVal)
                    {
                        var prev = GetNested(data, path);
                        var delta = numVal - (prev is double pn ? pn : 0);
                        if (delta > 0) increments[path] = (increments.TryGetValue(path, out var cur) ? cur : 0) + delta;
                    }
                    SetNested(data, path, value);
                    CaptureMeta(op, opMeta, path);
                    break;
                }

                case "inc":
                {
                    if (!op.TryGetValue("value", out var valObj) || valObj is not double incVal)
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"inc\" requires a numeric \"value\".");
                    var current = GetNested(data, path);
                    var newVal = (current is double cn ? cn : 0) + incVal;
                    if (newVal != 0 && Math.Abs(newVal) < 1e-9) newVal = 0;
                    SetNested(data, path, newVal);
                    if (incVal > 0) increments[path] = (increments.TryGetValue(path, out var cur2) ? cur2 : 0) + incVal;
                    CaptureMeta(op, opMeta, path);
                    break;
                }

                case "push":
                {
                    if (!op.TryGetValue("value", out var pushVal) || EndpointExpression.IsUndefined(pushVal))
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"push\" requires a \"value\".");
                    var arr = GetNested(data, path);
                    if (arr is List<object?> list)
                        list.Add(pushVal);
                    else
                        SetNested(data, path, new List<object?> { pushVal });
                    break;
                }

                case "pull":
                {
                    if (!op.TryGetValue("match", out var matchObj) || matchObj is not Dictionary<string, object?> match)
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"pull\" requires a \"match\" object.");
                    var arr2 = GetNested(data, path);
                    if (arr2 is List<object?> list2)
                    {
                        var filtered = new List<object?>();
                        foreach (var item in list2)
                        {
                            if (item is not Dictionary<string, object?> itemDict)
                            {
                                // Primitives: match.value for direct comparison (loose)
                                if (match.TryGetValue("value", out var mVal) && EndpointExpression.LooseEquals(item, mVal))
                                    continue;
                                filtered.Add(item);
                            }
                            else
                            {
                                // Objects: keep if NOT all match fields equal
                                if (MatchesAll(itemDict, match))
                                    continue;
                                filtered.Add(item);
                            }
                        }
                        SetNested(data, path, filtered);
                    }
                    break;
                }

                case "delete":
                case "remove":
                {
                    if (!op.TryGetValue("value", out var remVal) || EndpointExpression.IsUndefined(remVal))
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"remove\" requires a \"value\".");
                    var arr3 = GetNested(data, path);
                    if (arr3 is List<object?> list3)
                    {
                        var filtered = new List<object?>();
                        foreach (var item in list3)
                        {
                            if (item is not Dictionary<string, object?> itemDict2)
                            {
                                // Primitives: loose equality
                                if (EndpointExpression.LooseEquals(item, remVal)) continue;
                                filtered.Add(item);
                            }
                            else if (remVal is Dictionary<string, object?> remDict)
                            {
                                // Objects: deep-match all specified fields
                                if (MatchesAll(itemDict2, remDict)) continue;
                                filtered.Add(item);
                            }
                            else
                            {
                                filtered.Add(item);
                            }
                        }
                        SetNested(data, path, filtered);
                    }
                    break;
                }

                case "merge":
                {
                    if (!op.TryGetValue("value", out var mergeVal) || mergeVal is not Dictionary<string, object?> mergeDict)
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"merge\" requires an object \"value\".");
                    var target = GetNested(data, path);
                    if (target is null || EndpointExpression.IsUndefined(target))
                    {
                        SetNested(data, path, new Dictionary<string, object?>());
                        target = GetNested(data, path);
                    }
                    if (target is not Dictionary<string, object?> targetDict)
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: cannot merge into a non-object at \"{path}\".");
                    foreach (var (k, v) in mergeDict)
                    {
                        if (v is double mergeNum)
                        {
                            var mergedPath = path.Length > 0 ? $"{path}.{k}" : k;
                            var prev2 = GetNested(data, mergedPath);
                            var delta2 = mergeNum - (prev2 is double pn2 ? pn2 : 0);
                            if (delta2 > 0) increments[mergedPath] = (increments.TryGetValue(mergedPath, out var cur3) ? cur3 : 0) + delta2;
                        }
                        targetDict[k] = v;
                    }
                    CaptureMeta(op, opMeta, path);
                    break;
                }

                case "set_if_null":
                {
                    if (!op.TryGetValue("value", out var sinVal) || EndpointExpression.IsUndefined(sinVal))
                        return new ApplyResult(false, null, null, null, $"ops[{i}]: \"set_if_null\" requires a \"value\".");
                    var existing2 = GetNested(data, path);
                    if (existing2 is null || EndpointExpression.IsUndefined(existing2))
                    {
                        if (sinVal is double sinNum)
                        {
                            var prev3 = GetNested(data, path);
                            var delta3 = sinNum - (prev3 is double pn3 ? pn3 : 0);
                            if (delta3 > 0) increments[path] = (increments.TryGetValue(path, out var cur4) ? cur4 : 0) + delta3;
                        }
                        SetNested(data, path, sinVal);
                        CaptureMeta(op, opMeta, path);
                    }
                    break;
                }
            }
        }

        return new ApplyResult(true, data, increments, opMeta, null);
    }

    // ── Dot-path helpers ──

    public static object? GetNested(object? obj, string path)
    {
        var parts = path.Split('.');
        object? current = obj;
        foreach (var part in parts)
        {
            if (current is Dictionary<string, object?> d)
                current = d.TryGetValue(part, out var v) ? v : null;
            else
                return null;
        }
        return current;
    }

    public static void SetNested(Dictionary<string, object?> obj, string path, object? value)
    {
        var parts = path.Split('.');
        var current = obj;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) || next is not Dictionary<string, object?> nextDict)
            {
                nextDict = new Dictionary<string, object?>();
                current[parts[i]] = nextDict;
            }
            current = nextDict;
        }
        current[parts[^1]] = value;
    }

    // ── Helpers ──

    private static bool MatchesAll(Dictionary<string, object?> item, Dictionary<string, object?> match)
    {
        foreach (var (k, v) in match)
        {
            if (!item.TryGetValue(k, out var iv) || !Equals(iv, v)) return false;
        }
        return true;
    }

    private static void CaptureMeta(Dictionary<string, object?> op, Dictionary<string, OpMetaEntry> opMeta, string path)
    {
        var source = op.TryGetValue("source", out var s) && s is string ss ? ss[..Math.Min(ss.Length, 64)] : null;
        var reason = op.TryGetValue("reason", out var r) && r is string rr ? rr[..Math.Min(rr.Length, 256)] : null;
        if (source != null || reason != null) opMeta[path] = new OpMetaEntry(source, reason);
    }

    private static Dictionary<string, object?> DeepClone(Dictionary<string, object?> source)
    {
        var clone = new Dictionary<string, object?>(source.Count);
        foreach (var (k, v) in source) clone[k] = DeepCloneValue(v);
        return clone;
    }

    private static object? DeepCloneValue(object? v) => v switch
    {
        Dictionary<string, object?> d => DeepClone(d),
        List<object?> l => new List<object?>(l.ConvertAll(DeepCloneValue)),
        _ => v,
    };
}
