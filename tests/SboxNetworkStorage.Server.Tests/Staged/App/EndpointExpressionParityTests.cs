using System.Globalization;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Asserts the C# <see cref="EndpointExpression"/> port produces identical
/// results to the authoritative legacy server expression engine for every case in the
/// golden oracle (endpoint-expression-oracle.json, generated from the JS engine
/// by endpoint-expression-oracle.mjs). Any divergence fails the build — this is
/// the parity gate guarding the legacy server → .NET endpoint-execution cutover.
/// </summary>
public sealed class EndpointExpressionParityTests
{
    private static readonly OracleCase[] Cases = LoadOracle();

    [Fact]
    public void OracleFixture_IsNonTrivial()
    {
        Assert.True(Cases.Length >= 100, $"Expected >=100 oracle cases, found {Cases.Length}.");
        Assert.Contains(Cases, c => c.Id == "t_single_num");
        Assert.Contains(Cases, c => c.Id == "e_filter"); // the bare-path-throws quirk
    }

    [Fact]
    public void EndpointExpression_MatchesJsEngine_ForEveryOracleCase()
    {
        var failures = new List<string>();

        foreach (var c in Cases)
        {
            var context = (Dictionary<string, object?>)EndpointExpression.FromJson(c.Context)!;

            object? actual = null;
            var threw = false;
            string? thrownMessage = null;
            try
            {
                actual = c.Kind switch
                {
                    "template" => EndpointExpression.ResolveTemplate(c.Expr, context),
                    "condition" => EndpointExpression.EvaluateCondition(EndpointExpression.FromJson(c.Check!.Value), context),
                    "math" => EndpointExpression.EvaluateMath(c.Expr!, context),
                    "expression" => EndpointExpression.EvaluateExpression(c.Expr!, context),
                    _ => throw new InvalidOperationException($"Unknown kind {c.Kind}"),
                };
            }
            catch (ExpressionException ex)
            {
                threw = true;
                thrownMessage = ex.Message;
            }

            if (c.Threw)
            {
                if (!threw) failures.Add($"[{c.Id}] expected THROW but returned {Describe(actual)}");
                continue;
            }

            if (threw)
            {
                failures.Add($"[{c.Id}] expected {Describe(c.Expected)} but threw: {thrownMessage}");
                continue;
            }

            if (!JsDeepEquals(actual, c.Expected))
                failures.Add($"[{c.Id}] expected {Describe(c.Expected)} but got {Describe(actual)}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{Cases.Length} parity mismatches:\n  " + string.Join("\n  ", failures));
    }

    // ── Boolean template comparisons in expression checks ────────────────────
    //
    // `{{_hasSecretKey}} == true` is the documented way to gate dedicated-server
    // flows (00c-source-authoring.md: _hasSecretKey is a boolean). This syntax is
    // NOT covered by the oracle (the legacy server engine never supported it — it coerced
    // the token to a number and compared 1 == "true", which is always false, so
    // the parity oracle cannot pin a sensible value for it). The .NET port
    // deliberately fixes the semantics: the check must honor the boolean's real
    // value. Keep these in sync with the structured-field form below.

    [Fact]
    public void ExpressionCheck_BooleanTemplateComparison_HonorsBooleanValue()
    {
        var withSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = true };
        var withoutSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = false };

        bool Check(IDictionary<string, object?> ctx, string expression)
            => EndpointExpression.EvaluateCondition(
                new Dictionary<string, object?> { ["expression"] = expression }, ctx);

