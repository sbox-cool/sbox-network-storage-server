using System.Text.Json;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>Reads retained or live collection values without rewriting stored source or snapshots.</summary>
internal static class GameValueCollectionDefinition
{
    public static bool TryRead(JsonElement row, out JsonElement definition, out string visibility)
    {
        definition = default;
        visibility = Text(row, "visibility") ?? "private";
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("definition_json", out var stored)) return false;
        try
        {
            definition = stored.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(stored.GetString()!) : stored;
            if (definition.ValueKind != JsonValueKind.Object) return false;
            if (definition.TryGetProperty("sourceText", out var source) && source.ValueKind == JsonValueKind.String)
            {
                // Raw PUT collections from older Sync Tools were stored without compiling.
                // Compiled source carries constants/tables; do not parse its sourceText on each read.
                var hasCompiledValues = HasValueSection(definition, "constants") || HasValueSection(definition, "tables");
                var declaredVisibility = Text(definition, "visibility");
                if (!hasCompiledValues)
                {
                    // An explicitly private source stays private, even with accessMode: public.
                    if (!NetworkStorageSourceResourceCompiler.TryCompile(definition, "collection", out var compiled, out _)) return false;
                    definition = compiled;
                }
                visibility = declaredVisibility ?? Text(definition, "visibility")
                    ?? (Text(definition, "accessMode") == "public" ? "public" : visibility);
            }
            definition = Normalize(definition);
            return true;
        }
        catch (JsonException)
        {
            definition = default;
            return false;
        }
    }

    private static bool HasValueSection(JsonElement definition, string name)
        => definition.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static JsonElement Normalize(JsonElement definition)
    {
        var mapConstants = definition.TryGetProperty("constants", out var constants) && constants.ValueKind == JsonValueKind.Object;
        var mapTables = definition.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Object;
        var positionalTables = tables.ValueKind == JsonValueKind.Array && tables.EnumerateArray().Any(HasPositionalRows);
        // Preserve already-supported array definitions and their response metadata verbatim.
        if (!mapConstants && !mapTables && !positionalTables) return definition;
        var normalized = (JsonObject)JsonNode.Parse(definition.GetRawText())!;
        if (mapConstants)
        {
            var groups = new JsonArray();
            foreach (var group in constants.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Object) continue;
                groups.Add(new JsonObject { ["id"] = group.Name, ["name"] = group.Name, ["entries"] = JsonNode.Parse(group.Value.GetRawText()) });
            }
            normalized["constants"] = groups;
        }
        if (mapTables)
        {
            var list = new JsonArray();
            foreach (var table in tables.EnumerateObject())
                if (table.Value.ValueKind == JsonValueKind.Object) list.Add(NormalizeTable(table.Value, table.Name));
            normalized["tables"] = list;
        }
        else if (positionalTables)
        {
            var list = new JsonArray();
            foreach (var table in tables.EnumerateArray())
                list.Add(HasPositionalRows(table) ? NormalizeTable(table, null) : JsonNode.Parse(table.GetRawText()));
            normalized["tables"] = list;
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static bool HasPositionalRows(JsonElement table) => table.ValueKind == JsonValueKind.Object
        && table.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
        && rows.EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Array);

    private static JsonObject NormalizeTable(JsonElement table, string? id)
    {
        var result = (JsonObject)JsonNode.Parse(table.GetRawText())!;
        if (id is not null) result["id"] = id;
        if (!table.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array) return result;
        var keys = new List<string>();
        var normalizedColumns = new JsonArray();
        table.TryGetProperty("rows", out var rows);
        foreach (var column in columns.EnumerateArray())
        {
            var key = column.ValueKind == JsonValueKind.String ? column.GetString() : Text(column, "key");
            if (string.IsNullOrEmpty(key) || keys.Contains(key, StringComparer.Ordinal))
                throw new JsonException("Game value table columns must have distinct keys.");
            normalizedColumns.Add(column.ValueKind == JsonValueKind.String
                ? new JsonObject { ["key"] = key, ["type"] = ColumnType(rows, key, keys.Count) }
                : JsonNode.Parse(column.GetRawText()));
            keys.Add(key);
        }
        result["columns"] = normalizedColumns;
        if (rows.ValueKind != JsonValueKind.Array) return result;
        var normalizedRows = new JsonArray();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                normalizedRows.Add(JsonNode.Parse(row.GetRawText()));
                continue;
            }
            if (row.GetArrayLength() != keys.Count) throw new JsonException("Game value row width must match its columns.");
            var values = new JsonObject();
            for (var i = 0; i < keys.Count; i++) values[keys[i]] = JsonNode.Parse(row[i].GetRawText());
            normalizedRows.Add(values);
        }
        result["rows"] = normalizedRows;
        return result;
    }

    private static string ColumnType(JsonElement rows, string key, int index)
    {
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
            {
                var value = row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > index ? row[index]
                    : row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var field) ? field : default;
                if (value.ValueKind == JsonValueKind.Number) return "number";
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return "boolean";
                if (value.ValueKind == JsonValueKind.String) return "string";
            }
        return "string";
    }

    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
