using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Port of the reference <c>suggestTests</c> and the helpers it reaches
/// (sboxcool-website <c>tools/sbox/endpoint-tester.js</c> 778-1460 and <c>tools/sbox/game-values.js</c> 85-168).
/// Pure: no I/O, no randomness, no clock.
/// JS null and JS undefined are both represented by a C# null node. Helpers mirror JS <c>||</c>, <c>String()</c>,
/// first-occurrence <c>String.replace</c> with <c>$</c> patterns, and <c>parseFloat</c> prefix parsing.
/// </summary>
internal static class ManagementSuggestTests
{
    // Reference name separator, byte-for-byte. The reference source holds the UTF-8 bytes
    // C3 83 C2 A2 C3 A2 E2 80 9A C2 AC C3 A2 E2 82 AC C2 9D here (not U+2014). Ported verbatim.
    private const string NameSeparator = "\u00C3\u00A2\u00E2\u201A\u00AC\u00E2\u20AC\u009D";

    private const string WrongValue = "__WRONG_VALUE__";

    // Sentinel for a truthy non-string op/field: never equal to any literal op name (JS strict compare fails too).
    private const string NonStringOp = "\u0000";

    private static readonly Regex TemplateEdges = new(@"^\{\{|\}\}\z", RegexOptions.CultureInvariant);
    private static readonly Regex InputFieldRef = new(@"^\{?\{?input\.([A-Za-z0-9_]+)\}?\}?\z", RegexOptions.CultureInvariant);
    private static readonly Regex InputTemplateValue = new(@"^\{\{input\.([A-Za-z0-9_]+)\}\}\z", RegexOptions.CultureInvariant);
    private static readonly Regex PlayerTemplateRef = new(@"\{\{-?player\.([^}]+)\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex ParseFloatPrefix = new(
        @"^[+-]?(?:Infinity|[0-9]+(?:\.[0-9]*)?(?:[eE][+-]?[0-9]+)?|\.[0-9]+(?:[eE][+-]?[0-9]+)?)",
        RegexOptions.CultureInvariant);

    private sealed record FailScenario(JsonObject Input, JsonObject MockData);

    /// <summary>
    /// Reference <c>suggestTests(endpoint, collections, gameValues)</c>.
    /// <paramref name="collections"/>: the reference <c>getCollections()</c> array.
    /// <paramref name="gameValues"/>: the reference <c>getGameValues()</c> document (items array, items map or legacy categories).
    /// Null means <c>{ items: [] }</c>.
    /// </summary>
    public static JsonArray Suggest(JsonElement endpoint, JsonElement collections, JsonElement? gameValues)
    {
        var ep = ToNode(endpoint);
        var cols = ToNode(collections);
        var gvNode = gameValues is JsonElement gv ? ToNode(gv) : null;
        var gvOrEmpty = Truthy(gvNode) ? gvNode : new JsonObject { ["items"] = new JsonArray() };

        var steps = Items(Get(ep, "steps"));
        var input = Or(Get(ep, "input"), new JsonObject());
        var values = FlattenGameValues(gvOrEmpty, cols);
        var mockData = GenerateMockData(cols, values);
        var label = JsText(Or(Get(ep, "name"), Get(ep, "slug")));
        var slug = Get(ep, "slug");
        var method = Or(Get(ep, "method"), Str("POST"));
        var hasNewEntityGuard = steps.Any(step =>
            IsStr(Get(step, "type"), "condition") && HasNotExistsCheck(Get(step, "check")));
        var suggestions = new JsonArray();

        // 1. Happy path
        var happyInput = GenerateSmartInput(input, values, steps);
        var happyMock = hasNewEntityGuard
            ? WithEmptyPlayers(mockData)
            : EnrichMockDataForInput(mockData, happyInput, steps);
        suggestions.Add(BuildSuggestion(
            $"{label} {NameSeparator} Happy Path",
            "Valid input with sufficient resources. Expected: success.",
            slug, method, happyInput, happyMock,
            Expect("pass"), StrArray("auto", "happy-path")));

        // 2. One failing test per condition step
        foreach (var step in steps)
        {
            if (!IsStr(Get(step, "type"), "condition")) continue;
            var failActions = Or(Get(step, "onFail"), new JsonObject());
            var errorCode = Or(Get(failActions, "errorCode"), Get(failActions, "error"), Str("CONDITION_FAILED"));
            var failScenario = GenerateFailScenario(step, input, values, mockData, steps);
            if (failScenario is null) continue;

            var stepId = Get(step, "id");
            var reason = Or(Get(failActions, "message"), Get(failActions, "errorMessage"), errorCode);
            var tags = StrArray("auto", "condition-fail");
            tags.Add(Clone(stepId));
            suggestions.Add(BuildSuggestion(
                $"{label} {NameSeparator} Fail: {JsText(stepId)}",
                $"Should fail condition \"{JsText(stepId)}\": {ToJsString(reason)}",
                slug, method, failScenario.Input, failScenario.MockData,
                ExpectFail(errorCode), tags));
        }

        // 3. Minimum values for numeric inputs
        var numericProps = NumericProperties(input);
        if (numericProps.Count > 0)
        {
            var edgeInput = CloneObject(happyInput);
            foreach (var (key, schema) in numericProps)
            {
                Put(edgeInput, key, Or(Get(schema, "min"), Num(0.1)));
            }

            suggestions.Add(BuildSuggestion(
                $"{label} {NameSeparator} Minimum Values",
                "Numeric inputs set to minimum allowed values.",
                slug, method, edgeInput,
                hasNewEntityGuard ? WithEmptyPlayers(mockData) : EnrichMockDataForInput(mockData, edgeInput, steps),
                Expect("pass"), StrArray("auto", "edge-case", "minimum")));
        }

        // 4. Missing required fields
        var required = Get(input, "required");
        if (Truthy(required) && required is JsonArray requiredFields && requiredFields.Count > 0)
        {
            foreach (var reqField in requiredFields)
            {
                var partialInput = CloneObject(happyInput);
                partialInput.Remove(ToJsString(reqField));
                suggestions.Add(BuildSuggestion(
                    $"{label} {NameSeparator} Missing: {ToJsString(reqField)}",
                    $"Required field \"{ToJsString(reqField)}\" is missing. Expected: schema validation failure.",
                    slug, method, partialInput, mockData,
                    ExpectFail(Str("SCHEMA_VALIDATION_FAILED")),
                    StrArray("auto", "validation", "missing-field")));
            }
        }

        // 5. Large values for numeric inputs
        if (numericProps.Count > 0)
        {
            var largeInput = CloneObject(happyInput);
            foreach (var (key, schema) in numericProps)
            {
                Put(largeInput, key, Or(Get(schema, "max"), Num(99999)));
            }

            suggestions.Add(BuildSuggestion(
                $"{label} {NameSeparator} Large Values",
                "Numeric inputs set to very large values. Tests overflow and condition limits.",
                slug, method, largeInput,
                EnrichMockDataForInput(mockData, largeInput, steps),
                Expect("any"), StrArray("auto", "edge-case", "overflow")));
        }

        return (JsonArray)JsOrder(suggestions)!;
    }

