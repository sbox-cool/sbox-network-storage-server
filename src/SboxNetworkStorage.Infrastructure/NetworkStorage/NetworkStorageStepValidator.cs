using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Step-level save diagnostics for endpoint and workflow definitions. Every rule
/// mirrors what <see cref="EndpointStepExecutor"/> and <see cref="RecordOperations"/>
/// accept at runtime, plus the editor source compiler's validate_steps checks, so a
/// definition that saves cleanly is one the executor can run.
/// </summary>
internal sealed partial class NetworkStorageStepValidator
{
    // EndpointStepExecutor.SupportedTypes plus the workflow sub-flow step.
    internal static readonly HashSet<string> SupportedStepTypes = new(StringComparer.Ordinal)
    {
        "read", "write", "delete", "lookup", "filter", "lookup_many", "random_select",
        "condition", "assert", "transform", "object", "array", "merge", "sort", "switch", "compute",
        "random", "block", "workflow", "webhook", "sleep", "response",
    };

    // Canonical in the editor compiler but not executed by either backend.
    private static readonly HashSet<string> LoopFamily = new(StringComparer.Ordinal)
    {
        "while", "until", "foreach", "call", "return", "return_on",
    };

    internal static readonly string[] WriteOps = ["set", "inc", "push", "pull", "remove", "delete", "merge", "set_if_null"];
    private static readonly string[] RouteActions = ["continue", "return", "reject", "goto", "skip", "run"];
    private static readonly HashSet<string> ReservedContextKeys = new(StringComparer.Ordinal)
    {
        "input", "values", "steamId", "playerKey", "now", "projectId", "userId",
    };

    private const int MaxSleepMs = 300_000;

