using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>One save-time finding. Errors block saving; warnings are advisory.</summary>
public sealed record DefinitionDiagnostic(string Severity, string Code, string Message, string Path)
{
    public bool IsError => Severity == "error";
}

/// <summary>Project resources a definition may reference, used for cross-reference checks.</summary>
public sealed class DefinitionValidationContext
{
    public static readonly DefinitionValidationContext None = new();

    /// <summary>True when the reference sets below describe the project; otherwise cross-reference checks are skipped.</summary>
    public bool HasProjectResources { get; init; }
    public IReadOnlySet<string> Collections { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Workflows { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> ValueTables { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>The id of the resource being saved (detects self-referencing workflows).</summary>
    public string? ResourceId { get; init; }
}

internal sealed class DiagnosticBag
{
    public List<DefinitionDiagnostic> Items { get; } = [];
    public void Error(string code, string message, string path) => Items.Add(new("error", code, message, path));
    public void Warning(string code, string message, string path) => Items.Add(new("warning", code, message, path));
}

/// <summary>
/// Save-time diagnostics for owner-authored definitions. Compiles source-backed
/// resources with the same compiler as editor sync, then checks the compiled
/// definition against what the native executor, record operations and query
/// executor accept, so the dashboard never stores a definition that fails when
/// called. Rules port the editor's source_compiler.py validate_definition and
/// validate_steps plus the hosted step-editor route and schema checks.
/// </summary>
public static class NetworkStorageDefinitionValidator
{
    private static readonly HashSet<string> CommonKeys = new(StringComparer.Ordinal)
    {
        "id", "slug", "name", "description", "notes", "metadata", "sourceVersion", "imports", "libraries", "dslVersion",
        "authoringMode", "sourceFormat", "sourcePath", "sourceText", "sourcePathSystemGenerated", "version", "createdAt", "updatedAt",
    };
    private static readonly Dictionary<string, HashSet<string>> BodyKeys = new(StringComparer.Ordinal)
    {
        ["endpoint"] = new(StringComparer.Ordinal) { "method", "enabled", "deprecated", "input", "steps", "response", "rateLimits", "skipSboxAuth", "requiresSecretKey", "requireReauth", "exposure", "publiclyCallable", "internalOnly", "internal", "visibility", "routes", "let" },
        ["workflow"] = new(StringComparer.Ordinal) { "condition", "check", "onFail", "params", "input", "steps", "returns", "response", "enabled", "exposure", "internalOnly", "visibility", "budgets", "routes", "let" },
        ["collection"] = new(StringComparer.Ordinal) { "schema", "fields", "collectionType", "accessMode", "visibility", "rateLimits", "rateLimitAction", "webhookOnRateLimit", "maxRecords", "allowRecordDelete", "requireSaveVersion", "constants", "tables" },
        ["query"] = new(StringComparer.Ordinal) { "type", "sources", "config", "cache", "requiresSecretKey", "enabled" },
    };

    public static readonly string[] QueryTypes = ["leaderboard", "count", "sum", "average", "min", "max"];

    /// <summary>Compiles a stored resource wrapper (source-backed or plain) and validates the result.</summary>
    public static IReadOnlyList<DefinitionDiagnostic> ValidateResource(
        JsonElement resource, string kind, DefinitionValidationContext context, out JsonElement compiled)
    {
        compiled = resource;
        if (kind == "game-values") return ValidateGameValues(resource);
        if (!NetworkStorageSourceResourceCompiler.TryCompile(resource, kind, out compiled, out var error))
            return [new DefinitionDiagnostic("error", "SOURCE_COMPILE_FAILED", error ?? "Source compilation failed.", "/")];
        return Validate(kind, compiled, context);
    }

    /// <summary>Validates an already compiled definition of the given kind.</summary>
    public static IReadOnlyList<DefinitionDiagnostic> Validate(string kind, JsonElement definition, DefinitionValidationContext context)
    {
        var bag = new DiagnosticBag();
        if (definition.ValueKind != JsonValueKind.Object)
        {
            bag.Error("INVALID_DEFINITION", "The definition must be an object.", "/");
            return bag.Items;
        }
        if (BodyKeys.TryGetValue(kind, out var known))
            foreach (var property in definition.EnumerateObject())
                if (!known.Contains(property.Name) && !CommonKeys.Contains(property.Name))
                    bag.Warning("UNKNOWN_TOP_LEVEL_FIELD", $"\"{property.Name}\" is not a {kind} setting and is ignored.", "/" + property.Name);

        switch (kind)
        {
            case "endpoint": ValidateEndpoint(definition, bag, context); break;
            case "workflow": ValidateWorkflow(definition, bag, context); break;
            case "collection": NetworkStorageSchemaValidator.ValidateCollection(definition, bag); break;
            case "query": ValidateQuery(definition, bag, context); break;
            case "game-values": bag.Items.AddRange(ValidateGameValues(definition)); break;
        }
        return bag.Items;
    }

    private static void ValidateEndpoint(JsonElement definition, DiagnosticBag bag, DefinitionValidationContext context)
    {
        if (definition.TryGetProperty("method", out var method)
            && (method.ValueKind != JsonValueKind.String || method.GetString() is not ("GET" or "POST")))
            bag.Error("INVALID_METHOD", "Endpoint method must be GET or POST. Game clients only send GET (input from the query string) or POST (input from the JSON body).", "/method");
        foreach (var flag in new[] { "enabled", "deprecated", "requiresSecretKey", "internalOnly", "skipSboxAuth" })
            RequireBool(definition, flag, bag);
        ValidateExposure(definition, bag);
        ValidateInputSchema(definition, bag);
        ValidateLet(definition, bag);

        if (!definition.TryGetProperty("steps", out var steps))
            bag.Warning("NO_STEPS", "This endpoint has no steps; it only returns its response.", "/steps");
        else new NetworkStorageStepValidator(bag, context, workflow: false).ValidateSteps(steps, "/steps");

        if (definition.TryGetProperty("response", out var response))
        {
            if (response.ValueKind != JsonValueKind.Object)
                bag.Error("INVALID_RESPONSE", "response must be an object with status and body.", "/response");
            else
            {
                if (response.TryGetProperty("status", out var status)
                    && (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code < 100 || code > 599))
                    bag.Error("INVALID_STATUS", "response status must be an HTTP status code between 100 and 599.", "/response/status");
                if (response.TryGetProperty("echo", out var echo) && echo.ValueKind != JsonValueKind.Array)
                    bag.Error("INVALID_ECHO", "response echo must be a list of templates such as \"{{player.coins}}\".", "/response/echo");
                foreach (var property in response.EnumerateObject())
                    if (property.Name is not ("status" or "body" or "echo"))
                        bag.Warning("RESPONSE_FIELD_IGNORED", $"response.{property.Name} is ignored. Put returned data under response.body.", "/response/" + property.Name);
            }
        }
    }

    private static void ValidateWorkflow(JsonElement definition, DiagnosticBag bag, DefinitionValidationContext context)
    {
        ValidateExposure(definition, bag);
        ValidateLet(definition, bag);
        var hasSteps = definition.TryGetProperty("steps", out var steps);
        if (hasSteps) new NetworkStorageStepValidator(bag, context, workflow: true).ValidateSteps(steps, "/steps");
        var checkName = definition.TryGetProperty("check", out _) ? "check" : definition.TryGetProperty("condition", out _) ? "condition" : null;
        if (checkName is not null)
            new NetworkStorageStepValidator(bag, context, workflow: true)
                .ValidateCondition(definition.GetProperty(checkName), "/" + checkName, "Workflow " + checkName);
        if (!hasSteps && checkName is null)
            bag.Warning("NO_STEPS", "This workflow has no steps and no check, so calling it does nothing.", "/steps");
        if (definition.TryGetProperty("params", out var parameters) && parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            bag.Error("INVALID_PARAMS", "params must describe parameter names (for example amount: { type: number }).", "/params");
        if (definition.TryGetProperty("returns", out var returns) && returns.ValueKind != JsonValueKind.Object)
            bag.Error("INVALID_RETURNS", "returns must map output names to templates.", "/returns");
        if (definition.TryGetProperty("onFail", out var onFail) && onFail.ValueKind != JsonValueKind.Object)
            bag.Error("INVALID_ON_FAIL", "onFail must be an object with status, errorCode and message.", "/onFail");
    }

    private static void ValidateQuery(JsonElement definition, DiagnosticBag bag, DefinitionValidationContext context)
    {
        var type = NetworkStorageStepValidator.Str(definition, "type") ?? "leaderboard";
        if (definition.TryGetProperty("type", out var rawType) && rawType.ValueKind != JsonValueKind.String || Array.IndexOf(QueryTypes, type) < 0)
            bag.Error("INVALID_QUERY_TYPE", $"Query type must be one of {string.Join(", ", QueryTypes)}.", "/type");
        RequireBool(definition, "requiresSecretKey", bag);

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        if (!definition.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() == 0)
            bag.Error("QUERY_SOURCES_REQUIRED", "sources must list at least one collection (collectionId).", "/sources");
        else
        {
            var index = 0;
            foreach (var source in sources.EnumerateArray())
            {
                var path = $"/sources/{index++}";
                var collectionId = NetworkStorageStepValidator.Str(source, "collectionId");
                if (string.IsNullOrWhiteSpace(collectionId))
                {
                    var hint = NetworkStorageStepValidator.Str(source, "collection") is not null ? " Rename collection to collectionId." : "";
                    bag.Error("SOURCE_COLLECTION_REQUIRED", $"Source {index} needs collectionId.{hint}", path + "/collectionId");
                    continue;
                }
                CheckCollection(collectionId, path + "/collectionId", bag, context);
                var alias = NetworkStorageStepValidator.Str(source, "alias") ?? NetworkStorageStepValidator.Str(source, "as") ?? collectionId;
                if (!aliases.Add(alias))
                    bag.Error("DUPLICATE_SOURCE_ALIAS", $"Source alias \"{alias}\" is used twice; give each source a unique alias.", path + "/alias");
            }
        }

        var config = definition.TryGetProperty("config", out var c) ? c : default;
        if (config.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            bag.Error("INVALID_QUERY_CONFIG", "config must be an object.", "/config");
            config = default;
        }
        var hasMetric = config.ValueKind == JsonValueKind.Object
            && (config.TryGetProperty("field", out _) || config.TryGetProperty("valueExpression", out _)
                || config.TryGetProperty("fieldExpression", out _) || config.TryGetProperty("expression", out _));
        if (type != "count" && Array.IndexOf(QueryTypes, type) >= 0 && !hasMetric)
            bag.Error("QUERY_FIELD_REQUIRED", $"A {type} query needs config.field (the numeric field to rank or total, for example coins).", "/config/field");
        if (config.ValueKind != JsonValueKind.Object) return;

        if (config.TryGetProperty("limit", out var limit)
            && (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var n) || n < 1 || n > 1000))
            bag.Error("INVALID_QUERY_LIMIT", "config.limit must be a whole number from 1 to 1000.", "/config/limit");
        if (NetworkStorageStepValidator.Str(config, "order") is { } order && order is not ("asc" or "desc"))
            bag.Error("INVALID_QUERY_ORDER", "config.order must be asc or desc.", "/config/order");
        if (type == "count" && NetworkStorageStepValidator.Str(config, "condition") is { } condition
            && condition is not ("eq" or "neq" or "gt" or "gte" or "lt" or "lte" or "exists" or "not_exists"))
            bag.Error("INVALID_COUNT_CONDITION", "config.condition must be eq, neq, gt, gte, lt, lte, exists or not_exists.", "/config/condition");
        if (config.TryGetProperty("fields", out var fields) && fields.ValueKind != JsonValueKind.Array)
            bag.Error("INVALID_QUERY_FIELDS", "config.fields must be a list of output fields.", "/config/fields");
        if (config.TryGetProperty("joins", out var joins))
        {
            if (joins.ValueKind != JsonValueKind.Array)
                bag.Error("INVALID_QUERY_JOINS", "config.joins must be a list.", "/config/joins");
            else
            {
                var index = 0;
                foreach (var join in joins.EnumerateArray())
                {
                    var path = $"/config/joins/{index++}";
                    var cid = NetworkStorageStepValidator.Str(join, "sourceCollectionId") ?? NetworkStorageStepValidator.Str(join, "collectionId");
                    if (string.IsNullOrWhiteSpace(cid))
                        bag.Error("JOIN_COLLECTION_REQUIRED", $"Join {index} needs sourceCollectionId.", path + "/sourceCollectionId");
                    else CheckCollection(cid, path + "/sourceCollectionId", bag, context);
                    if (string.IsNullOrWhiteSpace(NetworkStorageStepValidator.Str(join, "localKey")))
                        bag.Error("JOIN_KEY_REQUIRED", $"Join {index} needs localKey (the field on each row that holds the other record's key, or _key).", path + "/localKey");
                    if (NetworkStorageStepValidator.Str(join, "type") is { } joinType && joinType is not ("inner" or "left"))
                        bag.Error("INVALID_JOIN_TYPE", $"Join {index} type must be inner or left.", path + "/type");
                    var alias = NetworkStorageStepValidator.Str(join, "alias") ?? cid ?? "";
                    if (alias.Length > 0 && !aliases.Add(alias))
                        bag.Error("DUPLICATE_SOURCE_ALIAS", $"Join alias \"{alias}\" is already used.", path + "/alias");
                }
            }
        }
        if (definition.TryGetProperty("cache", out var cache))
        {
            if (cache.ValueKind != JsonValueKind.Object
                || (cache.TryGetProperty("ttlSeconds", out var ttl) && (!ttl.TryGetInt32(out var seconds) || seconds < 0 || seconds > 86_400)))
                bag.Error("INVALID_CACHE_TTL", "cache.ttlSeconds must be a whole number of seconds from 0 to 86400.", "/cache/ttlSeconds");
        }
    }

    /// <summary>Game values: items of type group (entries) or table (typed columns and rows).</summary>
    public static IReadOnlyList<DefinitionDiagnostic> ValidateGameValues(JsonElement values)
    {
        var bag = new DiagnosticBag();
        if (values.ValueKind != JsonValueKind.Object)
        {
            bag.Error("INVALID_GAME_VALUES", "Game values must be an object with an items list.", "/");
            return bag.Items;
        }
        var hasItems = values.TryGetProperty("items", out var items);
        var legacy = values.TryGetProperty("groups", out _) || values.TryGetProperty("tables", out _);
        foreach (var property in values.EnumerateObject())
            if (property.Name is not ("items" or "groups" or "tables") && !CommonKeys.Contains(property.Name))
                bag.Warning("GAME_VALUE_IGNORED", $"\"{property.Name}\" is ignored. Endpoints read groups and tables listed under items.", "/" + property.Name);
        if (!hasItems)
        {
            if (!legacy) bag.Error("EMPTY_GAME_VALUES", "Add at least one group or table under items.", "/items");
            return bag.Items;
        }
        if (legacy)
            bag.Warning("LEGACY_GAME_VALUES", "groups and tables are ignored while items is present. Move them into items.", "/");
        if (items.ValueKind != JsonValueKind.Array)
        {
            bag.Error("INVALID_GAME_VALUES", "items must be a list of groups and tables.", "/items");
            return bag.Items;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var path = $"/items/{index++}";
            var id = NetworkStorageStepValidator.Str(item, "id");
            if (string.IsNullOrWhiteSpace(id) || !NetworkStorageStepValidator.StepIdPattern().IsMatch(id))
            {
                bag.Error("INVALID_GAME_VALUE_ID", $"Item {index} needs an id of letters, numbers and underscores (used as {{{{values.<id>}}}}).", path + "/id");
                continue;
            }
            if (!ids.Add(id)) bag.Error("DUPLICATE_GAME_VALUE_ID", $"\"{id}\" is used by more than one item.", path + "/id");
            var type = NetworkStorageStepValidator.Str(item, "type") ?? "group";
            if (type == "group")
            {
                if (!item.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                    bag.Error("INVALID_GROUP_ENTRIES", $"Group \"{id}\" needs entries (key: value pairs).", path + "/entries");
                else foreach (var entry in entries.EnumerateObject())
                    if (entry.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        bag.Warning("GROUP_ENTRY_SHAPE", $"Group \"{id}\" entry \"{entry.Name}\" is not a plain value.", $"{path}/entries/{entry.Name}");
            }
            else if (type == "table") ValidateTable(item, id, path, bag);
            else bag.Error("INVALID_GAME_VALUE_TYPE", $"\"{id}\" type must be group or table.", path + "/type");
        }
        return bag.Items;
    }

    private static void ValidateTable(JsonElement item, string id, string path, DiagnosticBag bag)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("columns", out var cols))
        {
            if (cols.ValueKind != JsonValueKind.Array) bag.Error("INVALID_TABLE_COLUMNS", $"Table \"{id}\" columns must be a list.", path + "/columns");
            else
            {
                var c = 0;
                foreach (var column in cols.EnumerateArray())
                {
                    var key = NetworkStorageStepValidator.Str(column, "key");
                    var type = NetworkStorageStepValidator.Str(column, "type") ?? "string";
                    if (string.IsNullOrWhiteSpace(key))
                        bag.Error("INVALID_TABLE_COLUMN", $"Table \"{id}\" column {c + 1} needs a key.", $"{path}/columns/{c}/key");
                    else if (type is not ("string" or "number" or "boolean"))
                        bag.Error("INVALID_TABLE_COLUMN", $"Table \"{id}\" column \"{key}\" type must be string, number or boolean.", $"{path}/columns/{c}/type");
                    else if (!columns.TryAdd(key, type))
                        bag.Error("INVALID_TABLE_COLUMN", $"Table \"{id}\" has two columns named \"{key}\".", $"{path}/columns/{c}/key");
                    c++;
                }
            }
        }
        if (!item.TryGetProperty("rows", out var rows)) return;
        if (rows.ValueKind != JsonValueKind.Array)
        {
            bag.Error("INVALID_TABLE_ROWS", $"Table \"{id}\" rows must be a list of objects.", path + "/rows");
            return;
        }
        var r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var rowPath = $"{path}/rows/{r++}";
            if (row.ValueKind != JsonValueKind.Object)
            {
                bag.Error("INVALID_TABLE_ROW", $"Table \"{id}\" row {r} must be an object.", rowPath);
                continue;
            }
            if (columns.Count == 0) continue;
            foreach (var cell in row.EnumerateObject())
            {
                if (!columns.TryGetValue(cell.Name, out var type))
                {
                    bag.Warning("UNKNOWN_TABLE_CELL", $"Table \"{id}\" row {r} has \"{cell.Name}\", which is not a column.", $"{rowPath}/{cell.Name}");
                    continue;
                }
                var ok = cell.Value.ValueKind == JsonValueKind.Null || type switch
                {
                    "number" => cell.Value.ValueKind == JsonValueKind.Number,
                    "boolean" => cell.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => cell.Value.ValueKind == JsonValueKind.String,
                };
                if (!ok) bag.Error("TABLE_CELL_TYPE", $"Table \"{id}\" row {r} \"{cell.Name}\" must be a {type}.", $"{rowPath}/{cell.Name}");
            }
        }
    }

    private static void ValidateExposure(JsonElement definition, DiagnosticBag bag)
    {
        if (NetworkStorageStepValidator.Str(definition, "exposure") is { } exposure && exposure is not ("public" or "internal"))
            bag.Error("INVALID_EXPOSURE", "exposure must be public or internal.", "/exposure");
    }

    private static void ValidateInputSchema(JsonElement definition, DiagnosticBag bag)
    {
        if (!definition.TryGetProperty("input", out var input)) return;
        if (input.ValueKind != JsonValueKind.Object)
        {
            bag.Error("INVALID_INPUT", "input must describe the expected fields (type: object, properties, required).", "/input");
            return;
        }
        var properties = input.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        if (input.TryGetProperty("properties", out var rawProperties) && rawProperties.ValueKind != JsonValueKind.Object)
            bag.Error("INVALID_INPUT", "input.properties must map field names to { type }.", "/input/properties");
        if (!input.TryGetProperty("required", out var required)) return;
        if (required.ValueKind != JsonValueKind.Array)
        {
            bag.Error("INVALID_INPUT", "input.required must be a list of field names.", "/input/required");
            return;
        }
        foreach (var name in required.EnumerateArray())
            if (name.ValueKind != JsonValueKind.String || properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name.GetString()!, out _))
                bag.Warning("INPUT_REQUIRED_UNKNOWN", $"input.required lists \"{name}\", which is not in input.properties.", "/input/required");
    }

    private static void ValidateLet(JsonElement definition, DiagnosticBag bag)
    {
        if (definition.TryGetProperty("let", out var let) && let.ValueKind != JsonValueKind.Object)
            bag.Error("INVALID_LET", "let must map alias names to paths.", "/let");
    }

    internal static void RequireBool(JsonElement owner, string name, DiagnosticBag bag, string prefix = "")
    {
        if (owner.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            bag.Error("INVALID_FLAG", $"{name} must be true or false.", prefix + "/" + name);
    }

    private static void CheckCollection(string collection, string path, DiagnosticBag bag, DefinitionValidationContext context)
    {
        if (context.HasProjectResources && !context.Collections.Contains(collection))
            bag.Warning("COLLECTION_NOT_FOUND", $"Collection \"{collection}\" does not exist in this project yet.", path);
    }
}