    // ---- suggestion assembly -------------------------------------------------------------

    private static JsonObject BuildSuggestion(
        string name,
        string description,
        JsonNode? endpointSlug,
        JsonNode? method,
        JsonObject input,
        JsonObject mockData,
        JsonObject expect,
        JsonArray tags) =>
        new()
        {
            ["name"] = Str(name),
            ["description"] = Str(description),
            ["endpoint"] = Clone(endpointSlug),
            ["method"] = Clone(method),
            ["input"] = CloneObject(input),
            ["mockData"] = CloneObject(mockData),
            ["expect"] = expect,
            ["skipWebhooks"] = Bool(true),
            ["tags"] = tags,
        };

    private static JsonObject Expect(string outcome) => new() { ["outcome"] = Str(outcome) };

    private static JsonObject ExpectFail(JsonNode? errorCode) =>
        new() { ["outcome"] = Str("fail"), ["errorCode"] = Clone(errorCode) };

    // ---- generateSmartInput (valid mode only; the reference never calls it with another mode) ---

    private static JsonObject GenerateSmartInput(JsonNode? inputSchema, JsonObject values, IReadOnlyList<JsonNode?> steps)
    {
        var input = new JsonObject();
        var props = Or(Get(inputSchema, "properties"), new JsonObject());
        var lookups = BuildInputLookupMap(steps);

        foreach (var (key, schema) in Entries(props))
        {
            var type = Get(schema, "type");
            if (IsStr(type, "string"))
            {
                JsonNode? found = null;
                if (lookups.TryGetValue(key, out var lookup)
                    && Get(values, JsText(lookup.Table)) is JsonArray table
                    && table.Count > 0)
                {
                    found = Get(table[0], JsText(lookup.Field));
                }

                Put(input, key, Or(
                    found,
                    FindIdValue(key, values),
                    Get(schema, "default"),
                    FirstOrNull(Get(schema, "enum")),
                    Str("test_value")));
            }
            else if (IsStr(type, "number"))
            {
                Put(input, key, Or(Get(schema, "default"), Get(schema, "min"), Num(5)));
            }
            else if (IsStr(type, "boolean"))
            {
                Put(input, key, Or(Get(schema, "default"), Bool(false)));
            }
            else if (IsStr(type, "array"))
            {
                Put(input, key, Or(Get(schema, "default"), new JsonArray()));
            }
            else
            {
                Put(input, key, Or(Get(schema, "default"), null));
            }
        }

        foreach (var (key, value) in ScanConditionsForInputConstraints(steps))
        {
            if (input.ContainsKey(key)) Put(input, key, value);
        }

        return input;
    }

