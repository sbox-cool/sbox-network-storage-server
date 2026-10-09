using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>Condition, route, write-op and where-clause rules for <see cref="NetworkStorageStepValidator"/>.</summary>
internal sealed partial class NetworkStorageStepValidator
{
    internal void ValidateCondition(JsonElement check, string path, string label)
    {
        if (check.ValueKind is JsonValueKind.True or JsonValueKind.False && path.EndsWith("/when", StringComparison.Ordinal))
            return;
        if (check.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_CONDITION", $"{label}: a condition must be an object with field and op, an expression, or an all/any list.", path);
            return;
        }
        foreach (var group in new[] { "all", "any" })
        {
            if (!check.TryGetProperty(group, out var items)) continue;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                _bag.Error("INVALID_CONDITION_GROUP", $"{label}: {group} must be a non-empty list of conditions.", path + "/" + group);
                return;
            }
            var index = 0;
            foreach (var item in items.EnumerateArray())
                ValidateCondition(item, $"{path}/{group}/{index++}", label);
            return;
        }

        var field = check.TryGetProperty("field", out var f) && f.ValueKind is not JsonValueKind.Null ? f
            : check.TryGetProperty("left", out var l) ? l : default;
        var hasField = field.ValueKind == JsonValueKind.String ? field.GetString()!.Length > 0
            : field.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        var expression = Str(check, "expression");
        var op = Str(check, "op");
        if (check.TryGetProperty("expression", out var rawExpression) && rawExpression.ValueKind != JsonValueKind.String)
            _bag.Error("INVALID_EXPRESSION", $"{label}: expression must be text.", path + "/expression");