        Assert.True(Check(withSecret, "{{_hasSecretKey}} == true"));
        Assert.False(Check(withoutSecret, "{{_hasSecretKey}} == true"));
        Assert.False(Check(withSecret, "{{_hasSecretKey}} == false"));
        Assert.True(Check(withoutSecret, "{{_hasSecretKey}} == false"));
        Assert.True(Check(withSecret, "{{_hasSecretKey}} != false"));
        Assert.False(Check(withoutSecret, "{{_hasSecretKey}} != false"));
    }

    [Fact]
    public void ExpressionCheck_NonBooleanTemplateComparison_StillMatchesNumberAndStringSemantics()
    {
        var context = new Dictionary<string, object?>
        {
            ["count"] = 5d,
            ["name"] = "alpha",
            ["zone"] = "spawn",
        };

        bool Check(string expression)
            => EndpointExpression.EvaluateCondition(
                new Dictionary<string, object?> { ["expression"] = expression }, context);

        // Numbers compare numerically.
        Assert.True(Check("{{count}} == 5"));
        Assert.False(Check("{{count}} == 6"));
        Assert.True(Check("{{count}} > 4"));
        // Strings compare by value.
        Assert.True(Check("{{zone}} == spawn"));
        Assert.False(Check("{{name}} == beta"));
        // Missing values compare as nullish (no longer a math-throw).
        Assert.False(Check("{{missing}} == 5"));
    }

    [Fact]
    public void StructuredFieldCheck_BooleanValue_HonorsBooleanValue()
    {
        // The documented structured form (field/op/value) must keep working — it
        // is the form the docs and the legacy server engine tests use.
        var withSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = true };
        var withoutSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = false };

        bool Check(IDictionary<string, object?> ctx, object value)
            => EndpointExpression.EvaluateCondition(
                new Dictionary<string, object?> { ["field"] = "_hasSecretKey", ["op"] = "==", ["value"] = value }, ctx);

        Assert.True(Check(withSecret, true));
        Assert.False(Check(withoutSecret, true));
        Assert.True(Check(withoutSecret, false));
    }

    [Fact]
    public void StructuredFieldCheck_StringifiedBooleanValue_MatchesBooleanField()
    {
        // The YAML compiler (YamlDotNet CoreSchema) stored `value: true` as the
        // STRING "true"; the check must still honor the boolean field's value.
        var withSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = true };
        var withoutSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = false };

        bool Check(IDictionary<string, object?> ctx, object value)
            => EndpointExpression.EvaluateCondition(
                new Dictionary<string, object?> { ["field"] = "{{_hasSecretKey}}", ["op"] = "==", ["value"] = value }, ctx);

        Assert.True(Check(withSecret, "true"));
        Assert.False(Check(withoutSecret, "true"));
        Assert.False(Check(withSecret, "false"));
        Assert.True(Check(withoutSecret, "false"));
    }

    // ── Oracle loading ──

    private sealed record OracleCase(string Id, string Kind, string? Expr, JsonElement? Check, JsonElement Context, bool Threw, object? Expected);

    private static OracleCase[] LoadOracle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "NetworkStorage", "endpoint-expression-oracle.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<OracleCase>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            var kind = c.GetProperty("kind").GetString()!;
            var threw = c.GetProperty("threw").GetBoolean();
            string? expr = c.TryGetProperty("expr", out var e) ? e.GetString() : null;
            JsonElement? check = c.TryGetProperty("check", out var ch) ? ch.Clone() : null;
            var context = c.GetProperty("context").Clone();
            object? expected = null;
            if (!threw && c.TryGetProperty("value", out var v))
                expected = ConvertOracleValue(v);
            list.Add(new OracleCase(id, kind, expr, check, context, threw, expected));
        }
        return list.ToArray();
    }

    // Maps the oracle JSON value into the engine's value model, translating the
    // {"__undefined":true} sentinel back to JsUndefined.
    private static object? ConvertOracleValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("__undefined", out var u) && u.ValueKind == JsonValueKind.True && el.EnumerateObject().Count() == 1)
            return JsUndefined.Value;
        return el.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(el),
            JsonValueKind.Array => ConvertArray(el),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null,
        };
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject()) d[p.Name] = ConvertOracleValue(p.Value);
        return d;
    }

    private static List<object?> ConvertArray(JsonElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray()) list.Add(ConvertOracleValue(item));
        return list;
    }

    // ── Structural JS value equality ──

    private static bool JsDeepEquals(object? a, object? b)
    {
        var aU = EndpointExpression.IsUndefined(a);
        var bU = EndpointExpression.IsUndefined(b);
        if (aU || bU) return aU && bU;
        if (a is null || b is null) return a is null && b is null;

        switch (a)
        {
            case double da when b is double db:
                return da == db || (double.IsNaN(da) && double.IsNaN(db));
            case bool ba when b is bool bb:
                return ba == bb;
            case string sa when b is string sb:
                return string.Equals(sa, sb, StringComparison.Ordinal);
            case List<object?> la when b is List<object?> lb:
                if (la.Count != lb.Count) return false;
                for (var i = 0; i < la.Count; i++) if (!JsDeepEquals(la[i], lb[i])) return false;
                return true;
            case Dictionary<string, object?> oa when b is Dictionary<string, object?> ob:
                if (oa.Count != ob.Count) return false;
                foreach (var (k, v) in oa)
                {
                    if (!ob.TryGetValue(k, out var ov)) return false;
                    if (!JsDeepEquals(v, ov)) return false;
                }
                return true;
            default:
                return false;
        }
    }

    private static string Describe(object? v)
    {
        if (EndpointExpression.IsUndefined(v)) return "undefined";
        if (v is null) return "null";
        var sb = new StringBuilder();
        Write(sb, v);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case string s: sb.Append('"').Append(s).Append('"'); break;
            case List<object?> a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(sb, a[i]); }
                sb.Append(']');
                break;
            case Dictionary<string, object?> o:
                sb.Append('{');
                var first = true;
                foreach (var (k, val) in o) { if (!first) sb.Append(','); first = false; sb.Append(k).Append(':'); Write(sb, val); }
                sb.Append('}');
                break;
            default:
                sb.Append(EndpointExpression.IsUndefined(v) ? "undefined" : v.ToString());
                break;
        }
    }
}