    private static Dictionary<string, (JsonNode? Table, JsonNode? Field)> BuildInputLookupMap(IReadOnlyList<JsonNode?> steps)
    {
        var map = new Dictionary<string, (JsonNode? Table, JsonNode? Field)>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var type = Get(step, "type");
            var isLookup = IsStr(type, "lookup") || IsStr(type, "filter") || IsStr(type, "random_select");
            var where = Get(step, "where");
            if (!isLookup || !Truthy(where)) continue;

            var valueTemplate = ToJsString(Or(Get(where, "value"), Str(string.Empty)));
            var match = InputTemplateValue.Match(valueTemplate);
            if (match.Success && Truthy(Get(step, "table")))
            {
                map[match.Groups[1].Value] = (Get(step, "table"), Get(where, "field"));
            }
        }
        return map;
    }

    private static Dictionary<string, JsonNode?> ScanConditionsForInputConstraints(IReadOnlyList<JsonNode?> steps)
    {
        var constraints = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var check = Get(step, "check");
            if (!IsStr(Get(step, "type"), "condition") || !Truthy(check)) continue;
            ScanCheckForConstraints(check, constraints);
        }
        return constraints;
    }

    private static void ScanCheckForConstraints(JsonNode? check, Dictionary<string, JsonNode?> constraints)
    {
        var anyNode = Get(check, "any");
        if (Truthy(anyNode))
        {
            // Case 1: any -> all[] groups. Pick the first complete group.
            var firstAllGroup = Items(anyNode).FirstOrDefault(sub => Truthy(Get(sub, "all")));
            if (firstAllGroup is not null)
            {
                foreach (var item in Items(Get(firstAllGroup, "all")))
                {
                    ExtractInputEqConstraint(item, constraints);
                }
                return;
            }

            // Case 2: any -> flat eq checks.
            foreach (var sub in Items(anyNode))
            {
                if (ExtractInputEqConstraint(sub, constraints)) return;
            }
        }

        var allNode = Get(check, "all");
        if (Truthy(allNode))
        {
            foreach (var sub in Items(allNode))
            {
                ScanCheckForConstraints(sub, constraints);
            }
        }
    }

    private static bool ExtractInputEqConstraint(JsonNode? check, Dictionary<string, JsonNode?> constraints)
    {
        var field = FieldOf(Or(Get(check, "field"), Get(check, "left")));
        TryValueOf(check, out var value);
        var op = OpOf(Get(check, "op"), string.Empty);
        if (op != "==" && op != "eq") return false;
        if (TryString(value, out var text) && text.Contains("{{", StringComparison.Ordinal)) return false;

        var match = InputFieldRef.Match(field);
        if (!match.Success) return false;
        var name = match.Groups[1].Value;
        if (constraints.ContainsKey(name)) return false;

        constraints[name] = value;
        return true;
    }

    private static JsonNode? FindIdValue(string fieldName, JsonObject values)
    {
        var clean = RemoveSuffix(
            RemoveSuffix(RemoveSuffix(RemoveSuffix(fieldName, "_id"), "Id"), "_type"),
            "Type");

        foreach (var tableName in new[] { clean + "_types", clean + "s", clean, clean + "_table" })
        {
            if (Get(values, tableName) is JsonArray table && table.Count > 0)
            {
                var first = table[0];
                return Or(
                    Get(first, fieldName),
                    Get(first, clean + "Id"),
                    Get(first, clean + "_id"),
                    Get(first, "id"),
                    Get(first, clean),
                    null);
            }
        }

        // Returns as soon as the factions table exists, even when the result is falsy (JS does the same).
        if (fieldName.Contains("faction", StringComparison.Ordinal)
            && Get(values, "factions") is JsonArray factions
            && factions.Count > 0)
        {
            return Or(Get(factions[0], "factionId"), Get(factions[0], "faction_id"), Get(factions[0], "id"));
        }

        // Fallback: any table whose first row has the field (snake or camel case).
        var camelField = CamelCaseUnderscores(fieldName);
        foreach (var (_, table) in Entries(values))
        {
            if (table is not JsonArray rows || rows.Count == 0) continue;
            var first = rows[0];
            if (HasKey(first, fieldName)) return Get(first, fieldName);
            if (HasKey(first, camelField)) return Get(first, camelField);
        }

        return null;
    }

    // ---- enrichMockDataForInput and condition enrichment ---------------------------------

    private static JsonObject EnrichMockDataForInput(JsonObject baseMock, JsonObject input, IReadOnlyList<JsonNode?> steps)
    {
        var mock = CloneObject(baseMock);
        var transform = BuildTransformMap(steps);
        foreach (var step in steps)
        {
            var check = Get(step, "check");
            if (IsStr(Get(step, "type"), "condition") && Truthy(check))
            {
                EnrichForCheck(check, input, mock, transform);
            }
        }
        return mock;
    }

    private static Dictionary<string, List<string>> BuildTransformMap(IReadOnlyList<JsonNode?> steps)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var expression = Get(step, "expression");
            if (!IsStr(Get(step, "type"), "transform") || !Truthy(expression)) continue;

            var refs = PlayerTemplateRef
                .Matches(ToJsString(expression))
                .Select(m => m.Groups[1].Value)
                .ToList();
            if (refs.Count > 0)
            {
                map[JsText(Or(Get(step, "id"), Get(step, "as")))] = refs;
            }
        }
        return map;
    }

    private static void EnrichForCheck(
        JsonNode? check,
        JsonObject input,
        JsonObject mock,
        Dictionary<string, List<string>> transform)
    {
        var allNode = Get(check, "all");
        if (Truthy(allNode))
        {
            foreach (var sub in Items(allNode)) EnrichForCheck(sub, input, mock, transform);
            return;
        }

        var anyNode = Get(check, "any");
        if (Truthy(anyNode))
        {
            foreach (var sub in Items(anyNode)) EnrichForCheck(sub, input, mock, transform);
            return;
        }

        var field = FieldOf(Or(Get(check, "field"), Get(check, "left")));
        var op = OpOf(Get(check, "op"), string.Empty);
        var value = TryValueOf(check, out var explicitValue) ? explicitValue : Str(string.Empty);
        var players = PlayersOf(mock);

        if (op == "not_exists")
        {
            var path = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(path) && players is not null)
            {
                DeleteNestedValue(players, path);
            }
            return;
        }

        if (op == "exists")
        {
            var path = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(path) && players is not null
                && GetNestedValueSafe(players, path) is null)
            {
                SetNestedValue(players, path, Num(1));
            }
            return;
        }

        if (op == "==" || op == "eq")
        {
            var playerPath = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(playerPath) && players is not null)
            {
                if (TryString(value, out var template) && template.Contains("{{input.", StringComparison.Ordinal))
                {
                    SetNestedValue(players, playerPath, Str(ReplaceInputPlaceholders(template, input)));
                }
                else if (!TryString(value, out var literal) || !literal.Contains("{{", StringComparison.Ordinal))
                {
                    SetNestedValue(players, playerPath, value);
                }
            }
            return;
        }

        if (op is ">=" or ">" or "gte" or "gt" or "<=" or "<" or "lte" or "lt")
        {
            var isGreater = op is ">=" or ">" or "gte" or "gt";
            var playerPath = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(playerPath) && players is not null)
            {
                if (ResolveToNumber(value, input) is double number)
                {
                    SetNestedValue(players, playerPath, Num(isGreater ? number * 10 : 0));
                }
                return;
            }

            // Trace through transform steps: "{{refinery_tier}} > 0" -> player.refinery.tier
            var cleanField = CleanTemplateEdges(field);
            if (transform.TryGetValue(cleanField, out var transformFields) && players is not null)
            {
                if (ResolveToNumber(value, input) is double traced)
                {
                    foreach (var playerField in transformFields)
                    {
                        SetNestedValue(players, playerField, Num(isGreater ? Math.Max(traced * 10, 1) : 0));
                    }
                }
            }
            return;
        }

        if (op == "contains" && players is not null)
        {
            var playerPath = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(playerPath))
            {
                var resolved = ResolveValueText(value, input);
                if (GetNestedValueSafe(players, playerPath) is JsonArray existing
                    && !ArrayIncludes(existing, resolved))
                {
                    existing.Add(Str(resolved));
                }
            }
            return;
        }

        if (op == "not_contains" && players is not null)
        {
            var playerPath = ResolveToPlayerPath(field, input);
            if (!string.IsNullOrEmpty(playerPath))
            {
                var resolved = ResolveValueText(value, input);
                if (GetNestedValueSafe(players, playerPath) is JsonArray existing)
                {
                    var index = IndexOfString(existing, resolved);
                    if (index >= 0) existing.RemoveAt(index);
                }
            }
            // Falls through to the fallback below, as in the reference.
        }

        // Fallback: field embeds an input reference, e.g. "player.ores.{{input.ore_id}}".
        if (field.Contains("input.", StringComparison.Ordinal))
        {
            var parts = ReplaceInputPlaceholders(field, input).Split('.');
            if (parts[0] == "player" && players is not null)
            {
                if (ResolveToNumber(value, input) is double number)
                {
                    SetNestedValue(players, string.Join(".", parts[1..]), Num(number * 10));
                }
            }
        }
    }

    private static bool HasNotExistsCheck(JsonNode? check)
    {
        if (!Truthy(check)) return false;
        if (IsStr(Get(check, "op"), "not_exists")
            && FieldOf(Get(check, "field")).Contains("player.", StringComparison.Ordinal))
        {
            return true;
        }

        var allNode = Get(check, "all");
        if (Truthy(allNode)) return Items(allNode).Any(sub => HasNotExistsCheck(sub));

        var anyNode = Get(check, "any");
        if (Truthy(anyNode)) return Items(anyNode).Any(sub => HasNotExistsCheck(sub));

        return false;
    }

    private static List<(string Key, JsonNode? Schema)> NumericProperties(JsonNode? input)
    {
        var result = new List<(string Key, JsonNode? Schema)>();
        var properties = Get(input, "properties");
        if (!Truthy(properties)) return result;

        foreach (var (key, schema) in Entries(properties))
        {
            if (IsStr(Get(schema, "type"), "number")) result.Add((key, schema));
        }
        return result;
    }

    private static JsonObject WithEmptyPlayers(JsonObject mockData)
    {
        var copy = CloneObject(mockData);
        copy["players"] = new JsonObject();
        return copy;
    }

    // ---- generateFailScenario -----------------------------------------------------------

    private static FailScenario? GenerateFailScenario(
        JsonNode? condStep,
        JsonNode? inputSchema,
        JsonObject values,
        JsonObject baseMock,
        IReadOnlyList<JsonNode?> steps)
    {
        var check = Get(condStep, "check");
        if (!Truthy(check)) return null;

        var input = GenerateSmartInput(inputSchema, values, steps);
        var mock = EnrichMockDataForInput(baseMock, input, steps);

        var allNode = Get(check, "all");
        var anyNode = Get(check, "any");
        var target = Truthy(allNode)
            ? FirstOrNull(allNode)
            : Truthy(anyNode) ? FirstOrNull(anyNode) : check;
        // Non-object targets: the reference reads undefined fields and returns null without mutating.
        if (target is not JsonObject targetObj) return null;

        var field = FieldOf(Or(Get(targetObj, "field"), Get(targetObj, "left")));
        var op = OpOf(Get(targetObj, "op"), ">=");

        var playerPath = ResolveToPlayerPath(field, input);
        if (!string.IsNullOrEmpty(playerPath) && PlayersOf(mock) is { } players)
        {
            if (op is ">=" or ">" or "gte" or "gt")
            {
                SetNestedValue(players, playerPath, Num(0));
            }
            else if (op is "<=" or "<" or "lte" or "lt")
            {
                SetNestedValue(players, playerPath, Num(999999));
            }
            else if (op is "==" or "eq")
            {
                SetNestedValue(players, playerPath, Str(WrongValue));
            }
            else if (op == "exists")
            {
                DeleteNestedValue(players, playerPath);
            }
            else if (op == "not_exists")
            {
                SetNestedValue(players, playerPath, Num(99999));
            }
            else if (op == "contains")
            {
                SetNestedValue(players, playerPath, new JsonArray());
            }
            else if (op == "not_contains")
            {
                var rawValue = Get(targetObj, "value");
                var text = TryString(rawValue, out var s)
                    ? s
                    : Truthy(rawValue) ? ToJsString(rawValue) : string.Empty;
                SetNestedValue(players, playerPath, new JsonArray(Str(ReplaceInputPlaceholders(text, input))));
            }
            return new FailScenario(input, mock);
        }

        // Input equality on "{{input.X}}": send a wrong value.
        var cleanField = TemplateEdges.Replace(field, string.Empty);
        if ((op == "==" || op == "eq") && cleanField.StartsWith("input.", StringComparison.Ordinal))
        {
            var inputField = cleanField["input.".Length..];
            if (input.ContainsKey(inputField))
            {
                return new FailScenario(WithWrongInputValue(input, inputField), mock);
            }
        }

        // all[] target: break the first input equality sub-check.
        if (Truthy(Get(targetObj, "all")))
        {
            foreach (var sub in Items(Get(targetObj, "all")))
            {
                var subField = TemplateEdges.Replace(FieldOf(Or(Get(sub, "field"), Get(sub, "left"))), string.Empty);
                var subOp = OpOf(Get(sub, "op"), string.Empty);
                if ((subOp == "==" || subOp == "eq") && subField.StartsWith("input.", StringComparison.Ordinal))
                {
                    var inputField = subField["input.".Length..];
                    if (input.ContainsKey(inputField))
                    {
                        return new FailScenario(WithWrongInputValue(input, inputField), mock);
                    }
                }
            }
        }

        return null;
    }

    private static JsonObject WithWrongInputValue(JsonObject input, string key)
    {
        var copy = CloneObject(input);
        copy[key] = Str(WrongValue);
        return copy;
    }

    // ---- path and value helpers ---------------------------------------------------------

    private static JsonObject? PlayersOf(JsonObject mock) => Get(mock, "players") as JsonObject;

    private static string CleanTemplateEdges(string field)
    {
        var cleaned = TemplateEdges.Replace(field, string.Empty);
        return cleaned.StartsWith('-') ? cleaned[1..] : cleaned;
    }

    private static string? ResolveToPlayerPath(string field, JsonObject input)
    {
        var cleaned = CleanTemplateEdges(field).Trim();
        if (!cleaned.StartsWith("player.", StringComparison.Ordinal)) return null;
        return ReplaceInputPlaceholders(cleaned["player.".Length..], input);
    }

    private static string ResolveValueText(JsonNode? value, JsonObject input) =>
        ReplaceInputPlaceholders(TryString(value, out var text) ? text : ToJsString(value), input);

    private static double? ResolveToNumber(JsonNode? value, JsonObject input)
    {
        var number = ParseFloat(ResolveValueText(value, input));
        return double.IsNaN(number) ? null : number;
    }

    private static string ReplaceInputPlaceholders(string text, JsonObject input)
    {
        foreach (var kv in input)
        {
            text = ReplaceFirstPlaceholder(text, kv.Key, kv.Value);
        }
        return text;
    }

    // String.prototype.replace(string, value): first occurrence only; value is ToString'd; $-patterns expand.
    private static string ReplaceFirstPlaceholder(string text, string key, JsonNode? replacement)
    {
        var search = "{{input." + key + "}}";
        var index = text.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return text;

        var expanded = ExpandReplacement(ToJsString(replacement), text, index, search.Length);
        return text[..index] + expanded + text[(index + search.Length)..];
    }

    private static string ExpandReplacement(string replacement, string text, int matchIndex, int matchLength)
    {
        if (replacement.IndexOf('$') < 0) return replacement;

        var sb = new StringBuilder(replacement.Length);
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '$' && i + 1 < replacement.Length)
            {
                switch (replacement[i + 1])
                {
                    case '$':
                        sb.Append('$');
                        i++;
                        continue;
                    case '&':
                        sb.Append(text, matchIndex, matchLength);
                        i++;
                        continue;
                    case '`':
                        sb.Append(text, 0, matchIndex);
                        i++;
                        continue;
                    case '\'':
                        sb.Append(text, matchIndex + matchLength, text.Length - matchIndex - matchLength);
                        i++;
                        continue;
                }
            }
            sb.Append(replacement[i]);
        }
        return sb.ToString();
    }

    // JS parseFloat: leading whitespace, then the longest decimal-literal prefix; NaN when none.
    private static double ParseFloat(string text)
    {
        var match = ParseFloatPrefix.Match(text.TrimStart());
        if (!match.Success) return double.NaN;

        var token = match.Value;
        if (token.EndsWith("Infinity", StringComparison.Ordinal))
        {
            return token[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }
        return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static JsonNode? GetNestedValueSafe(JsonNode? root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.'))
        {
            if (current is not (JsonObject or JsonArray)) return null;
            current = Get(current, part);
        }
        return current;
    }

    private static void SetNestedValue(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var child = Get(current, parts[i]);
            if (child is JsonObject or JsonArray)
            {
                current = child;
                continue;
            }
            var created = new JsonObject();
            SetChild(current, parts[i], created);
            current = created;
        }
        SetChild(current, parts[^1], Clone(value));
    }

    private static void DeleteNestedValue(JsonObject root, string path)
    {
        var parts = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var child = Get(current, parts[i]);
            if (child is not (JsonObject or JsonArray)) return;
            current = child;
        }
        RemoveChild(current, parts[^1]);
    }

    private static bool ArrayIncludes(JsonArray array, string value) =>
        array.Any(item => TryString(item, out var text) && text == value);

    private static int IndexOfString(JsonArray array, string value)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (TryString(array[i], out var text) && text == value) return i;
        }
        return -1;
    }

    private static JsonNode? FirstOrNull(JsonNode? node) =>
        node is JsonArray array && array.Count > 0 ? array[0] : null;

    // ---- generateMockData and field defaults --------------------------------------------

    private static JsonObject GenerateMockData(JsonNode? collections, JsonObject values)
    {
        var mock = new JsonObject();
        foreach (var collection in Items(collections))
        {
            var name = Or(Get(collection, "name"), Get(collection, "id"));
            var schema = Or(Get(collection, "schema"), new JsonObject());
            var data = new JsonObject();

            var properties = Get(schema, "properties");
            if (Truthy(properties))
            {
                foreach (var (field, def) in Entries(properties))
                {
                    Put(data, field, GenerateFieldDefault(field, def, values));
                }
            }
            else if (schema is JsonObject or JsonArray)
            {
                foreach (var (field, def) in Entries(schema))
                {
                    if (field.StartsWith('_')) continue;
                    Put(data, field, GenerateFieldDefault(field, def, values));
                }
            }

            Put(mock, JsText(name), data);
        }
        return mock;
    }

    private static JsonNode? GenerateFieldDefault(string field, JsonNode? def, JsonObject values)
    {
        if (def is null) return null;
        if (def is JsonObject defWithDefault && defWithDefault.ContainsKey("default"))
        {
            return Clone(defWithDefault["default"]);
        }

        string? type;
        if (def is JsonObject defObj)
        {
            var declared = Get(defObj, "type");
            type = Truthy(declared)
                ? (TryString(declared, out var declaredText) ? declaredText : null)
                : "string";
        }
        else
        {
            type = TryString(def, out var defText) ? defText : "string";
        }

        switch (type)
        {
            case "number":
                return field switch
                {
                    "currency" => Num(50000),
                    "xp" => Num(5000),
                    "backpackCapacity" => Num(200),
                    "currentOreKg" => Num(0),
                    "level" or "rank" => Num(5),
                    _ => Or(Get(def, "min"), Num(0)),
                };
            case "string":
                return Str(string.Empty);
            case "boolean":
                return Bool(false);
            case "array":
                return new JsonArray();
            case "object":
                return GenerateObjectDefault(field, def, values);
            default:
                return null;
        }
    }

    private static JsonNode? GenerateObjectDefault(string field, JsonNode? def, JsonObject values)
    {
        if (field == "ores" && Get(values, "ore_types") is JsonArray oreTypes)
        {
            var oreObject = new JsonObject();
            foreach (var ore in oreTypes)
            {
                var id = Or(Get(ore, "oreId"), Get(ore, "ore_id"), Get(ore, "id"));
                if (Truthy(id)) Put(oreObject, JsText(id), Num(5));
            }
            return oreObject;
        }

        if (Truthy(Get(def, "additionalProperties"))) return new JsonObject();

        var properties = Get(def, "properties");
        if (!Truthy(properties)) return new JsonObject();

        var obj = new JsonObject();
        foreach (var (key, value) in Entries(properties))
        {
            Put(obj, key, GenerateFieldDefault(key, value, values));
        }
        return obj;
    }

    // ---- flattenGameValues (game-values.js 85-168) --------------------------------------

    private static JsonObject FlattenGameValues(JsonNode? gameValues, JsonNode? collections)
    {
        var nested = new JsonObject();

        // Collection-level constants and tables take precedence.
        if (collections is JsonArray)
        {
            foreach (var collection in Items(collections))
            {
                foreach (var constant in Items(Get(collection, "constants")))
                {
                    if (Truthy(Get(constant, "id")))
                    {
                        nested[JsText(Get(constant, "id"))] = CopyEntries(Get(constant, "entries"));
                    }
                }

                foreach (var table in Items(Get(collection, "tables")))
                {
                    if (Truthy(Get(table, "id")))
                    {
                        nested[JsText(Get(table, "id"))] = CopyRows(Get(table, "rows"));
                    }
                }
            }
        }

        // Legacy game-values items fill only what the collections do not define.
        foreach (var item in NormalizeGameValueItems(gameValues))
        {
            if (!Truthy(Get(item, "id")) || nested.ContainsKey(JsText(Get(item, "id")))) continue;
            nested[JsText(Get(item, "id"))] = IsStr(Get(item, "type"), "table")
                ? (JsonNode)CopyRows(Get(item, "rows"))
                : (JsonNode)CopyEntries(Get(item, "entries"));
        }

        return nested;
    }

    private static List<JsonNode?> NormalizeGameValueItems(JsonNode? gameValues)
    {
        if (gameValues is JsonArray list) return list.Where(item => Truthy(item)).ToList();
        if (gameValues is not JsonObject document) return new List<JsonNode?>();

        var items = Get(document, "items");
        if (items is JsonArray itemArray) return itemArray.Where(item => Truthy(item)).ToList();

        if (items is JsonObject itemMap)
        {
            var result = new List<JsonNode?>();
            foreach (var (id, value) in Entries(itemMap))
            {
                var item = ItemFromMapEntry(id, value);
                if (item is not null) result.Add(item);
            }
            return result;
        }

        if (Get(document, "categories") is JsonArray categories)
        {
            var result = new List<JsonNode?>();
            foreach (var category in categories)
            {
                var item = LegacyCategoryToItem(category);
                if (item is not null) result.Add(item);
            }
            return result;
        }

        return new List<JsonNode?>();
    }

    private static JsonObject? ItemFromMapEntry(string id, JsonNode? value)
    {
        if (value is not JsonObject map) return null;

        // { id: value.id || id, ...value }: an own "id" key in value wins, even when falsy.
        var item = new JsonObject { ["id"] = map.ContainsKey("id") ? Clone(map["id"]) : Str(id) };
        foreach (var kv in map)
        {
            if (kv.Key != "id") item[kv.Key] = Clone(kv.Value);
        }
        return item;
    }

    private static JsonObject? LegacyCategoryToItem(JsonNode? category)
    {
        if (category is not JsonObject cat || !Truthy(Get(cat, "id"))) return null;

        var type = Get(cat, "type");
        var values = Get(cat, "values");
        if (IsStr(type, "constants") || (!Truthy(type) && Truthy(values)))
        {
            var entries = new JsonObject();
            if (values is JsonObject valueMap)
            {
                foreach (var (key, def) in Entries(valueMap))
                {
                    entries[key] = def is JsonObject defObj && defObj.ContainsKey("value")
                        ? Clone(defObj["value"])
                        : Clone(def);
                }
            }
            return new JsonObject
            {
                ["id"] = Clone(Get(cat, "id")),
                ["type"] = Str("group"),
                ["entries"] = entries,
            };
        }

        return new JsonObject
        {
            ["id"] = Clone(Get(cat, "id")),
            ["type"] = Clone(Or(type, Str("table"))),
            ["rows"] = CopyRows(Get(cat, "rows")),
        };
    }

    private static JsonObject CopyEntries(JsonNode? entries)
    {
        var copy = new JsonObject();
        if (entries is JsonObject map)
        {
            foreach (var kv in map) copy[kv.Key] = Clone(kv.Value);
        }
        return copy;
    }

    private static JsonArray CopyRows(JsonNode? rows)
    {
        var copy = new JsonArray();
        foreach (var row in Items(rows)) copy.Add(SpreadCopy(row));
        return copy;
    }

    // JS object spread: {...row}
    private static JsonObject SpreadCopy(JsonNode? source)
    {
        var copy = new JsonObject();
        if (source is JsonObject map)
        {
            foreach (var kv in map) copy[kv.Key] = Clone(kv.Value);
        }
        else if (source is JsonArray list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                copy[i.ToString(CultureInfo.InvariantCulture)] = Clone(list[i]);
            }
        }
        else if (TryString(source, out var text))
        {
            for (var i = 0; i < text.Length; i++)
            {
                copy[i.ToString(CultureInfo.InvariantCulture)] = Str(text[i].ToString());
            }
        }
        return copy;
    }

    // ---- JS value model helpers ---------------------------------------------------------

    private static JsonNode? ToNode(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    // JS truthiness: null, false, 0, NaN and "" are falsy; objects and arrays are truthy.
    private static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        switch (node.GetValueKind())
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                {
                    var number = node.GetValue<double>();
                    return number != 0 && !double.IsNaN(number);
                }
            case JsonValueKind.String:
                return node.GetValue<string>().Length > 0;
            case JsonValueKind.Null:
                return false;
            default:
                return true;
        }
    }

    // JS a || b || c: first truthy operand, else the last operand.
    private static JsonNode? Or(params JsonNode?[] candidates)
    {
        for (var i = 0; i < candidates.Length - 1; i++)
        {
            if (Truthy(candidates[i])) return candidates[i];
        }
        return candidates[^1];
    }

    private static JsonNode? Get(JsonNode? target, string key) => target switch
    {
        JsonObject obj => obj.TryGetPropertyValue(key, out var value) ? value : null,
        JsonArray arr => TryArrayIndex(key, out var index) && index < arr.Count ? arr[(int)index] : null,
        _ => null,
    };

    private static bool HasKey(JsonNode? target, string key) => target switch
    {
        JsonObject obj => obj.ContainsKey(key),
        JsonArray arr => TryArrayIndex(key, out var index) && index < arr.Count,
        _ => false,
    };

    // Object.entries for objects (insertion order) and arrays (index keys). Other values yield nothing.
    private static List<(string Key, JsonNode? Value)> Entries(JsonNode? node)
    {
        var list = new List<(string Key, JsonNode? Value)>();
        if (node is JsonObject obj)
        {
            foreach (var kv in obj) list.Add((kv.Key, kv.Value));
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                list.Add((i.ToString(CultureInfo.InvariantCulture), arr[i]));
            }
        }
        return list;
    }

    // for...of over an array; non-arrays yield nothing.
    private static List<JsonNode?> Items(JsonNode? node) =>
        node is JsonArray arr ? arr.ToList() : new List<JsonNode?>();

    private static bool TryString(JsonNode? node, out string value)
    {
        if (node is JsonValue && node.GetValueKind() == JsonValueKind.String)
        {
            value = node.GetValue<string>();
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsStr(JsonNode? node, string expected) => TryString(node, out var text) && text == expected;

    // typeof x === "string" ? x : "" (JS would throw on non-string field access; treated as absent here).
    private static string FieldOf(JsonNode? node) => TryString(node, out var text) ? text : string.Empty;

    // Truthy non-string op never matches a literal op name, same as the reference's strict comparisons.
    private static string OpOf(JsonNode? node, string fallback)
    {
        if (!Truthy(node)) return fallback;
        return TryString(node, out var op) ? op : NonStringOp;
    }

    // JS: check.value !== undefined ? check.value : check.right. Returns false when neither key is present.
    private static bool TryValueOf(JsonNode? check, out JsonNode? value)
    {
        if (check is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("value", out value)) return true;
            return obj.TryGetPropertyValue("right", out value);
        }
        value = null;
        return false;
    }

    // JS ToPropertyKey / template text where undefined prints as "undefined".
    private static string JsText(JsonNode? node) => node is null ? "undefined" : ToJsString(node);

    // JS String(value) for a defined value; null prints as "null".
    private static string ToJsString(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonArray array)
        {
            return string.Join(",", array.Select(item => item is null ? string.Empty : ToJsString(item)));
        }
        if (node is JsonObject) return "[object Object]";

        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                return node.GetValue<string>();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return NumberToString(node.GetValue<double>());
            default:
                return "null";
        }
    }

    private static string NumberToString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";
        if (Math.Floor(value) == value && Math.Abs(value) < 1e21)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E');
        if (e < 0) return text;

        var exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return text[..e] + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10 || (key.Length > 1 && key[0] == '0')) return false;
        foreach (var ch in key)
        {
            if (ch < '0' || ch > '9') return false;
        }
        var value = ulong.Parse(key, CultureInfo.InvariantCulture);
        if (value > 4294967294UL) return false;
        index = (uint)value;
        return true;
    }

    // ---- node construction and mutation (every insert clones, so no node has two parents) ---

    private static JsonNode? Str(string value) => JsonValue.Create(value);

    private static JsonNode? Bool(bool value) => JsonValue.Create(value);

    // JS JSON.stringify turns non-finite numbers into null; -0 serializes as 0.
    private static JsonNode? Num(double value)
    {
        if (!double.IsFinite(value)) return null;
        if (value == 0) value = 0;
        return JsonValue.Create(value);
    }

    private static JsonArray StrArray(params string[] items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(Str(item));
        return array;
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static JsonObject CloneObject(JsonObject node) => (JsonObject)node.DeepClone();

    private static void Put(JsonObject target, string key, JsonNode? value) => target[key] = Clone(value);

    // Stores a node that the caller owns (fresh, or already cloned).
    private static void SetChild(JsonNode parent, string key, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj:
                obj[key] = value;
                break;
            case JsonArray arr when TryArrayIndex(key, out var index):
                while (arr.Count <= index) arr.Add((JsonNode?)null);
                arr[(int)index] = value;
                break;
        }
    }

    // delete obj[key]; on arrays JS leaves a hole, which JSON.stringify writes as null.
    private static void RemoveChild(JsonNode parent, string key)
    {
        switch (parent)
        {
            case JsonObject obj:
                obj.Remove(key);
                break;
            case JsonArray arr when TryArrayIndex(key, out var index) && index < arr.Count:
                arr[(int)index] = null;
                break;
        }
    }

    private static string RemoveSuffix(string text, string suffix) =>
        text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    // fieldName.replace(/_([a-z])/g, (_, c) => c.toUpperCase())
    private static string CamelCaseUnderscores(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i + 1 < text.Length && text[i + 1] is >= 'a' and <= 'z')
            {
                sb.Append(char.ToUpperInvariant(text[i + 1]));
                i++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    // Rebuilds objects so array-index keys come first in ascending order, as JS property enumeration does.
    private static JsonNode? JsOrder(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var indexed = new List<(uint Index, string Key, JsonNode? Value)>();
                    var named = new List<(string Key, JsonNode? Value)>();
                    foreach (var kv in obj)
                    {
                        var ordered = JsOrder(kv.Value);
                        if (TryArrayIndex(kv.Key, out var index))
                        {
                            indexed.Add((index, kv.Key, ordered));
                        }
                        else
                        {
                            named.Add((kv.Key, ordered));
                        }
                    }

                    var result = new JsonObject();
                    foreach (var (_, key, value) in indexed.OrderBy(entry => entry.Index))
                    {
                        result[key] = value;
                    }
                    foreach (var (key, value) in named)
                    {
                        result[key] = value;
                    }
                    return result;
                }
            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array) result.Add(JsOrder(item));
                    return result;
                }
            default:
                return node?.DeepClone();
        }
    }
}
