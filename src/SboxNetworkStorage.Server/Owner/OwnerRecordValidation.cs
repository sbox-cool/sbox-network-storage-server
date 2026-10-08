using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Validates owner edits using the collection's JSON-schema or legacy bare field map.</summary>
internal static class OwnerRecordValidation
{
    public static string? Validate(JsonElement row, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "Record payload must be a JSON object.";
        if (!row.TryGetProperty("definition_json", out var definition)) return null;
        if (definition.ValueKind == JsonValueKind.String)
        {
            using var document = JsonDocument.Parse(definition.GetString() ?? "{}");
            definition = document.RootElement.Clone();
        }
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("schema", out var schema)) return null;
        if (schema.ValueKind == JsonValueKind.String)
        {
            using var document = JsonDocument.Parse(schema.GetString() ?? "{}");
            schema = document.RootElement.Clone();
        }
        return Node(schema, payload, "$", 0);
    }

    private static string? Node(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 64) return $"{path}: schema nesting exceeds 64 levels.";
        if (schema.ValueKind == JsonValueKind.String) return Type(schema.GetString(), value, path);
        if (schema.ValueKind == JsonValueKind.False) return $"{path}: the collection schema does not allow this value.";
        if (schema.ValueKind == JsonValueKind.True) return null;
        if (schema.ValueKind != JsonValueKind.Object) return $"{path}: invalid collection schema.";
        var standard = value.ValueKind != JsonValueKind.Object || schema.TryGetProperty("properties", out _)
            || schema.TryGetProperty("enum", out _) || schema.TryGetProperty("required", out _)
            || schema.TryGetProperty("additionalProperties", out _) || schema.TryGetProperty("$ref", out _)
            || (schema.TryGetProperty("type", out var declaredType) && declaredType.ValueKind != JsonValueKind.Object
                && schema.EnumerateObject().All(field => Keywords.Contains(field.Name)));
        if (standard)
        {
            foreach (var keyword in schema.EnumerateObject())
                if (!Keywords.Contains(keyword.Name))
                    return $"{path}: collection schema modifier '{keyword.Name}' is not supported by the record editor.";
            foreach (var keyword in schema.EnumerateObject())
            {
                var valid = keyword.Name switch
                {
                    "type" => keyword.Value.ValueKind == JsonValueKind.String,
                    "properties" => keyword.Value.ValueKind == JsonValueKind.Object,
                    "required" => keyword.Value.ValueKind == JsonValueKind.Array
                        && keyword.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                    "enum" => keyword.Value.ValueKind == JsonValueKind.Array,
                    "additionalProperties" => keyword.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False,
                    "minLength" or "maxLength" or "minItems" or "maxItems" => keyword.Value.ValueKind == JsonValueKind.Number
                        && keyword.Value.TryGetInt32(out var limit) && limit >= 0,
                    "minimum" or "maximum" => keyword.Value.ValueKind == JsonValueKind.Number,
                    _ => true
                };
                if (!valid) return $"{path}: invalid '{keyword.Name}' in the collection schema.";
            }
        }
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && Type(type.GetString(), value, path) is { } typeError) return typeError;
        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array
            && !choices.EnumerateArray().Any(choice => Equal(choice, value))) return $"{path}: value is not in the allowed enum.";
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var key in required.EnumerateArray())
                    if (key.ValueKind == JsonValueKind.String && !value.TryGetProperty(key.GetString()!, out _))
                        return $"{path}.{key.GetString()}: required field is missing.";
            var dictionary = schema.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
            var properties = CollectionSchemaHelpers.GetSchemaProperties(dictionary);
            var disallowExtra = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
            foreach (var field in value.EnumerateObject())
            {
                if (properties.TryGetValue(field.Name, out var raw) && raw is JsonElement fieldSchema)
                {
                    if (Node(fieldSchema, field.Value, path + "." + field.Name, depth + 1) is { } error) return error;
                }
                else if (disallowExtra) return $"{path}.{field.Name}: additional fields are not allowed.";
                else if (additional.ValueKind == JsonValueKind.Object
                    && Node(additional, field.Value, path + "." + field.Name, depth + 1) is { } additionalError)
                    return additionalError;
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && min.TryGetInt32(out var minimum) && count < minimum)
                return $"{path}: must contain at least {minimum} items.";
            if (schema.TryGetProperty("maxItems", out var max) && max.TryGetInt32(out var maximum) && count > maximum)
                return $"{path}: must contain at most {maximum} items.";
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (Node(items, item, $"{path}[{index}]", depth + 1) is { } error) return error;
                    index++;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.EnumerateRunes().Count();
            if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var minimum) && length < minimum)
                return $"{path}: must contain at least {minimum} characters.";
            if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maximum) && length > maximum)
                return $"{path}: must contain at most {maximum} characters.";
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (schema.TryGetProperty("minimum", out var min) && min.TryGetDouble(out var minimum) && number < minimum)
                return $"{path}: must be at least {minimum}.";
            if (schema.TryGetProperty("maximum", out var max) && max.TryGetDouble(out var maximum) && number > maximum)
                return $"{path}: must be at most {maximum}.";
        }
        return null;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "enum", "items",
        "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems",
        "description", "title", "default", "$schema"
    };
    private static string? Type(string? type, JsonElement value, string path)
    {
        var valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && Math.Truncate(n) == n,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "player" or "playerSave" => value.ValueKind == JsonValueKind.String && StorageIdValidation.IsValidRecordKey(value.GetString()!),
            "datetime" => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
            "null" => value.ValueKind == JsonValueKind.Null,
            null or "" => true,
            _ => false
        };
        return valid ? null : $"{path}: expected {type}.";
    }

    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(p => right.TryGetProperty(p.Name, out var other) && Equal(p.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equal(pair.First, pair.Second)),
            _ => true
        };
    }
}
