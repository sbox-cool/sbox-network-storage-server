using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Collection settings and schema checks, ported from the hosted collection
/// editor (storage-collection.js validateClientSchema). Accepts the standard
/// form (type: object, properties, required list) and the flat field map.
/// </summary>
internal static partial class NetworkStorageSchemaValidator
{
    public static readonly string[] FieldTypes = ["string", "number", "boolean", "object", "array", "player", "playerSave", "datetime", "any"];
    private static readonly string[] RateKeys = ["_maxPerMin", "_maxPerHour", "_maxPerDay", "_maxPerWeek", "_maxPerYear"];
    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal)
    {
        "type", "description", "title", "default", "enum", "required", "min", "max", "minimum", "maximum",
        "minLength", "maxLength", "minItems", "maxItems", "properties", "items", "additionalProperties",
        "_ledger", "_unique", "_maxPerMin", "_maxPerHour", "_maxPerDay", "_maxPerWeek", "_maxPerYear", "$schema",
    };

    public static void ValidateCollection(JsonElement definition, DiagnosticBag bag)
    {
        var type = NetworkStorageStepValidator.Str(definition, "collectionType");
        if (definition.TryGetProperty("collectionType", out _) && type is not ("per-steamid" or "global" or "player"))
            bag.Warning("COLLECTION_TYPE", "collectionType should be per-steamid (one record per player) or global (shared records). Anything else is stored per player.", "/collectionType");
        if (NetworkStorageStepValidator.Str(definition, "accessMode") is { } access && access is not ("public" or "endpoint" or "private"))
            bag.Warning("ACCESS_MODE", "accessMode should be public (game clients write records directly) or endpoint (only endpoints write).", "/accessMode");
        if (definition.TryGetProperty("maxRecords", out var maxRecords)
            && (maxRecords.ValueKind != JsonValueKind.Number || !maxRecords.TryGetInt32(out var records) || records < 1 || records > 50))
            bag.Error("INVALID_MAX_RECORDS", "maxRecords must be a whole number from 1 to 50 (save slots per player).", "/maxRecords");
        foreach (var flag in new[] { "allowRecordDelete", "requireSaveVersion", "webhookOnRateLimit" })
            NetworkStorageDefinitionValidator.RequireBool(definition, flag, bag);
        if (NetworkStorageStepValidator.Str(definition, "rateLimitAction") is { } action && action is not ("reject" or "clamp"))
            bag.Error("INVALID_RATE_LIMIT_ACTION", "rateLimitAction must be reject or clamp.", "/rateLimitAction");
        if (definition.TryGetProperty("rateLimits", out var rateLimits))
        {
            if (rateLimits.ValueKind != JsonValueKind.Object)
                bag.Error("INVALID_RATE_LIMITS", "rateLimits must be an object with mode and savesPerDay.", "/rateLimits");
            else
            {
                if (NetworkStorageStepValidator.Str(rateLimits, "mode") is { } mode && mode is not ("player" or "collection"))
                    bag.Error("INVALID_RATE_LIMITS", "rateLimits.mode must be player or collection.", "/rateLimits/mode");
                if (rateLimits.TryGetProperty("savesPerDay", out var saves)
                    && (saves.ValueKind != JsonValueKind.Number || !saves.TryGetInt32(out var perDay) || perDay < 1 || perDay > 10_000_000))
                    bag.Error("INVALID_RATE_LIMITS", "rateLimits.savesPerDay must be a whole number from 1 to 10000000.", "/rateLimits/savesPerDay");
            }
        }

        if (!definition.TryGetProperty("schema", out var schema))
        {
            if (!definition.TryGetProperty("fields", out _) && !definition.TryGetProperty("tables", out _) && !definition.TryGetProperty("constants", out _))
                bag.Warning("EMPTY_COLLECTION_DEFINITION", "This collection has no schema yet. Add fields so the dashboard and record editor know the data shape.", "/schema");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            bag.Error("INVALID_SCHEMA", "schema must map field names to field definitions.", "/schema");
            return;
        }
        var standard = schema.TryGetProperty("properties", out var properties);
        if (standard)
        {
            if (NetworkStorageStepValidator.Str(schema, "type") is { } rootType && rootType != "object")
                bag.Error("INVALID_SCHEMA", "A schema with properties must have type object.", "/schema/type");
            if (properties.ValueKind != JsonValueKind.Object)
            {
                bag.Error("INVALID_SCHEMA", "schema.properties must map field names to definitions.", "/schema/properties");
                return;
            }
            Walk(properties, "/schema/properties", "", bag);
            CheckRequiredList(schema, properties, "/schema", "", bag);
        }
        else Walk(schema, "/schema", "", bag);
    }

    private static void Walk(JsonElement properties, string pointer, string prefix, DiagnosticBag bag)
    {
        foreach (var field in properties.EnumerateObject())
        {
            var name = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
            var path = pointer + "/" + field.Name;
            if (!FieldName().IsMatch(field.Name))
                bag.Error("INVALID_FIELD_NAME", $"{name}: field names start with a letter or underscore and use letters, numbers, _ or - (max 64).", path);
            ValidateField(field.Value, name, path, bag);
        }
    }

    private static void ValidateField(JsonElement def, string name, string path, DiagnosticBag bag)
    {
        if (def.ValueKind == JsonValueKind.String)
        {
            if (Array.IndexOf(FieldTypes, def.GetString()) < 0)
                bag.Error("INVALID_FIELD_TYPE", $"{name}: unsupported type \"{def.GetString()}\". Use {string.Join(", ", FieldTypes)}.", path);
            return;
        }
        if (def.ValueKind != JsonValueKind.Object)
        {
            bag.Error("INVALID_FIELD", $"{name}: a field definition must be an object such as {{ type: number }}.", path);
            return;
        }
        var type = NetworkStorageStepValidator.Str(def, "type") ?? "any";
        if (Array.IndexOf(FieldTypes, type) < 0)
        {
            bag.Error("INVALID_FIELD_TYPE", $"{name}: unsupported type \"{type}\". Use {string.Join(", ", FieldTypes)}.", path + "/type");
            return;
        }
        foreach (var key in def.EnumerateObject())
            if (!FieldKeys.Contains(key.Name))
                bag.Warning("UNKNOWN_FIELD_SETTING", $"{name}: \"{key.Name}\" is not a field setting and is ignored.", path + "/" + key.Name);

        var min = Number(def, "min") ?? Number(def, "minimum");
        var max = Number(def, "max") ?? Number(def, "maximum");
        foreach (var bound in new[] { "min", "max", "minimum", "maximum" })
        {
            if (!def.TryGetProperty(bound, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number) bag.Error("INVALID_FIELD_BOUND", $"{name}: {bound} must be a number.", path + "/" + bound);
            else if (type != "number") bag.Error("INVALID_FIELD_BOUND", $"{name}: {bound} only applies to number fields.", path + "/" + bound);
        }
        if (min is double lo && max is double hi && lo > hi)
            bag.Error("INVALID_FIELD_BOUND", $"{name}: min cannot be greater than max.", path + "/min");
        if (IsTrue(def, "_ledger") && type != "number")
            bag.Error("INVALID_LEDGER", $"{name}: _ledger only applies to number fields.", path + "/_ledger");
        if (IsTrue(def, "_unique") && type is not ("string" or "number"))
            bag.Error("INVALID_UNIQUE", $"{name}: _unique only applies to string or number fields.", path + "/_unique");
        foreach (var flag in new[] { "_ledger", "_unique" })
            if (def.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                bag.Error("INVALID_FIELD_FLAG", $"{name}: {flag} must be true or false.", path + "/" + flag);
        foreach (var key in RateKeys)
        {
            if (!def.TryGetProperty(key, out var value)) continue;
            if (type != "number") bag.Error("INVALID_RATE_FIELD", $"{name}: {key} only applies to number fields.", path + "/" + key);
            if (value.ValueKind != JsonValueKind.Number || value.GetDouble() <= 0)
                bag.Error("INVALID_RATE_FIELD", $"{name}: {key} must be a positive number.", path + "/" + key);
        }
        if (def.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array))
            bag.Error("INVALID_REQUIRED", $"{name}: required must be true/false (or a list of child fields on objects).", path + "/required");
        if (def.TryGetProperty("enum", out var choices))
        {
            if (choices.ValueKind != JsonValueKind.Array) bag.Error("INVALID_ENUM", $"{name}: enum must be a list of allowed values.", path + "/enum");
            else foreach (var choice in choices.EnumerateArray())
                if (choice.ValueKind != JsonValueKind.Null && !Matches(type, choice))
                    bag.Error("INVALID_ENUM", $"{name}: enum value {choice.GetRawText()} is not a {type}.", path + "/enum");
        }
        if (def.TryGetProperty("default", out var fallback) && fallback.ValueKind != JsonValueKind.Null && !Matches(type, fallback))
            bag.Error("INVALID_DEFAULT", $"{name}: default must be a {type}.", path + "/default");

        if (type == "object")
        {
            if (def.TryGetProperty("properties", out var children))
            {
                if (children.ValueKind != JsonValueKind.Object) bag.Error("INVALID_SCHEMA", $"{name}: properties must be an object.", path + "/properties");
                else
                {
                    Walk(children, path + "/properties", name, bag);
                    CheckRequiredList(def, children, path, name, bag);
                }
            }
            if (def.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Object))
                bag.Error("INVALID_SCHEMA", $"{name}: additionalProperties must be true, false or a field definition.", path + "/additionalProperties");
            else if (extra.ValueKind == JsonValueKind.Object)
                ValidateField(extra, name + ".*", path + "/additionalProperties", bag);
        }
        else if (type == "array" && def.TryGetProperty("items", out var items))
            ValidateField(items, name + "[]", path + "/items", bag);
        else if (type == "array")
            bag.Warning("ARRAY_ITEMS", $"{name}: add an items type so list entries are described.", path + "/items");
    }

    private static void CheckRequiredList(JsonElement owner, JsonElement properties, string path, string name, DiagnosticBag bag)
    {
        if (!owner.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in required.EnumerateArray())
            if (entry.ValueKind != JsonValueKind.String || !properties.TryGetProperty(entry.GetString()!, out _))
                bag.Error("REQUIRED_FIELD_MISSING", $"{(name.Length == 0 ? "schema" : name)}: required field {entry.GetRawText()} is not defined.", path + "/required");
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "string" or "player" or "playerSave" or "datetime" => value.ValueKind == JsonValueKind.String,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    private static double? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool IsTrue(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$-]{0,63}$")]
    private static partial Regex FieldName();
}