    private readonly DiagnosticBag _bag;
    private readonly DefinitionValidationContext _context;
    private readonly bool _workflow;
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);
    private List<string> _topLevelKeys = [];

    internal NetworkStorageStepValidator(DiagnosticBag bag, DefinitionValidationContext context, bool workflow)
    {
        _bag = bag;
        _context = context;
        _workflow = workflow;
    }

    internal void ValidateSteps(JsonElement steps, string path)
    {
        if (steps.ValueKind != JsonValueKind.Array)
        {
            _bag.Error("INVALID_STEPS", "steps must be a list of step objects.", path);
            return;
        }
        _topLevelKeys = steps.EnumerateArray()
            .Select(step => Str(step, "as") ?? Str(step, "id"))
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .ToList();
        ValidateList(steps, path, topLevel: true);
    }

    private void ValidateList(JsonElement steps, string path, bool topLevel)
    {
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            ValidateStep(step, $"{path}/{index}", topLevel ? index : -1);
            index++;
        }
    }

    private void ValidateStep(JsonElement step, string path, int topLevelIndex)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_STEP", "Each step must be an object with id and type.", path);
            return;
        }

        var id = Str(step, "id");
        var type = Str(step, "type");
        var label = $"Step \"{id ?? "?"}\"{(type is null ? "" : $" ({type})")}";
        if (string.IsNullOrWhiteSpace(id))
            _bag.Error("MISSING_STEP_ID", $"{label}: every step needs an id so later steps can read its result.", path + "/id");
        else
        {
            if (!StepIdPattern().IsMatch(id))
                _bag.Warning("STEP_ID_FORMAT", $"{label}: use letters, numbers and underscores in step ids (for example {id.Replace('-', '_')}) so expressions can reference the result.", path + "/id");
            if (ReservedContextKeys.Contains(id))
                _bag.Warning("RESERVED_STEP_ID", $"{label}: the id \"{id}\" replaces the built-in {{{{{id}}}}} value for later steps. Pick another id.", path + "/id");
        }

        var key = Str(step, "as") ?? id;
        if (!string.IsNullOrEmpty(key) && !_seenKeys.Add(key))
            _bag.Error("DUPLICATE_STEP_ID", $"{label}: the result name \"{key}\" is already used by another step. Step ids and as aliases must be unique.", path + "/id");

        if (string.IsNullOrWhiteSpace(type))
        {
            _bag.Error("MISSING_STEP_TYPE", $"{label}: set type to one of {string.Join(", ", SupportedStepTypes.Order())}.", path + "/type");
            return;
        }
        if (!SupportedStepTypes.Contains(type))
        {
            var message = LoopFamily.Contains(type)
                ? $"{label}: \"{type}\" steps are not supported by this server's executor. Use a condition with goto routes or a workflow step instead."
                : $"{label}: unknown step type \"{type}\". Use one of {string.Join(", ", SupportedStepTypes.Order())}.";
            _bag.Error("INVALID_STEP_TYPE", message, path + "/type");
            return;
        }

        if (step.TryGetProperty("when", out var when))
            ValidateCondition(when, path + "/when", $"{label} when");
        if (step.TryGetProperty("let", out var let) && let.ValueKind != JsonValueKind.Object)
            _bag.Error("INVALID_LET", $"{label}: let must map alias names to paths.", path + "/let");

        switch (type)
        {
            case "read":
                RequireCollection(step, path, label);
                RequireKey(step, path, label);
                ValidateRequired(step, path, label);
                break;
            case "write":
                RequireCollection(step, path, label);
                RequireKey(step, path, label);
                ValidateWriteOps(step, path, label);
                break;
            case "delete":
                RequireCollection(step, path, label);
                RequireKey(step, path, label);
                break;
            case "lookup" or "filter" or "random_select":
                ValidateScanSource(step, path, label);
                ValidateWhere(step, path, label, required: true);
                ValidateRequired(step, path, label);
                break;
            case "lookup_many":
                ValidateScanSource(step, path, label);
                ValidateLookupMany(step, path, label);
                break;
            case "condition":
                ValidateConditionStep(step, path, label, topLevelIndex);
                break;
            case "assert":
                if (!step.TryGetProperty("check", out var assertCheck))
                    _bag.Error("MISSING_CHECK", $"{label}: assert needs a check condition.", path + "/check");
                else ValidateCondition(assertCheck, path + "/check", label);
                ValidateStatus(step, "status", path, label);
                break;
            case "transform":
                if (!step.TryGetProperty("expression", out _) && !step.TryGetProperty("value", out _))
                    _bag.Error("MISSING_TRANSFORM_VALUE", $"{label}: set expression (math or a function) or value (a template).", path);
                else if (step.TryGetProperty("expression", out var expression) && expression.ValueKind != JsonValueKind.String)
                    _bag.Error("INVALID_EXPRESSION", $"{label}: expression must be text, for example \"{{{{input.amount}}}} * 2\".", path + "/expression");
                break;
            case "object":
                if (!(TryObject(step, "fields") || TryObject(step, "value")))
                    _bag.Error("MISSING_OBJECT_FIELDS", $"{label}: fields must be an object of output keys.", path + "/fields");
                break;
            case "array":
                if (step.TryGetProperty("items", out var items) && items.ValueKind != JsonValueKind.Array)
                    _bag.Error("INVALID_ARRAY_ITEMS", $"{label}: items must be a list.", path + "/items");
                break;
            case "merge":
                if (!step.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() == 0)
                    _bag.Error("MISSING_MERGE_SOURCES", $"{label}: sources must list the objects to merge.", path + "/sources");
                break;
            case "sort":
                if (Str(step, "source") is null)
                    _bag.Error("MISSING_SORT_SOURCE", $"{label}: source must be a template that resolves to a list, for example \"{{{{rows}}}}\".", path + "/source");
                if (Str(step, "direction") is { } direction && direction is not ("asc" or "desc"))
                    _bag.Error("INVALID_SORT_DIRECTION", $"{label}: direction must be asc or desc.", path + "/direction");
                break;
            case "switch":
                ValidateSwitch(step, path, label);
                break;
            case "compute":
                if (!(TryObject(step, "values") || TryObject(step, "fields")))
                    _bag.Error("MISSING_COMPUTE_VALUES", $"{label}: values must map output names to expressions.", path + "/values");
                if (Str(step, "output") is { } output && output != "scalars")
                    _bag.Warning("COMPUTE_OUTPUT", $"{label}: output only supports \"scalars\"; anything else stores the object under the step id.", path + "/output");
                break;
            case "random":
                ValidateRandom(step, path, label);
                break;
            case "block":
                if (!step.TryGetProperty("steps", out var children) || children.ValueKind != JsonValueKind.Array)
                    _bag.Error("MISSING_BLOCK_STEPS", $"{label}: a block needs a steps list.", path + "/steps");
                else ValidateList(children, path + "/steps", topLevel: false);
                break;
            case "workflow":
                ValidateWorkflowStep(step, path, label);
                break;
            case "webhook":
                ValidateWebhook(step, path, label);
                break;
            case "sleep":
                ValidateSleep(step, path, label);
                break;
        }
    }

    private void RequireCollection(JsonElement step, string path, string label)
    {
        var collection = Str(step, "collection");
        if (string.IsNullOrWhiteSpace(collection))
        {
            var hint = step.TryGetProperty("table", out _) ? " Rename table to collection." : "";
            _bag.Error("MISSING_COLLECTION", $"{label}: choose the collection to use.{hint}", path + "/collection");
            return;
        }
        if (_context.HasProjectResources && !collection.Contains("{{", StringComparison.Ordinal)
            && !_context.Collections.Contains(collection))
            _bag.Warning("COLLECTION_NOT_FOUND", $"{label}: collection \"{collection}\" does not exist in this project yet. Create it before calling this endpoint.", path + "/collection");
    }

    private void RequireKey(JsonElement step, string path, string label)
    {
        if (!step.TryGetProperty("key", out var key) || key.ValueKind is JsonValueKind.Null
            || (key.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(key.GetString())))
            _bag.Error("MISSING_KEY", $"{label}: set the record key, for example \"{{{{steamId}}}}\".", path + "/key");
    }

    private void ValidateRequired(JsonElement step, string path, string label)
    {
        if (step.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            _bag.Error("INVALID_REQUIRED", $"{label}: required must be true or false.", path + "/required");
        if (step.TryGetProperty("onMissing", out var onMissing))
        {
            if (onMissing.ValueKind != JsonValueKind.Object)
                _bag.Error("INVALID_ON_MISSING", $"{label}: onMissing must be an object with status, errorCode and message.", path + "/onMissing");
            else ValidateStatus(onMissing, "status", path + "/onMissing", label);
        }
    }

    private void ValidateScanSource(JsonElement step, string path, string label)
    {
        if (Str(step, "source") == "values")
        {
            var table = Str(step, "table") ?? Str(step, "collection");
            if (string.IsNullOrWhiteSpace(table))
                _bag.Error("MISSING_VALUES_TABLE", $"{label}: source values needs the game values table name in table.", path + "/table");
            else if (_context.HasProjectResources && !_context.ValueTables.Contains(table))
                _bag.Warning("VALUES_TABLE_NOT_FOUND", $"{label}: game values table \"{table}\" does not exist yet. Add it under Game values.", path + "/table");
            return;
        }
        if (step.TryGetProperty("source", out var source) && Str(step, "source") is { } other)
            _bag.Error("INVALID_SCAN_SOURCE", $"{label}: source \"{other}\" is not supported. Use source: values with a table, or set collection.", path + "/source");
        else if (source.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && source.ValueKind != JsonValueKind.String)
            _bag.Error("INVALID_SCAN_SOURCE", $"{label}: source must be \"values\" or omitted.", path + "/source");
        RequireCollection(step, path, label);
    }

    private void ValidateLookupMany(JsonElement step, string path, string label)
    {
        var name = step.TryGetProperty("keys", out _) ? "keys" : "values";
        if (!step.TryGetProperty(name, out var keys)
            || (keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() == 0)
            || keys.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
            _bag.Error("MISSING_LOOKUP_KEYS", $"{label}: keys must be a non-empty list or a template that resolves to a list.", path + "/keys");
        if (step.TryGetProperty("asMap", out var asMap) && asMap.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            _bag.Error("INVALID_AS_MAP", $"{label}: asMap must be true or false.", path + "/asMap");
        if (step.TryGetProperty("keyField", out var keyField) && keyField.ValueKind != JsonValueKind.String)
            _bag.Error("INVALID_KEY_FIELD", $"{label}: keyField must be a field name.", path + "/keyField");
    }

    private void ValidateSwitch(JsonElement step, string path, string label)
    {
        if (!step.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() == 0)
        {
            _bag.Error("MISSING_SWITCH_CASES", $"{label}: cases must list when/then pairs.", path + "/cases");
            return;
        }
        var index = 0;
        foreach (var entry in cases.EnumerateArray())
        {
            var casePath = $"{path}/cases/{index}";
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("when", out var when))
                _bag.Error("INVALID_SWITCH_CASE", $"{label}: case {index + 1} needs a when condition and a then value.", casePath);
            else
            {
                ValidateCondition(when, casePath + "/when", $"{label} case {index + 1}");
                if (!entry.TryGetProperty("then", out _))
                    _bag.Warning("SWITCH_CASE_THEN", $"{label}: case {index + 1} has no then value and resolves to null.", casePath + "/then");
            }
            index++;
        }
    }

    private void ValidateRandom(JsonElement step, string path, string label)
    {
        var mode = Str(step, "mode") ?? "int";
        if (mode is not ("int" or "float" or "weighted" or "beta"))
        {
            _bag.Error("INVALID_RANDOM_MODE", $"{label}: mode must be int, float, weighted or beta.", path + "/mode");
            return;
        }
        if (mode != "weighted") return;
        var hasItems = step.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
        var hasFrom = Str(step, "from") is not null;
        var hasTable = Str(step, "source") == "values" && Str(step, "table") is not null;
        if (!hasItems && !hasFrom && !hasTable)
            _bag.Error("MISSING_RANDOM_ITEMS", $"{label}: weighted mode needs items, from (a template list) or source: values with a table.", path + "/items");
        if (step.TryGetProperty("where", out var where) && where.ValueKind == JsonValueKind.Object && where.EnumerateObject().Any())
        {
            if (Str(where, "field") is null || Str(where, "op") is null)
                _bag.Error("INVALID_WHERE", $"{label}: a weighted filter needs field and op.", path + "/where");
            else CheckOp(Str(where, "op")!, path + "/where/op", label);
        }
    }

    private void ValidateWorkflowStep(JsonElement step, string path, string label)
    {
        var workflow = Str(step, "workflow");
        if (string.IsNullOrWhiteSpace(workflow))
        {
            var hint = Str(step, "target") is not null ? " This server reads the workflow field; rename target to workflow." : "";
            _bag.Error("MISSING_WORKFLOW_TARGET", $"{label}: choose the workflow to run.{hint}", path + "/workflow");
            return;
        }
        CheckWorkflowExists(workflow, path + "/workflow", label);
        if (step.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Object)
            _bag.Error("INVALID_PARAMS", $"{label}: params must map parameter names to values.", path + "/params");
    }

    private void CheckWorkflowExists(string workflow, string path, string label)
    {
        if (_workflow && workflow == _context.ResourceId)
            _bag.Warning("RECURSIVE_WORKFLOW", $"{label}: this workflow calls itself. Nested workflows stop after 5 levels with a 400 error.", path);
        else if (_context.HasProjectResources && !_context.Workflows.Contains(workflow))
            _bag.Error("WORKFLOW_NOT_FOUND", $"{label}: workflow \"{workflow}\" does not exist in this project. Create it first.", path);
    }

    private void ValidateWebhook(JsonElement step, string path, string label)
    {
        var url = Str(step, "url");
        if (string.IsNullOrWhiteSpace(url))
            _bag.Error("MISSING_WEBHOOK_URL", $"{label}: set the Discord webhook URL.", path + "/url");
        else if (!url.Contains("{{", StringComparison.Ordinal)
            && !url.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)
            && !url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal))
            _bag.Error("INVALID_WEBHOOK_URL", $"{label}: only Discord webhook URLs (https://discord.com/api/webhooks/...) are supported.", path + "/url");
        if (step.TryGetProperty("fields", out var fields) && fields.ValueKind != JsonValueKind.Array)
            _bag.Error("INVALID_WEBHOOK_FIELDS", $"{label}: fields must be a list of name/value pairs.", path + "/fields");
        if (Str(step, "color") is { } color && !HexColor().IsMatch(color))
            _bag.Warning("WEBHOOK_COLOR", $"{label}: color should be six hex digits such as 5865f2.", path + "/color");
    }

    private void ValidateSleep(JsonElement step, string path, string label)
    {
        string? name = null;
        foreach (var candidate in new[] { "durationMs", "ms", "sleepMs" })
            if (step.TryGetProperty(candidate, out _)) { name = candidate; break; }
        if (name is null)
        {
            var hint = step.TryGetProperty("delayMs", out _) ? " This server reads ms or durationMs; rename delayMs." : "";
            _bag.Error("INVALID_SLEEP_MS", $"{label}: set ms between 0 and {MaxSleepMs}.{hint}", path + "/ms");
            return;
        }
        var value = step.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var ms) || ms < 0 || ms > MaxSleepMs)
            _bag.Error("INVALID_SLEEP_MS", $"{label}: {name} must be a number between 0 and {MaxSleepMs}.", path + "/" + name);
    }

    private void ValidateStatus(JsonElement owner, string property, string path, string label)
    {
        if (!owner.TryGetProperty(property, out var status)) return;
        if (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code < 100 || code > 599)
            _bag.Error("INVALID_STATUS", $"{label}: {property} must be an HTTP status code between 100 and 599.", path + "/" + property);
    }

    private bool CheckOp(string op, string path, string label)
    {
        try
        {
            EndpointExpression.NormalizeOp(op);
            return true;
        }
        catch (ExpressionException)
        {
            _bag.Error("INVALID_CONDITION_OP", $"{label}: operator \"{op}\" is not supported. Use ==, !=, >, <, >=, <=, contains, not_contains, in, not_in, starts_with, not_starts_with, matches, exists or not_exists.", path);
            return false;
        }
    }

    private static bool TryObject(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    internal static string? Str(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, 100)]
    internal static partial Regex StepIdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{6}$", RegexOptions.None, 100)]
    private static partial Regex HexColor();
}
