using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Faithful C# port of the Bun Network Storage endpoint expression engine
/// (<c>tools/sbox/endpoint-expression.js</c>). Evaluates <c>{{template}}</c>
/// strings, condition objects, math expressions, and rich aggregate expressions
/// with the exact JavaScript semantics the live data plane relies on
/// (loose <c>==</c> coercion, <c>Number()</c>/<c>String()</c> coercion,
/// <c>Math.round</c> half-up, operator aliases).
///
/// The JS value model maps to <see cref="object"/>?:
/// <list type="bullet">
/// <item><c>null</c> → C# <c>null</c></item>
/// <item><c>undefined</c> → <see cref="JsUndefined.Value"/></item>
/// <item><c>number</c> → <see cref="double"/></item>
/// <item><c>string</c> → <see cref="string"/></item>
/// <item><c>boolean</c> → <see cref="bool"/></item>
/// <item><c>array</c> → <see cref="List{T}"/> of <c>object?</c></item>
/// <item><c>object</c> → <see cref="Dictionary{TKey,TValue}"/> (string → object?), insertion-ordered</item>
/// </list>
///
/// Parity with the JS engine is enforced by EndpointExpressionParityTests, which
/// asserts identical output against a golden oracle generated from the JS engine.
/// </summary>
public static partial class EndpointExpression
{
    private const int MaxTemplateLength = 10_000;
    private const int MaxPathDepth = 10;
    private const int MaxResolveDepth = 8;
    private static readonly string[] ForbiddenPaths = ["__proto__", "constructor", "prototype"];

    // ── Public API ──────────────────────────────────────────────────────────

    public static object? ResolveTemplate(object? template, IDictionary<string, object?> context, IDictionary<string, object?>? aliases = null)
        => ResolveTemplateInternal(template, context, 0, aliases);

    public static object? ResolveDeep(object? value, IDictionary<string, object?> context, int depth = 0, IDictionary<string, object?>? aliases = null)
    {
        if (depth > MaxResolveDepth) throw new ExpressionException("Maximum template nesting depth exceeded.");
        switch (value)
        {
            case string s:
                return ResolveTemplate(s, context, aliases);
            case List<object?> arr:
            {
                var result = new List<object?>(arr.Count);
                foreach (var item in arr) result.Add(ResolveDeep(item, context, depth + 1, aliases));
                return result;
            }
            case Dictionary<string, object?> obj:
            {
                var result = new Dictionary<string, object?>(obj.Count);
                foreach (var (k, v) in obj) result[k] = ResolveDeep(v, context, depth + 1, aliases);
                return result;
            }
            default:
                return value;
        }
    }

    /// <summary>Evaluate a condition check (Dictionary model). Returns the <c>ok</c> boolean.</summary>
    public static bool EvaluateCondition(object? check, IDictionary<string, object?> context)
    {
        if (check is not Dictionary<string, object?> c)
            throw new ExpressionException("Condition check must be an object.");

        if (c.TryGetValue("all", out var allRaw) && allRaw is List<object?> all)
        {
            foreach (var sub in all)
                if (!EvaluateCondition(sub, context)) return false;
            return true;
        }
        if (c.TryGetValue("any", out var anyRaw) && anyRaw is List<object?> any)
        {
            foreach (var sub in any)
                if (EvaluateCondition(sub, context)) return true;
            return false;
        }

        var rawField = Get(c, "field");
        var leftAlias = Get(c, "left");
        var op = Get(c, "op") as string;
        var rawValue = c.TryGetValue("value", out var rv) ? rv : JsUndefined.Value;
        var rightAlias = Get(c, "right");
        var rawExpression = Get(c, "expression") as string;

        var field = !IsNullish(rawField) ? rawField : leftAlias;
        var value = !IsUndefined(rawValue) ? rawValue : rightAlias;
        var expression = !string.IsNullOrWhiteSpace(rawExpression) ? rawExpression.Trim() : null;

        var hasField = field is string fs ? fs.Length > 0 : !IsNullish(field);

        if (expression != null && !hasField)
            return EvaluateExpressionCheck(expression, context);

        if (expression != null && hasField)
        {
            if (string.IsNullOrEmpty(op)) throw new ExpressionException("Condition requires \"op\" when both \"field\" and \"expression\" are used.");
            var l = ResolveFieldForComparison(field, context);
            var r = EvaluateMath(expression, context);
            return Compare(l, NormalizeOp(op), r);
        }

        if (!hasField || string.IsNullOrEmpty(op))
            throw new ExpressionException("Condition requires \"field\" (or \"left\") and \"op\".");

        var left = ResolveFieldForComparison(field, context);
        var normalizedOp = NormalizeOp(op);

        if (normalizedOp == "exists") return !IsNullish(left);
        if (normalizedOp == "not_exists") return IsNullish(left);

        var right = ResolveTemplate(value, context);
        return Compare(left, normalizedOp, right);
    }

    public static double EvaluateMath(string expression, IDictionary<string, object?> context)
    {
        var preprocessed = PreprocessHighLevelFunctions(expression, context, 0);

        var resolved = ReplaceTemplateTokens(preprocessed, token =>
        {
            var resolvedToken = ResolveNestedToken(token, context, 0);
            var val = ResolveSingleToken(resolvedToken, context, null);
            if (IsNullish(val))
                throw new ExpressionException($"Unresolved variable \"{{{{{resolvedToken}}}}}\" in math expression.");
            var num = JsNumber(val);
            if (double.IsNaN(num))
                throw new ExpressionException($"Variable \"{{{{{resolvedToken}}}}}\" resolved to non-numeric value: {JsToString(val)}");
            return JsNumberToString(num);
        });

        if (resolved.Length > 1000)
            throw new ExpressionException("Math expression exceeds maximum length of 1000 characters.");

        string check = resolved;
        foreach (var fn in MathAllowedFunctions)
            check = check.Replace(fn, "");
        if (LetterPattern().IsMatch(check))
            throw new ExpressionException("Math expression contains invalid characters.");

        return ParseMathExpression(resolved);
    }

    public static object? EvaluateExpression(string expr, IDictionary<string, object?> context)
    {
        expr = expr.Trim();
        var parsed = ParseTopLevelFunction(expr);
        if (parsed != null)
            return EvaluateFunctionCall(parsed.Value.Name, parsed.Value.Args, context);

        if (expr.StartsWith("{{", StringComparison.Ordinal) && expr.EndsWith("}}", StringComparison.Ordinal))
        {
            var end = FindTemplateEnd(expr, 0);
            if (end == expr.Length - 2) return ResolveTemplate(expr, context);
        }
        return EvaluateMath(expr, context);
    }

    // ── JS value coercion ────────────────────────────────────────────────────

    public static bool IsUndefined(object? v) => ReferenceEquals(v, JsUndefined.Value);
    public static bool IsNullish(object? v) => v is null || IsUndefined(v);

    /// <summary>Number(v) — JS numeric coercion.</summary>
    public static double JsNumber(object? v)
    {
        switch (v)
        {
            case null: return 0d;
            case double d: return d;
            case float f: return f;
            case long l: return l;
            case int i: return i;
            case short s2: return s2;
            case ushort us: return us;
            case uint u: return u;
            case ulong ul: return ul;
            case decimal dec: return (double)dec;
            case bool b: return b ? 1d : 0d;
            case string s: return StringToNumber(s);
        }
        if (IsUndefined(v)) return double.NaN;
        if (v is List<object?> || v is Dictionary<string, object?>) return StringToNumber(JsToString(v));
        return double.NaN;
    }