        if (!string.IsNullOrWhiteSpace(expression) && !hasField) return;
        if (!hasField)
        {
            _bag.Error("CONDITION_FIELD_REQUIRED", $"{label}: set field (the value to test, for example player.coins) and op, or an expression.", path + "/field");
            return;
        }
        if (string.IsNullOrEmpty(op))
        {
            _bag.Error("CONDITION_OP_REQUIRED", $"{label}: set op, for example >= or exists.", path + "/op");
            return;
        }
        if (!CheckOp(op, path + "/op", label) || !string.IsNullOrWhiteSpace(expression)) return;
        if (op is "exists" or "not_exists" or "not_exist" or "notexists") return;
        if (!check.TryGetProperty("value", out _) && !check.TryGetProperty("right", out _))
            _bag.Warning("CONDITION_VALUE_MISSING", $"{label}: no value to compare with, so the check compares against nothing.", path + "/value");
    }

    private void ValidateConditionStep(JsonElement step, string path, string label, int topLevelIndex)
    {
        var hasWorkflow = step.TryGetProperty("workflow", out var workflow);
        if (hasWorkflow)
        {
            if (workflow.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(workflow.GetString()))
                _bag.Error("MISSING_WORKFLOW_TARGET", $"{label}: workflow must name a saved workflow with a check.", path + "/workflow");
            else CheckWorkflowExists(workflow.GetString()!, path + "/workflow", label);
        }
        else if (!step.TryGetProperty("check", out var check))
            _bag.Error("MISSING_CHECK", $"{label}: a condition needs a check (or a saved workflow).", path + "/check");
        else ValidateCondition(check, path + "/check", label);

        if (step.TryGetProperty("routes", out var routes))
        {
            if (routes.ValueKind != JsonValueKind.Object)
                _bag.Error("INVALID_ROUTES", $"{label}: routes must have true and/or false entries.", path + "/routes");
            else foreach (var branch in routes.EnumerateObject())
            {
                if (branch.Name is not ("true" or "false" or "pass" or "fail"))
                    _bag.Warning("UNKNOWN_ROUTE", $"{label}: routes.{branch.Name} is ignored; use true or false.", $"{path}/routes/{branch.Name}");
                else ValidateRoute(branch.Value, $"{path}/routes/{branch.Name}", label, topLevelIndex, step);
            }
        }
        foreach (var name in new[] { "onTrue", "onFalse" })
            if (step.TryGetProperty(name, out var route))
                ValidateRoute(route, $"{path}/{name}", label, topLevelIndex, step);
        if (step.TryGetProperty("onFail", out var onFail))
            ValidateOnFail(onFail, path + "/onFail", label, topLevelIndex, step);
    }

    private void ValidateOnFail(JsonElement onFail, string path, string label, int topLevelIndex, JsonElement step)
    {
        if (onFail.ValueKind == JsonValueKind.String)
        {
            if (onFail.GetString() != "skip")
                _bag.Error("INVALID_ON_FAIL", $"{label}: onFail must be an object (status, error, message) or \"skip\".", path);
            return;
        }
        if (onFail.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_ON_FAIL", $"{label}: onFail must be an object with status, error and message.", path);
            return;
        }
        ValidateStatus(onFail, "status", path, label);
        if (onFail.TryGetProperty("clamp", out var clamp) && clamp.ValueKind == JsonValueKind.True)
            _bag.Warning("CLAMP_NOT_SUPPORTED", $"{label}: clamp is not supported by this server's executor; the failure rejects instead.", path + "/clamp");
        var skip = onFail.TryGetProperty("skip", out var s) ? s : onFail.TryGetProperty("skipSteps", out var ss) ? ss : default;
        if (skip.ValueKind == JsonValueKind.String && skip.GetString() != "next")
            CheckGotoTarget(skip.GetString()!, path + "/skip", label, topLevelIndex, step);
    }

    private void ValidateRoute(JsonElement route, string path, string label, int topLevelIndex, JsonElement step)
    {
        if (route.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) return;
        string action;
        if (route.ValueKind == JsonValueKind.String)
        {
            action = route.GetString()!;
            if (Array.IndexOf(RouteActions, action) < 0)
            {
                CheckGotoTarget(action, path, label, topLevelIndex, step);
                return;
            }
            if (action is "goto" or "run")
            {
                _bag.Error("ROUTE_TARGET_REQUIRED", $"{label}: a {action} route needs an object with {(action == "goto" ? "step" : "flow")}.", path);
                return;
            }
            CheckRouteInWorkflow(action, path, label);
            return;
        }
        if (route.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_ROUTE", $"{label}: a route must be an action name or an object with action.", path);
            return;
        }

        action = Str(route, "action") ?? Str(route, "type") ?? "";
        action = action switch { "step" => "goto", "workflow" or "flow" or "endpoint" => "run", _ => action };
        if (action.Length == 0)
        {
            _bag.Error("ROUTE_ACTION_REQUIRED", $"{label}: set the route action ({string.Join(", ", RouteActions)}).", path + "/action");
            return;
        }
        if (Array.IndexOf(RouteActions, action) < 0)
        {
            _bag.Error("INVALID_ROUTE_ACTION", $"{label}: route action \"{action}\" is not supported. Use {string.Join(", ", RouteActions)}.", path + "/action");
            return;
        }
        CheckRouteInWorkflow(action, path, label);
        switch (action)
        {
            case "goto":
                var target = Str(route, "step") ?? Str(route, "target") ?? Str(route, "goto");
                if (string.IsNullOrWhiteSpace(target))
                    _bag.Error("ROUTE_TARGET_REQUIRED", $"{label}: a goto route needs step (the id of the step to jump to).", path + "/step");
                else CheckGotoTarget(target, path + "/step", label, topLevelIndex, step);
                break;
            case "skip":
                if (route.TryGetProperty("count", out var count)
                    && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var n) || n < 1))
                    _bag.Error("INVALID_SKIP_COUNT", $"{label}: skip count must be a whole number of at least 1.", path + "/count");
                break;
            case "run":
                var flow = Str(route, "flow") ?? Str(route, "workflow") ?? Str(route, "endpoint") ?? Str(route, "target");
                if (string.IsNullOrWhiteSpace(flow))
                    _bag.Error("ROUTE_FLOW_REQUIRED", $"{label}: a run route needs flow (the workflow to run).", path + "/flow");
                else CheckWorkflowExists(flow, path + "/flow", label);
                if (route.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Object)
                    _bag.Error("INVALID_PARAMS", $"{label}: params must map parameter names to values.", path + "/params");
                break;
            case "reject" or "return":
                ValidateStatus(route, "status", path, label);
                break;
        }
    }

    private void CheckRouteInWorkflow(string action, string path, string label)
    {
        if (_workflow && action is "goto" or "skip" or "return")
            _bag.Warning("ROUTE_IGNORED_IN_WORKFLOW", $"{label}: {action} routes only run in endpoints; inside a workflow the step continues.", path);
    }

    private void CheckGotoTarget(string target, string path, string label, int topLevelIndex, JsonElement step)
    {
        var to = _topLevelKeys.IndexOf(target);
        if (to < 0)
        {
            _bag.Error("ROUTE_TARGET_MISSING", $"{label}: goto target \"{target}\" is not a top-level step id in this definition.", path);
            return;
        }
        if (topLevelIndex >= 0 && to <= topLevelIndex)
            _bag.Warning("ROUTE_BACKWARD_JUMP", $"{label}: jumping back to \"{target}\" repeats steps; each step may run at most 100 times before the request fails with 400.", path);
        if (topLevelIndex < 0 && step.ValueKind == JsonValueKind.Object)
            _bag.Warning("ROUTE_FROM_BLOCK", $"{label}: a goto inside a block jumps among the top-level steps.", path);
    }

    private void ValidateWriteOps(JsonElement step, string path, string label)
    {
        if (!step.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0)
        {
            var hint = step.TryGetProperty("value", out _) || step.TryGetProperty("data", out _)
                ? " Write steps change records through ops such as { op: set, path: name, value: ... }." : "";
            _bag.Error("MISSING_WRITE_OPS", $"{label}: add at least one op.{hint}", path + "/ops");
            return;
        }
        if (ops.GetArrayLength() > 100)
            _bag.Error("TOO_MANY_WRITE_OPS", $"{label}: at most 100 ops run per write.", path + "/ops");

        var index = 0;
        foreach (var op in ops.EnumerateArray())
            ValidateWriteOp(op, $"{path}/ops/{index}", $"{label} op {++index}");
    }

    private void ValidateWriteOp(JsonElement op, string path, string label)
    {
        if (op.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_WRITE_OP", $"{label}: each op must be an object with op, path and value.", path);
            return;
        }
        var name = Str(op, "op");
        if (string.IsNullOrWhiteSpace(name))
        {
            _bag.Error("INVALID_WRITE_OP", $"{label}: set op to one of {string.Join(", ", WriteOps)}.", path + "/op");
            return;
        }
        if (Array.IndexOf(WriteOps, name) < 0)
        {
            var hint = name switch
            {
                "increment" or "add" or "decrement" or "subtract" => " Use inc with a numeric value (negative to subtract).",
                "append" => " Use push.",
                "unset" => " Use set with value null.",
                _ => "",
            };
            _bag.Error("INVALID_WRITE_OP", $"{label}: \"{name}\" is not a write op. Use one of {string.Join(", ", WriteOps)}.{hint}", path + "/op");
            return;
        }
        if (string.IsNullOrWhiteSpace(Str(op, "path")))
            _bag.Error("MISSING_OP_PATH", $"{label}: set path (the record field to change, for example coins).", path + "/path");

        var hasValue = op.TryGetProperty("value", out var value);
        var hasExpression = op.TryGetProperty("valueExpression", out var expression) || op.TryGetProperty("expression", out expression);
        if (op.TryGetProperty("amount", out _) && !hasValue && !hasExpression)
        {
            _bag.Error("MISSING_OP_VALUE", $"{label}: rename amount to value.", path + "/amount");
            return;
        }
        if (hasExpression && expression.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            _bag.Error("INVALID_EXPRESSION", $"{label}: valueExpression must be a math expression.", path + "/valueExpression");

        switch (name)
        {
            case "pull":
                if (!op.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object)
                {
                    var hint = op.TryGetProperty("where", out _) ? " Rename where to match." : " For plain values use match: { value: ... }.";
                    _bag.Error("PULL_REQUIRES_MATCH", $"{label}: pull needs a match object listing the fields an item must have to be removed.{hint}", path + "/match");
                }
                break;
            case "inc":
                if (!hasValue && !hasExpression)
                    _bag.Error("MISSING_OP_VALUE", $"{label}: inc needs a numeric value (or valueExpression).", path + "/value");
                else if (hasValue && !hasExpression && !IsNumberOrTemplate(value))
                    _bag.Error("INC_VALUE_NOT_NUMERIC", $"{label}: inc value must be a number or a template such as \"{{{{input.amount}}}}\".", path + "/value");
                break;
            case "merge":
                if (!hasValue)
                    _bag.Error("MISSING_OP_VALUE", $"{label}: merge needs an object value.", path + "/value");
                else if (value.ValueKind != JsonValueKind.Object && !IsTemplate(value))
                    _bag.Error("MERGE_VALUE_NOT_OBJECT", $"{label}: merge value must be an object.", path + "/value");
                break;
            default:
                if (!hasValue && !hasExpression)
                    _bag.Error("MISSING_OP_VALUE", $"{label}: {name} needs a value.", path + "/value");
                break;
        }

        var when = op.TryGetProperty("when", out var w) ? w : op.TryGetProperty("if", out var i) ? i : default;
        if (when.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined) return;
        if (when.ValueKind != JsonValueKind.Object)
            _bag.Error("INVALID_OP_WHEN", $"{label}: when must be a condition object or true/false.", path + "/when");
        else ValidateCondition(when, path + "/when", label);
    }

    private void ValidateWhere(JsonElement step, string path, string label, bool required)
    {
        if (!step.TryGetProperty("where", out var where))
        {
            if (required) _bag.Error("WHERE_REQUIRED", $"{label}: add a where clause (field, op, value) to choose rows.", path + "/where");
            return;
        }
        if (where.ValueKind != JsonValueKind.Object
            || !(where.TryGetProperty("field", out _) || where.TryGetProperty("left", out _) || where.TryGetProperty("all", out _) || where.TryGetProperty("any", out _)))
        {
            _bag.Error("WHERE_REQUIRED", $"{label}: where must be an object with field/op/value or an all/any list.", path + "/where");
            return;
        }
        ValidateWhereClause(where, path + "/where", label);
    }

    private void ValidateWhereClause(JsonElement clause, string path, string label)
    {
        if (clause.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in clause.EnumerateArray()) ValidateWhereClause(item, $"{path}/{i++}", label);
            return;
        }
        if (clause.ValueKind != JsonValueKind.Object)
        {
            _bag.Error("INVALID_WHERE", $"{label}: each where clause must be an object.", path);
            return;
        }
        foreach (var group in new[] { "all", "any" })
        {
            if (!clause.TryGetProperty(group, out var items)) continue;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                _bag.Error("INVALID_WHERE", $"{label}: where {group} must be a non-empty list.", path + "/" + group);
            else
            {
                var i = 0;
                foreach (var item in items.EnumerateArray()) ValidateWhereClause(item, $"{path}/{group}/{i++}", label);
            }
            return;
        }
        if ((Str(clause, "field") ?? Str(clause, "left")) is null)
            _bag.Error("INVALID_WHERE", $"{label}: a where clause needs field (a row field such as item_id).", path + "/field");
        var op = Str(clause, "op");
        if (op is null) _bag.Error("INVALID_WHERE", $"{label}: a where clause needs op.", path + "/op");
        else CheckOp(op, path + "/op", label);
    }

    private static bool IsTemplate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString()!.Contains("{{", StringComparison.Ordinal);

    private static bool IsNumberOrTemplate(JsonElement value) => value.ValueKind == JsonValueKind.Number || IsTemplate(value);
}