    private static double StringToNumber(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return 0d;
        if (s is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (s == "-Infinity") return double.NegativeInfinity;
        try
        {
            if (s.Length > 2 && s[0] == '0')
            {
                var p = char.ToLowerInvariant(s[1]);
                if (p == 'x') return Convert.ToInt64(s[2..], 16);
                if (p == 'o') return Convert.ToInt64(s[2..], 8);
                if (p == 'b') return Convert.ToInt64(s[2..], 2);
            }
        }
        catch { return double.NaN; }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
    }

    /// <summary>String(v) — JS string coercion.</summary>
    public static string JsToString(object? v)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case double d: return JsNumberToString(d);
            case List<object?> arr:
            {
                var sb = new StringBuilder();
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var item = arr[i];
                    if (!IsNullish(item)) sb.Append(JsToString(item));
                }
                return sb.ToString();
            }
            case Dictionary<string, object?>: return "[object Object]";
        }
        if (IsUndefined(v)) return "undefined";
        return v.ToString() ?? "";
    }

    public static string JsNumberToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (double.IsInteger(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>JS truthiness.</summary>
    public static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => !IsUndefined(v),
    };

    /// <summary>JS abstract equality (==).</summary>
    public static bool LooseEquals(object? a, object? b)
    {
        if (IsNullish(a) || IsNullish(b)) return IsNullish(a) && IsNullish(b);
        if (a is double da && b is double db) return da == db;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb) return ba == bb;
        // Stringified booleans: the YAML compiler (YamlDotNet CoreSchema) and
        // legacy writers store `true`/`false` as the STRING "true"/"false", so
        // `check: { field: "{{_hasSecretKey}}", op: "==", value: true }` compiled
        // to "value": "true" and compared bool == "true" — always false
        // (JsNumber("true") is NaN). Match the string form so boolean checks work
        // regardless of the stored representation. Deliberate divergence from JS
        // abstract equality (true == "true" is false in JS), matching the
        // documented structured-check contract.
        if (a is bool baS && b is string bsS && bool.TryParse(bsS, out var bvS)) return baS == bvS;
        if (b is bool bbS && a is string asS && bool.TryParse(asS, out var avS)) return bbS == avS;
        if (a is bool) return LooseEquals(JsNumber(a), b);
        if (b is bool) return LooseEquals(a, JsNumber(b));
        if (a is double && b is string) return JsNumber(a) == JsNumber(b);
        if (a is string && b is double) return JsNumber(a) == JsNumber(b);
        var aObj = a is List<object?> || a is Dictionary<string, object?>;
        var bObj = b is List<object?> || b is Dictionary<string, object?>;
        if (aObj && !bObj) return LooseEquals(ToPrimitive(a), b);
        if (!aObj && bObj) return LooseEquals(a, ToPrimitive(b));
        if (aObj && bObj) return ReferenceEquals(a, b);
        return Equals(a, b);
    }

    private static object? ToPrimitive(object? v) => v switch
    {
        List<object?> => JsToString(v),
        Dictionary<string, object?> => "[object Object]",
        _ => v,
    };

    private static double ToNum(object? v)
    {
        if (v is double d) return d;
        var n = JsNumber(v);
        return double.IsNaN(n) ? 0d : n;
    }

    // ── Patterns ──────────────────────────────────────────────────────────────
    // Every fixed pattern is source-generated with a 100 ms match timeout.

    [GeneratedRegex("[a-zA-Z]", RegexOptions.None, 100)]
    private static partial Regex LetterPattern();

    [GeneratedRegex("^\\{\\{[^}]+\\}\\}$", RegexOptions.None, 100)]
    private static partial Regex SingleTokenPattern();

    [GeneratedRegex("^-?\\d+(\\.\\d+)?$", RegexOptions.None, 100)]
    private static partial Regex NumberLiteralPattern();

    [GeneratedRegex("^([a-zA-Z_][a-zA-Z0-9_]*)\\((.*)\\)$", RegexOptions.Singleline, 100)]
    private static partial Regex FunctionTokenPattern();

    [GeneratedRegex("\\b(filter|values|length|count|sum|avg|any|all|pluck|find|first)\\s*\\(", RegexOptions.None, 100)]
    private static partial Regex AggregateCallPattern();

    [GeneratedRegex("^(floor|ceil|round|min|max|abs|random|pow|log10|clamp|now|nowS|hour|dayOfWeek|diffMs|diffS)\\s*\\(", RegexOptions.None, 100)]
    private static partial Regex MathFunctionPattern();

    [GeneratedRegex("^(\\d+\\.?\\d*(?:[eE][+-]?\\d+)?)", RegexOptions.None, 100)]
    private static partial Regex MathNumberPattern();

    [GeneratedRegex("^([a-zA-Z_][a-zA-Z0-9_]*)\\(", RegexOptions.None, 100)]
    private static partial Regex CallStartPattern();

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*(\\.[a-zA-Z_][a-zA-Z0-9_]*)*$", RegexOptions.None, 100)]
    private static partial Regex PathPattern();

    [GeneratedRegex("^\\s*([a-zA-Z_][a-zA-Z0-9_]*)\\s*=>\\s*(.+)\\s*$", RegexOptions.Singleline, 100)]
    private static partial Regex LambdaPattern();

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*(\\.[a-zA-Z_][a-zA-Z0-9_.]*)*$", RegexOptions.None, 100)]
    private static partial Regex DottedPathPattern();

    [GeneratedRegex("\\b(length|count|sum|avg|any|all|min|max)\\s*\\(", RegexOptions.None, 100)]
    private static partial Regex PreprocessedFunctionPattern();

    /// <summary>
    /// User-supplied matches patterns run with a 50 ms timeout and a bounded compiled-pattern LRU.
    /// </summary>
    private static readonly ExpressionRegexCache DynamicPatterns = new(capacity: 256, timeout: TimeSpan.FromMilliseconds(50));

    // ── Operators ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> OpAliases = new(StringComparer.Ordinal)
    {
        ["=="] = "==", ["!="] = "!=", [">"] = ">", ["<"] = "<", [">="] = ">=", ["<="] = "<=",
        ["contains"] = "contains", ["in"] = "in", ["exists"] = "exists", ["not_exists"] = "not_exists",
        ["eq"] = "==", ["neq"] = "!=", ["ne"] = "!=", ["gt"] = ">", ["lt"] = "<",
        ["gte"] = ">=", ["ge"] = ">=", ["lte"] = "<=", ["le"] = "<=",
        ["includes"] = "contains", ["has"] = "contains",
        ["not_contains"] = "not_contains", ["not_includes"] = "not_contains", ["not_has"] = "not_contains", ["notcontains"] = "not_contains",
        ["not_in"] = "not_in", ["notin"] = "not_in",
        ["not_exist"] = "not_exists", ["notexists"] = "not_exists",
        ["starts_with"] = "starts_with", ["startswith"] = "starts_with",
        ["not_starts_with"] = "not_starts_with", ["notstartswith"] = "not_starts_with",
        ["matches"] = "matches",
    };

    public static string NormalizeOp(string op)
    {
        if (OpAliases.TryGetValue(op, out var canonical)) return canonical;
        throw new ExpressionException($"Unsupported operator: \"{op}\".");
    }

    public static bool Compare(object? left, string op, object? right)
    {
        switch (op)
        {
            case "==": return LooseEquals(left, right);
            case "!=": return !LooseEquals(left, right);
            case ">": return ToNum(left) > ToNum(right);
            case "<": return ToNum(left) < ToNum(right);
            case ">=": return ToNum(left) >= ToNum(right);
            case "<=": return ToNum(left) <= ToNum(right);
            case "contains":
                if (left is List<object?> la) return la.Any(item => LooseEquals(item, right));
                if (left is string ls) return ls.Contains(JsToString(right), StringComparison.Ordinal);
                return false;
            case "in":
                if (right is List<object?> ra) return ra.Any(item => LooseEquals(item, left));
                if (right is string rs) return rs.Contains(JsToString(left), StringComparison.Ordinal);
                return false;
            case "not_contains":
                if (left is List<object?> la2) return !la2.Any(item => LooseEquals(item, right));
                if (left is string ls2) return !ls2.Contains(JsToString(right), StringComparison.Ordinal);
                return true;
            case "not_in":
                if (right is List<object?> ra2) return !ra2.Any(item => LooseEquals(item, left));
                if (right is string rs2) return !rs2.Contains(JsToString(left), StringComparison.Ordinal);
                return true;
            case "starts_with": return left is string sl && sl.StartsWith(JsToString(right), StringComparison.Ordinal);
            case "not_starts_with": return left is not string snl || !snl.StartsWith(JsToString(right), StringComparison.Ordinal);
            case "matches": return DynamicPatterns.Match(JsToString(right), JsToString(left)).Success;
            default: throw new ExpressionException($"Unsupported operator: \"{op}\".");
        }
    }

    public static object? ResolveFieldForComparison(object? field, IDictionary<string, object?> context)
    {
        if (IsNullish(field) || (field is string fe && fe.Length == 0)) return JsUndefined.Value;
        var fieldStr = JsToString(field);
        string fieldTemplate;
        if (fieldStr.Contains("{{", StringComparison.Ordinal))
        {
            var isSingleToken = SingleTokenPattern().IsMatch(fieldStr);
            if (isSingleToken)
            {
                fieldTemplate = fieldStr;
            }
            else
            {
                var resolvedPath = ResolveTemplate(fieldStr, context);
                fieldTemplate = resolvedPath is string rp && !rp.Contains("{{", StringComparison.Ordinal)
                    ? $"{{{{{rp}}}}}"
                    : fieldStr;
            }
        }
        else
        {
            fieldTemplate = $"{{{{{fieldStr}}}}}";
        }
        return ResolveTemplate(fieldTemplate, context);
    }

    // ── Template resolution ────────────────────────────────────────────────────

    private static object? ResolveTemplateInternal(object? template, IDictionary<string, object?> context, int depth, IDictionary<string, object?>? aliases)
    {
        if (template is not string s) return template;
        if (s.Length > MaxTemplateLength) throw new ExpressionException("Template string exceeds maximum length.");
        if (depth > MaxResolveDepth) throw new ExpressionException("Maximum template nesting depth exceeded.");
        if (!s.Contains("{{", StringComparison.Ordinal)) return s;

        var singleToken = GetSingleTemplateToken(s);
        if (singleToken != null)
            return ResolveSingleToken(ResolveNestedToken(singleToken, context, depth), context, aliases);

        return ReplaceTemplateTokens(s, token =>
        {
            var val = ResolveSingleToken(ResolveNestedToken(token, context, depth), context, aliases);
            return IsNullish(val) ? "" : JsToString(val);
        });
    }

    private static object? ResolveSingleToken(string token, IDictionary<string, object?> context, IDictionary<string, object?>? aliases)
    {
        var (corePath, hasDefault, defaultVal) = ParseTokenDefault(token);
        var coreToken = corePath;

        var effectiveAliases = aliases;
        if (effectiveAliases == null && context.TryGetValue("_aliases", out var al) && al is IDictionary<string, object?> alD)
            effectiveAliases = alD;
        if (effectiveAliases != null)
        {
            var firstDot = coreToken.IndexOf('.');
            var rootKey = firstDot >= 0 ? coreToken[..firstDot] : coreToken;
            if (effectiveAliases.TryGetValue(rootKey, out var aliasTargetObj) && !IsUndefined(aliasTargetObj))
            {
                var aliasTarget = JsToString(aliasTargetObj);
                var rest = firstDot >= 0 ? coreToken[(firstDot + 1)..] : "";
                var expandedPath = rest.Length > 0 ? $"{aliasTarget}.{rest}" : aliasTarget;
                var expandedResult = ResolveSingleToken(expandedPath, context, aliases);
                if (!IsNullish(expandedResult)) return expandedResult;
                if (hasDefault) return ResolveDefaultValue(defaultVal, context);
                return expandedResult;
            }
        }

        var negate = coreToken.StartsWith('-');
        var path = negate ? coreToken[1..] : coreToken;

        if (path.Length == 0)
            return hasDefault ? ResolveDefaultValue(defaultVal, context) : JsUndefined.Value;

        var helper = ResolveHelperToken(path, context);
        var val = helper.Matched ? helper.Value : GetNestedValue(context, path);

        if (IsNullish(val) && hasDefault)
            val = ResolveDefaultValue(defaultVal, context);

        if (negate)
        {
            if (IsNullish(val)) return 0d;
            if (val is double dn) return -dn;
            if (val is string vs)
            {
                var num = JsNumber(vs);
                if (!double.IsNaN(num) && vs.Trim().Length > 0) return -num;
            }
            throw new ExpressionException($"Cannot negate non-numeric value at \"{path}\".");
        }
        return val;
    }

    private static string ResolveNestedToken(string token, IDictionary<string, object?> context, int depth)
    {
        var trimmed = token.Trim();
        if (!trimmed.Contains("{{", StringComparison.Ordinal)) return trimmed;
        return JsToString(ResolveTemplateInternal(trimmed, context, depth + 1, null)).Trim();
    }

    private static string? GetSingleTemplateToken(string template)
    {
        if (!template.StartsWith("{{", StringComparison.Ordinal)) return null;
        var end = FindTemplateEnd(template, 0);
        if (end < 0 || end != template.Length - 2) return null;
        return template.Substring(2, end - 2);
    }

    private static string ReplaceTemplateTokens(string template, Func<string, string> replacer)
    {
        var output = new StringBuilder();
        var cursor = 0;
        while (cursor < template.Length)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0) { output.Append(template, cursor, template.Length - cursor); break; }
            output.Append(template, cursor, start - cursor);
            var end = FindTemplateEnd(template, start);
            if (end < 0) { output.Append(template, start, template.Length - start); break; }
            output.Append(replacer(template.Substring(start + 2, end - (start + 2))));
            cursor = end + 2;
        }
        return output.ToString();
    }

    private static int FindTemplateEnd(string template, int start)
    {
        var depth = 1;
        var cursor = start + 2;
        while (cursor < template.Length)
        {
            var nextOpen = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            var nextClose = template.IndexOf("}}", cursor, StringComparison.Ordinal);
            if (nextClose < 0) return -1;
            if (nextOpen >= 0 && nextOpen < nextClose) { depth++; cursor = nextOpen + 2; continue; }
            depth--;
            if (depth == 0) return nextClose;
            cursor = nextClose + 2;
        }
        return -1;
    }

    public static object? GetNestedValue(object? obj, string? dotPath)
    {
        if (dotPath == null) return JsUndefined.Value;
        var parts = dotPath.Split('.');
        if (parts.Length > MaxPathDepth) throw new ExpressionException($"Path depth exceeds maximum of {MaxPathDepth}.");
        foreach (var part in parts)
            if (Array.IndexOf(ForbiddenPaths, part) >= 0) throw new ExpressionException($"Forbidden path segment: \"{part}\".");

        var current = obj;
        foreach (var part in parts)
        {
            // JS checks the PARENT before indexing: a non-object parent yields undefined,
            // but a present key whose value is null returns null.
            if (current is Dictionary<string, object?> d)
                current = d.TryGetValue(part, out var nv) ? nv : JsUndefined.Value;
            else if (current is List<object?> arr)
                current = part == "length"
                    ? (double)arr.Count
                    : int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < arr.Count ? arr[idx] : JsUndefined.Value;
            else
                return JsUndefined.Value;
        }
        return current;
    }

    private static (string Path, bool HasDefault, string DefaultVal) ParseTokenDefault(string token)
    {
        var pipeIdx = token.IndexOf('|');
        if (pipeIdx < 0) return (token, false, "");
        var path = token[..pipeIdx].Trim();
        var defaultVal = token[(pipeIdx + 1)..].Trim();
        if (path.Length == 0) return (token, false, "");
        return (path, true, defaultVal);
    }

    private static object? ResolveDefaultValue(string defaultExpr, IDictionary<string, object?> context)
    {
        if (string.IsNullOrEmpty(defaultExpr)) return JsUndefined.Value;
        var trimmed = defaultExpr.Trim();
        if (NumberLiteralPattern().IsMatch(trimmed)) return double.Parse(trimmed, CultureInfo.InvariantCulture);
        if (trimmed == "true") return true;
        if (trimmed == "false") return false;
        if (trimmed == "null") return null;
        if (trimmed.Contains("{{", StringComparison.Ordinal)) return ResolveTemplate(trimmed, context);
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            return trimmed[1..^1];
        return trimmed;
    }

    // ── Helper tokens: coalesce/default/num/get/min/max/count/sum/avg/any/all/find/first/pluck ──

    private static (bool Matched, object? Value) ResolveHelperToken(string token, IDictionary<string, object?> context)
    {
        var match = FunctionTokenPattern().Match(token);
        if (!match.Success) return (false, null);
        var name = match.Groups[1].Value;
        var rawArgs = match.Groups[2].Value;
        var rawArgList = SplitHelperArgs(rawArgs);

        switch (name)
        {
            case "matches":
                if (rawArgList.Count != 2) throw new ExpressionException("matches() requires an input and a pattern.");
                return (true, DynamicPatterns.Match(
                    JsToString(ResolveHelperArg(rawArgList[1], context, false)),
                    JsToString(ResolveHelperArg(rawArgList[0], context, false))).Success);
            case "coalesce":
            case "default":
            {
                var args = rawArgList.Select(a => ResolveHelperArg(a, context, false)).ToList();
                if (args.Count < 2) throw new ExpressionException($"{name}() requires at least 2 arguments.");
                foreach (var arg in args)
                    if (!IsNullish(arg) && !(arg is string es && es.Length == 0)) return (true, arg);
                return (true, args[^1]);
            }
            case "num":
            {
                var args = rawArgList.Select(a => ResolveHelperArg(a, context, false)).ToList();
                if (args.Count is < 1 or > 2) throw new ExpressionException("num() requires value and optional default.");
                var fallback = args.Count == 2 ? JsNumber(args[1]) : 0d;
                var n = JsNumber(args[0]);
                return (true, double.IsFinite(n) ? n : (double.IsFinite(fallback) ? fallback : 0d));
            }
            case "get":
            {
                var args = rawArgList.Select((a, i) => ResolveHelperArg(a, context, i > 0 && i < rawArgList.Count - 1)).ToList();
                if (args.Count < 3) throw new ExpressionException("get() requires root, at least one path segment, and a default value.");
                var root = args[0];
                var fallback = args[^1];
                var path = string.Join(".", args.Skip(1).Take(args.Count - 2).Select(JsToString));
                var value = GetNestedValue(root, path);
                return (true, IsNullish(value) ? fallback : value);
            }
            case "min":
            case "max":
            {
                var args = rawArgList.Select(a => ResolveHelperArg(a, context, false)).ToList();
                if (args.Count == 1 && args[0] is List<object?> only)
                {
                    if (only.Count == 0) throw new ExpressionException($"{name}() requires a non-empty array.");
                    var nums0 = only.Select(JsNumber).ToList();
                    if (nums0.Any(x => !double.IsFinite(x))) throw new ExpressionException($"{name}() array contains non-numeric values.");
                    return (true, name == "min" ? nums0.Min() : nums0.Max());
                }
                if (args.Count < 2) throw new ExpressionException($"{name}() requires at least 2 numeric arguments or one numeric array.");
                var nums = args.Select(JsNumber).ToList();
                if (nums.Any(x => !double.IsFinite(x))) throw new ExpressionException($"{name}() requires numeric arguments.");
                return (true, name == "min" ? nums.Min() : nums.Max());
            }
            case "count": return (true, EvaluateCountExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "sum": return (true, EvaluateSumExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "avg": return (true, EvaluateAvgExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "any": return (true, EvaluateAnyExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "all": return (true, EvaluateAllExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "find":
            case "first": return (true, EvaluateFirstExpr(rawArgList[0], Arg(rawArgList, 1), context));
            case "pluck":
                if (rawArgList.Count != 2) throw new ExpressionException("pluck() requires collection and path/lambda selector.");
                return (true, EvaluatePluckExpr(rawArgList[0], rawArgList[1], context));
            default: return (false, null);
        }
    }

    private static string? Arg(List<string> list, int i) => i < list.Count ? list[i] : null;

    private static List<string> SplitHelperArgs(string rawArgs)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var depth = 0;
        for (var i = 0; i < rawArgs.Length; i++)
        {
            var ch = rawArgs[i];
            if (quote != null)
            {
                current.Append(ch);
                if (ch == quote && (i == 0 || rawArgs[i - 1] != '\\')) quote = null;
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; current.Append(ch); continue; }
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (ch == ',' && depth == 0) { args.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(ch);
        }
        if (current.ToString().Trim().Length > 0 || rawArgs.Trim().Length > 0) args.Add(current.ToString().Trim());
        return args;
    }

    private static object? ResolveHelperArg(string arg, IDictionary<string, object?> context, bool missingAsLiteral)
    {
        if (arg.Length == 0) return "";
        if ((arg.StartsWith('"') && arg.EndsWith('"')) || (arg.StartsWith('\'') && arg.EndsWith('\''))) return arg[1..^1];
        if (arg == "true") return true;
        if (arg == "false") return false;
        if (arg == "null") return null;
        if (NumberLiteralPattern().IsMatch(arg)) return double.Parse(arg, CultureInfo.InvariantCulture);
        var helper = ResolveHelperToken(arg, context);
        if (helper.Matched) return helper.Value;
        var value = GetNestedValue(context, arg);
        return IsUndefined(value) && missingAsLiteral ? arg : value;
    }

    // ── Math preprocessing + parser ────────────────────────────────────────────

    private static readonly string[] MathAllowedFunctions =
        ["floor", "ceil", "round", "min", "max", "abs", "random", "pow", "log10", "clamp", "dayOfWeek", "diffMs", "diffS", "nowS", "hour", "now"];

    private static string PreprocessHighLevelFunctions(string expr, IDictionary<string, object?> context, int depth)
    {
        if (depth > 10) throw new ExpressionException("Maximum function nesting depth exceeded.");
        var result = expr;
        foreach (var funcName in new[] { "length", "count", "sum", "avg", "any", "all" })
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                var m = FindFunctionCall(result, funcName);
                if (m != null)
                {
                    var fullCall = $"{funcName}({m.Value.ArgsStr})";
                    var value = EvaluateExpression(fullCall, context);
                    var numValue = JsNumber(value);
                    if (double.IsNaN(numValue)) throw new ExpressionException($"{funcName}() must return a numeric value.");
                    result = result[..m.Value.Start] + JsNumberToString(numValue) + result[m.Value.End..];
                    changed = true;
                }
            }
        }
        foreach (var funcName in new[] { "min", "max" })
        {
            FunctionCall? m;
            while ((m = FindFunctionCall(result, funcName)) != null)
            {
                var argsStr = m.Value.ArgsStr.Trim();
                if (!argsStr.Contains(',') || HasNestedComma(argsStr))
                {
                    if (argsStr.Contains("{{", StringComparison.Ordinal) ||
                        AggregateCallPattern().IsMatch(argsStr))
                    {
                        var fullCall = $"{funcName}({argsStr})";
                        var value = EvaluateExpression(fullCall, context);
                        var numValue = JsNumber(value);
                        if (double.IsNaN(numValue)) throw new ExpressionException($"{funcName}() must return a numeric value for use in math expressions.");
                        result = result[..m.Value.Start] + JsNumberToString(numValue) + result[m.Value.End..];
                        continue;
                    }
                }
                break;
            }
        }
        return result;
    }

    private static bool HasNestedComma(string str)
    {
        var depth = 0;
        foreach (var ch in str)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0) return false;
        }
        return str.Contains(',');
    }

    private readonly record struct FunctionCall(int Start, int End, string ArgsStr);

    private static FunctionCall? FindFunctionCall(string expr, string funcName)
    {
        Match? match = null;
        foreach (Match candidate in PreprocessedFunctionPattern().Matches(expr))
        {
            if (candidate.Groups[1].Value == funcName) { match = candidate; break; }
        }
        if (match is null) return null;
        var start = match.Index;
        var openParen = start + match.Length - 1;
        var depth = 1;
        var i = openParen + 1;
        char? quote = null;
        while (i < expr.Length && depth > 0)
        {
            var ch = expr[i];
            if (quote != null) { if (ch == quote && expr[i - 1] != '\\') quote = null; }
            else if (ch is '\'' or '"') quote = ch;
            else if (ch == '(') depth++;
            else if (ch == ')') depth--;
            i++;
        }
        if (depth != 0) return null;
        return new FunctionCall(start, i, expr.Substring(openParen + 1, i - 1 - (openParen + 1)));
    }

    private static double ParseMathExpression(string input)
    {
        var pos = 0;

        void SkipWs() { while (pos < input.Length && char.IsWhiteSpace(input[pos])) pos++; }

        double ParseExpr()
        {
            var left = ParseTerm();
            SkipWs();
            while (pos < input.Length && (input[pos] == '+' || input[pos] == '-'))
            {
                var op = input[pos++];
                var right = ParseTerm();
                left = op == '+' ? left + right : left - right;
                SkipWs();
            }
            return left;
        }

        double ParseTerm()
        {
            var left = ParseUnary();
            SkipWs();
            while (pos < input.Length && (input[pos] == '*' || input[pos] == '/' || input[pos] == '%'))
            {
                var op = input[pos++];
                var right = ParseUnary();
                left = op == '*' ? left * right : op == '/' ? left / right : left % right;
                SkipWs();
            }
            return left;
        }

        double ParseUnary()
        {
            SkipWs();
            if (pos < input.Length && input[pos] == '-') { pos++; return -ParseUnary(); }
            return ParsePrimary();
        }

        List<double> ParseArgs()
        {
            var args = new List<double> { ParseExpr() };
            SkipWs();
            while (pos < input.Length && input[pos] == ',') { pos++; args.Add(ParseExpr()); SkipWs(); }
            return args;
        }

        double ParsePrimary()
        {
            SkipWs();
            var rest = input[pos..];
            var funcMatch = MathFunctionPattern().Match(rest);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                pos += funcMatch.Length;
                SkipWs();
                var args = new List<double>();
                if (pos < input.Length && input[pos] == ')') { /* zero-arg */ }
                else args = ParseArgs();
                SkipWs();
                if (pos >= input.Length || input[pos] != ')') throw new ExpressionException($"Expected closing ')' for function \"{funcName}\".");
                pos++;
                return ApplyMathFunc(funcName, args);
            }
            if (pos < input.Length && input[pos] == '(')
            {
                pos++;
                var val = ParseExpr();
                SkipWs();
                if (pos >= input.Length || input[pos] != ')') throw new ExpressionException("Mismatched parentheses in math expression.");
                pos++;
                return val;
            }
            var numMatch = MathNumberPattern().Match(input[pos..]);
            if (numMatch.Success)
            {
                pos += numMatch.Length;
                return double.Parse(numMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            throw new ExpressionException($"Unexpected character at position {pos} in math expression.");
        }

        var result = ParseExpr();
        SkipWs();
        if (pos < input.Length) throw new ExpressionException($"Unexpected trailing characters in math expression: \"{input[pos..]}\"");
        return result;
    }

    private static double ApplyMathFunc(string name, List<double> args)
    {
        switch (name)
        {
            case "floor": Require(args, 1, name); return Math.Floor(args[0]);
            case "ceil": Require(args, 1, name); return Math.Ceiling(args[0]);
            case "round": Require(args, 1, name); return JsRound(args[0]);
            case "min": if (args.Count < 2) throw new ExpressionException("min() requires at least 2 arguments."); return JsMin(args);
            case "max": if (args.Count < 2) throw new ExpressionException("max() requires at least 2 arguments."); return JsMax(args);
            case "abs": Require(args, 1, name); return Math.Abs(args[0]);
            case "pow": Require(args, 2, name); return Math.Pow(args[0], args[1]);
            case "log10":
                Require(args, 1, name);
                if (!double.IsFinite(args[0]) || args[0] <= 0) throw new ExpressionException("log10() requires a positive finite number.");
                return Math.Log10(args[0]);
            case "clamp": Require(args, 3, name); return Math.Max(args[1], Math.Min(args[2], args[0]));
            case "diffMs": Require(args, 2, name); return args[0] - args[1];
            case "diffS": Require(args, 2, name); return (args[0] - args[1]) / 1000d;
            default: throw new ExpressionException($"Unknown function: \"{name}\".");
        }
    }

    private static void Require(List<double> args, int n, string name)
    {
        if (args.Count != n) throw new ExpressionException($"{name}() requires exactly {n} argument(s).");
    }

    // JS Math.round: half rounds toward +Infinity (floor(x + 0.5)).
    private static double JsRound(double x) => Math.Floor(x + 0.5);

    // JS Math.min/max: any NaN → NaN.
    private static double JsMin(IEnumerable<double> xs)
    {
        var min = double.PositiveInfinity;
        foreach (var x in xs) { if (double.IsNaN(x)) return double.NaN; if (x < min) min = x; }
        return min;
    }

    private static double JsMax(IEnumerable<double> xs)
    {
        var max = double.NegativeInfinity;
        foreach (var x in xs) { if (double.IsNaN(x)) return double.NaN; if (x > max) max = x; }
        return max;
    }

    // ── Rich expressions: function dispatch + aggregates ───────────────────────

    private readonly record struct TopLevelFunction(string Name, List<string> Args);

    private static TopLevelFunction? ParseTopLevelFunction(string expr)
    {
        var match = CallStartPattern().Match(expr);
        if (!match.Success) return null;
        var name = match.Groups[1].Value;
        var rest = expr[(match.Index + name.Length)..];
        var content = ExtractParenContent(rest, 0);
        if (content == null) return null;
        if (content.Value.End != rest.Length - 1) return null;
        return new TopLevelFunction(name, SplitTopLevelArgs(content.Value.Value));
    }

    private readonly record struct ParenContent(string Value, int End);

    private static ParenContent? ExtractParenContent(string str, int startIdx)
    {
        if (startIdx >= str.Length || str[startIdx] != '(') return null;
        var depth = 0;
        for (var i = startIdx; i < str.Length; i++)
        {
            var ch = str[i];
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) return new ParenContent(str.Substring(startIdx + 1, i - (startIdx + 1)), i);
            }
        }
        return null;
    }

    private static List<string> SplitTopLevelArgs(string str)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char? quote = null;
        var i = 0;
        while (i < str.Length)
        {
            var ch = str[i];
            if (ch == '{' && i + 1 < str.Length && str[i + 1] == '{' && quote == null)
            {
                var end = FindTemplateEnd(str, i);
                if (end < 0) { current.Append(ch); i++; continue; }
                current.Append(str, i, end + 2 - i);
                i = end + 2;
                continue;
            }
            if (quote != null) { current.Append(ch); if (ch == quote && (i == 0 || str[i - 1] != '\\')) quote = null; i++; continue; }
            if (ch is '\'' or '"') { quote = ch; current.Append(ch); i++; continue; }
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0) { args.Add(current.ToString().Trim()); current.Clear(); i++; continue; }
            current.Append(ch);
            i++;
        }
        if (current.ToString().Trim().Length > 0 || str.Trim().Length > 0) args.Add(current.ToString().Trim());
        return args;
    }

    private static object? EvaluateFunctionCall(string name, List<string> args, IDictionary<string, object?> context)
    {
        switch (name)
        {
            case "length": Need(args, 1, name); return EvaluateLengthExpr(args[0], context);
            case "count": NeedRange(args, 1, 2, name); return EvaluateCountExpr(args[0], Arg(args, 1), context);
            case "sum": NeedRange(args, 1, 2, name); return EvaluateSumExpr(args[0], Arg(args, 1), context);
            case "avg": NeedRange(args, 1, 2, name); return EvaluateAvgExpr(args[0], Arg(args, 1), context);
            case "any": NeedRange(args, 1, 2, name); return EvaluateAnyExpr(args[0], Arg(args, 1), context);
            case "all": NeedRange(args, 1, 2, name); return EvaluateAllExpr(args[0], Arg(args, 1), context);
            case "find": NeedRange(args, 1, 2, name); return EvaluateFirstExpr(args[0], Arg(args, 1), context);
            case "first": NeedRange(args, 1, 2, name); return EvaluateFirstExpr(args[0], Arg(args, 1), context);
            case "pluck": Need(args, 2, name); return EvaluatePluckExpr(args[0], args[1], context);
            case "filter": Need(args, 2, name); return EvaluateFilterExpr(args[0], args[1], context);
            case "values": Need(args, 1, name); return EvaluateValuesExpr(args[0], context);
            case "min":
                if (args.Count == 1) return EvaluateMinMaxArrayExpr(args[0], context, isMin: true);
                return EvaluateMath($"{name}({string.Join(",", args)})", context);
            case "max":
                if (args.Count == 1) return EvaluateMinMaxArrayExpr(args[0], context, isMin: false);
                return EvaluateMath($"{name}({string.Join(",", args)})", context);
            default:
                return EvaluateMath($"{name}({string.Join(",", args)})", context);
        }
    }

    private static void Need(List<string> args, int n, string name)
    {
        if (args.Count != n) throw new ExpressionException($"{name}() requires exactly {n} argument(s).");
    }

    private static void NeedRange(List<string> args, int lo, int hi, string name)
    {
        if (args.Count < lo || args.Count > hi) throw new ExpressionException($"{name}() requires {lo}..{hi} arguments.");
    }

    private static object? EvaluateLengthExpr(string argExpr, IDictionary<string, object?> context)
    {
        var value = EvaluateExpression(argExpr, context);
        if (IsNullish(value)) return 0d;
        return value switch
        {
            string s => (double)s.Length,
            List<object?> a => (double)a.Count,
            Dictionary<string, object?> o => (double)o.Count,
            _ => throw new ExpressionException("length() requires an array, string, or object."),
        };
    }

    private static object? EvaluateFilterExpr(string arrayExpr, string lambdaStr, IDictionary<string, object?> context)
    {
        var array = EvaluateExpression(arrayExpr, context);
        if (IsNullish(array)) return new List<object?>();
        if (array is not List<object?> list) throw new ExpressionException("filter() first argument must resolve to an array.");

        var lambda = ParseLambda(lambdaStr.Trim());
        var resolvedBody = lambda.Body.Contains("{{", StringComparison.Ordinal)
            ? ReplaceTemplateTokens(lambda.Body, token =>
            {
                var resolvedToken = ResolveNestedToken(token, context, 0);
                var val = ResolveSingleToken(resolvedToken, context, null);
                return IsNullish(val) ? "" : JsToString(val);
            })
            : lambda.Body;

        var output = new List<object?>();
        foreach (var item in list)
        {
            var itemContext = Extend(context, lambda.Param, item);
            if (IsTruthy(EvaluateBooleanExpr(resolvedBody, itemContext))) output.Add(item);
        }
        return output;
    }

    private static object? EvaluateValuesExpr(string objExpr, IDictionary<string, object?> context)
    {
        var value = EvaluateExpression(objExpr, context);
        if (IsNullish(value)) return new List<object?>();
        if (value is List<object?> a) return a;
        if (value is Dictionary<string, object?> o) return o.Values.ToList();
        throw new ExpressionException("values() requires an object.");
    }

    private static List<object?> CollectionValues(object? value, string functionName)
    {
        if (IsNullish(value)) return new List<object?>();
        if (value is List<object?> a) return a;
        if (value is Dictionary<string, object?> o) return o.Values.ToList();
        throw new ExpressionException($"{functionName}() requires an array or object.");
    }

    private static object? EvaluateCollectionExpression(string collectionExpr, IDictionary<string, object?> context)
    {
        var expr = collectionExpr.Trim();
        var unquoted = StripQuotes(expr);
        if (PathPattern().IsMatch(unquoted))
        {
            var value = ResolveNestedPath(unquoted, context);
            if (!IsUndefined(value)) return value;
        }
        return EvaluateExpression(expr, context);
    }

    private static double EvaluateCountExpr(string collectionExpr, string? predicateExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "count");
        if (string.IsNullOrWhiteSpace(predicateExpr)) return items.Count;
        return items.Count(item => EvaluateAggregatePredicate(item, predicateExpr, context));
    }

    private static double EvaluateSumExpr(string collectionExpr, string? selectorExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "sum");
        var total = 0d;
        for (var i = 0; i < items.Count; i++) total += NumericAggregateValue(items[i], selectorExpr, context, "sum", i);
        return total;
    }

    private static double EvaluateAvgExpr(string collectionExpr, string? selectorExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "avg");
        if (items.Count == 0) return 0d;
        var total = 0d;
        for (var i = 0; i < items.Count; i++) total += NumericAggregateValue(items[i], selectorExpr, context, "avg", i);
        return total / items.Count;
    }

    private static bool EvaluateAnyExpr(string collectionExpr, string? predicateExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "any");
        if (string.IsNullOrWhiteSpace(predicateExpr)) return items.Any(IsTruthy);
        return items.Any(item => EvaluateAggregatePredicate(item, predicateExpr, context));
    }

    private static bool EvaluateAllExpr(string collectionExpr, string? predicateExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "all");
        if (string.IsNullOrWhiteSpace(predicateExpr)) return items.All(IsTruthy);
        return items.All(item => EvaluateAggregatePredicate(item, predicateExpr, context));
    }

    private static object? EvaluateFirstExpr(string collectionExpr, string? predicateExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "first");
        if (string.IsNullOrWhiteSpace(predicateExpr)) return items.Count > 0 ? items[0] : null;
        foreach (var item in items)
            if (EvaluateAggregatePredicate(item, predicateExpr, context)) return item;
        return null;
    }

    private static object? EvaluatePluckExpr(string collectionExpr, string selectorExpr, IDictionary<string, object?> context)
    {
        var items = CollectionValues(EvaluateCollectionExpression(collectionExpr, context), "pluck");
        var output = new List<object?>(items.Count);
        foreach (var item in items)
        {
            var value = EvaluateAggregateSelector(item, selectorExpr, context);
            output.Add(IsUndefined(value) ? null : value);
        }
        return output;
    }

    private static double NumericAggregateValue(object? item, string? selectorExpr, IDictionary<string, object?> context, string functionName, int index)
    {
        var value = string.IsNullOrWhiteSpace(selectorExpr) ? item : EvaluateAggregateSelector(item, selectorExpr, context);
        if (IsNullish(value) || (value is string es && es.Length == 0)) return 0d;
        var num = JsNumber(value);
        if (!double.IsFinite(num)) throw new ExpressionException($"{functionName}() selected non-numeric value at index {index}.");
        return num;
    }

    private static bool EvaluateAggregatePredicate(object? item, string predicateExpr, IDictionary<string, object?> context)
    {
        var expr = predicateExpr.Trim();
        var lambda = TryParseLambda(expr);
        if (lambda != null)
            return IsTruthy(EvaluateBooleanExpr(lambda.Value.Body, Extend(context, lambda.Value.Param, item)));
        return IsTruthy(EvaluateAggregateSelector(item, expr, context));
    }

    private static object? EvaluateAggregateSelector(object? item, string selectorExpr, IDictionary<string, object?> context)
    {
        var expr = selectorExpr.Trim();
        var lambda = TryParseLambda(expr);
        if (lambda != null)
            return EvaluateAggregateSelector(item, lambda.Value.Body, Extend(context, lambda.Value.Param, item));

        var unquoted = StripQuotes(expr);
        var selectorContext = Extend(context, "item", item);

        if (PathPattern().IsMatch(unquoted))
        {
            if (unquoted.StartsWith("item.", StringComparison.Ordinal)) return ResolveNestedPath(unquoted, selectorContext);
            var itemValue = GetNestedValue(item, unquoted);
            if (!IsUndefined(itemValue)) return itemValue;
            var ci = GetNestedValueCaseInsensitive(item, unquoted);
            if (!IsUndefined(ci)) return ci;
            return ResolveNestedPath(unquoted, selectorContext);
        }

        if (expr.Contains("{{", StringComparison.Ordinal))
        {
            var singleToken = GetSingleTemplateToken(expr);
            if (singleToken != null) return ResolveTemplate(expr, selectorContext);
            try { return EvaluateMath(expr, selectorContext); }
            catch { return ResolveTemplate(expr, selectorContext); }
        }

        return EvaluateExpression(expr, selectorContext);
    }

    private static string StripQuotes(string value)
    {
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            return value[1..^1];
        return value;
    }

    private static object? GetNestedValueCaseInsensitive(object? obj, string dotPath)
    {
        var parts = dotPath.Split('.');
        if (parts.Length > MaxPathDepth) throw new ExpressionException($"Path depth exceeds maximum of {MaxPathDepth}.");
        var current = obj;
        foreach (var part in parts)
        {
            if (Array.IndexOf(ForbiddenPaths, part) >= 0) throw new ExpressionException($"Forbidden path segment: \"{part}\".");
            if (current is not Dictionary<string, object?> d) return JsUndefined.Value;
            var key = d.Keys.FirstOrDefault(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));
            if (key == null) return JsUndefined.Value;
            current = d[key];
        }
        return current;
    }

    private static object? EvaluateMinMaxArrayExpr(string argExpr, IDictionary<string, object?> context, bool isMin)
    {
        var value = EvaluateExpression(argExpr, context);
        var label = isMin ? "min" : "max";
        if (IsNullish(value)) throw new ExpressionException($"{label}() requires a non-null argument.");
        if (value is double d) return d;
        if (value is not List<object?> arr)
        {
            var n = JsNumber(value);
            if (!double.IsNaN(n)) return n;
            throw new ExpressionException($"{label}() requires an array or number.");
        }
        if (arr.Count == 0) throw new ExpressionException($"{label}() requires a non-empty array.");
        var nums = arr.Select(v =>
        {
            var n = JsNumber(v);
            if (double.IsNaN(n)) throw new ExpressionException($"{label}() array contains non-numeric value.");
            return n;
        }).ToList();
        return isMin ? nums.Min() : nums.Max();
    }

    private readonly record struct Lambda(string Param, string Body);

    private static Lambda ParseLambda(string lambdaStr)
    {
        var match = LambdaPattern().Match(lambdaStr);
        if (!match.Success) throw new ExpressionException($"Invalid lambda expression: \"{lambdaStr}\". Expected \"param => body\".");
        return new Lambda(match.Groups[1].Value, match.Groups[2].Value.Trim());
    }

    private static Lambda? TryParseLambda(string lambdaStr)
    {
        try { return ParseLambda(lambdaStr); }
        catch { return null; }
    }

    // ── Boolean expression parser ──────────────────────────────────────────────

    private static object? EvaluateBooleanExpr(string expr, IDictionary<string, object?> context)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return false;
        return EvaluateOr(expr, context);
    }

    private static object? EvaluateOr(string expr, IDictionary<string, object?> context)
        => ParseBinary(expr, context, ["||"]);

    private static object? ParseBinary(string expr, IDictionary<string, object?> context, string[] ops)
    {
        // Mirrors the JS recursive grammar via staged operator sets.
        var foundOp = (string?)null;
        var foundIdx = -1;
        var maxIdx = -1;
        var depth = 0;
        char? quote = null;
        var templateDepth = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            var ch = expr[i];
            if (templateDepth > 0)
            {
                if (ch == '{' && i + 1 < expr.Length && expr[i + 1] == '{') templateDepth++;
                if (ch == '}' && i + 1 < expr.Length && expr[i + 1] == '}') { templateDepth--; i++; }
                continue;
            }
            if (ch == '{' && i + 1 < expr.Length && expr[i + 1] == '{') { templateDepth++; continue; }
            if (quote != null) { if (ch == quote && (i == 0 || expr[i - 1] != '\\')) quote = null; continue; }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth == 0)
            {
                foreach (var op in ops)
                {
                    if (i + op.Length <= expr.Length && expr.Substring(i, op.Length) == op && i > maxIdx)
                    {
                        maxIdx = i; foundOp = op; foundIdx = i;
                    }
                }
            }
        }

        if (foundOp == null)
            return NextStage(ops, expr, context);

        var leftExpr = expr[..foundIdx].Trim();
        var rightExpr = expr[(foundIdx + foundOp.Length)..].Trim();
        if (leftExpr.Length == 0) throw new ExpressionException($"Missing left operand before \"{foundOp}\".");
        if (rightExpr.Length == 0) throw new ExpressionException($"Missing right operand after \"{foundOp}\".");

        switch (foundOp)
        {
            case "||": return IsTruthy(EvaluateBooleanExpr(leftExpr, context)) || IsTruthy(EvaluateBooleanExpr(rightExpr, context));
            case "&&": return IsTruthy(EvaluateBooleanExpr(leftExpr, context)) && IsTruthy(EvaluateBooleanExpr(rightExpr, context));
        }

        var left = EvaluatePrimary(leftExpr, context);
        var right = EvaluatePrimary(rightExpr, context);
        return foundOp switch
        {
            "==" => LooseEquals(left, right),
            "!=" => !LooseEquals(left, right),
            ">" => JsNumber(left) > JsNumber(right),
            "<" => JsNumber(left) < JsNumber(right),
            ">=" => JsNumber(left) >= JsNumber(right),
            "<=" => JsNumber(left) <= JsNumber(right),
            _ => throw new ExpressionException($"Unknown operator \"{foundOp}\"."),
        };
    }

    private static object? NextStage(string[] currentOps, string expr, IDictionary<string, object?> context)
    {
        // Grammar order: || -> && -> ! -> ==/!= -> >=/<=/>/< -> primary
        if (currentOps.Length == 1 && currentOps[0] == "||") return ParseBinary(expr, context, ["&&"]);
        if (currentOps.Length == 1 && currentOps[0] == "&&") return EvaluateNot(expr, context);
        if (currentOps.Length == 2 && currentOps[0] == "==") return ParseBinary(expr, context, [">=", "<=", ">", "<"]);
        return EvaluatePrimary(expr, context);
    }

    private static object? EvaluateNot(string expr, IDictionary<string, object?> context)
    {
        expr = expr.Trim();
        if (expr.StartsWith('!'))
            return !IsTruthy(EvaluateNot(expr[1..].Trim(), context));
        return ParseBinary(expr, context, ["==", "!="]);
    }

    private static object? EvaluatePrimary(string expr, IDictionary<string, object?> context)
    {
        expr = expr.Trim();
        if (expr.Length == 0) throw new ExpressionException("Unexpected empty expression.");

        if (expr.StartsWith('('))
        {
            var inner = ExtractParenContent(expr, 0);
            if (inner == null || inner.Value.End != expr.Length - 1) throw new ExpressionException("Mismatched parentheses in expression.");
            return EvaluateBooleanExpr(inner.Value.Value, context);
        }
        if (expr.StartsWith("{{", StringComparison.Ordinal))
        {
            var end = FindTemplateEnd(expr, 0);
            if (end < 0) throw new ExpressionException("Unclosed template reference in expression.");
            return ResolveTemplate(expr[..(end + 2)], context);
        }
        if ((expr.StartsWith('\'') && expr.EndsWith('\'')) || (expr.StartsWith('"') && expr.EndsWith('"')))
            return expr[1..^1];
        if (expr == "true") return true;
        if (expr == "false") return false;
        if (expr == "null") return null;
        if (NumberLiteralPattern().IsMatch(expr)) return double.Parse(expr, CultureInfo.InvariantCulture);
        if (DottedPathPattern().IsMatch(expr))
            return ResolveNestedPath(expr, context);
        throw new ExpressionException($"Unexpected expression token: \"{Truncate(expr, 30)}\"");
    }

    private static object? ResolveNestedPath(string path, IDictionary<string, object?> context)
    {
        var parts = path.Split('.');
        object? current = context;
        foreach (var part in parts)
        {
            if (Array.IndexOf(ForbiddenPaths, part) >= 0) throw new ExpressionException($"Forbidden path segment: \"{part}\".");
            if (current is Dictionary<string, object?> d)
                current = d.TryGetValue(part, out var nv) ? nv : JsUndefined.Value;
            else if (current is List<object?> arr)
                current = part == "length"
                    ? (double)arr.Count
                    : int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < arr.Count ? arr[idx] : JsUndefined.Value;
            else
                return JsUndefined.Value;
        }
        return current;
    }

    private static bool EvaluateExpressionCheck(string expression, IDictionary<string, object?> context)
    {
        var cmp = FindTopLevelComparison(expression);
        if (cmp != null)
        {
            var leftExpr = expression[..cmp.Value.Index].Trim();
            var rightRaw = expression[(cmp.Value.Index + cmp.Value.Op.Length)..].Trim();
            if (leftExpr.Length == 0) throw new ExpressionException("Expression check has no left side before comparison operator.");
            if (rightRaw.Length == 0) throw new ExpressionException("Expression check has no right side after comparison operator.");

            // A bare {{token}} left side resolves to its TYPED value (boolean,
            // string, or number); only arithmetic left sides go through the
            // numeric math evaluator. Without this, `{{_hasSecretKey}} == true`
            // coerced the boolean to 1 (JsNumber) and compared 1 == "true"
            // (JsNumber("true") is NaN) — always false regardless of the real
            // value, so secret-key gates like `{{_hasSecretKey}} == true`
            // rejected every request with a conditionRejection.
            var left = GetSingleTemplateToken(leftExpr) is not null
                ? ResolveTemplate(leftExpr, context)
                : EvaluateMath(leftExpr, context);

            object? right;
            if (rightRaw.Contains("{{", StringComparison.Ordinal))
            {
                right = ResolveTemplate(rightRaw, context);
            }
            else if (bool.TryParse(rightRaw, out var boolRight))
            {
                // A literal `true`/`false` right side is a boolean, not the
                // string "true" (JS Number("true") is NaN, so bool == "true"
                // would be false even when both sides say true).
                right = boolRight;
            }
            else
            {
                var num = StringToNumber(rightRaw);
                right = double.IsNaN(num) ? ResolveTemplate(rightRaw, context) : num;
            }
            return Compare(left, NormalizeOp(cmp.Value.Op), right);
        }
        var result = EvaluateMath(expression, context);
        return result != 0 && !double.IsNaN(result);
    }

    private static readonly string[] TopLevelCompOps = ["!=", "==", "<=", ">=", "<", ">"];

    private readonly record struct TopLevelComparison(int Index, string Op);

    private static TopLevelComparison? FindTopLevelComparison(string expr)
    {
        var depth = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            if (expr[i] is '(' or '[') depth++;
            else if (expr[i] is ')' or ']') depth--;
            else if (depth == 0)
            {
                foreach (var op in TopLevelCompOps)
                    if (i + op.Length <= expr.Length && expr.Substring(i, op.Length) == op)
                        return new TopLevelComparison(i, op);
            }
        }
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static object? Get(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v : JsUndefined.Value;

    private static Dictionary<string, object?> Extend(IDictionary<string, object?> context, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(context);
        copy[key] = value;
        return copy;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ── JSON conversion (JsonElement → JS value model) ──────────────────────────

    public static object? FromJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => JsonObject(el),
        JsonValueKind.Array => JsonArray(el),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null,
    };

    private static Dictionary<string, object?> JsonObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject()) d[prop.Name] = FromJson(prop.Value);
        return d;
    }

    private static List<object?> JsonArray(JsonElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray()) list.Add(FromJson(item));
        return list;
    }
}

/// <summary>Singleton sentinel for the JS <c>undefined</c> value (distinct from <c>null</c>).</summary>
public sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();
    private JsUndefined() { }
    public override string ToString() => "undefined";
}

/// <summary>Raised for invalid expressions/templates — mirrors the JS <c>ExpressionError</c>.</summary>
public sealed class ExpressionException : Exception
{
    public ExpressionException(string message) : base(message) { }
}
