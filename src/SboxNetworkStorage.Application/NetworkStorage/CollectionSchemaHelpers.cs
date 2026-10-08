using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// A single field discovered in a collection schema. The JSON shape
/// (<c>{ path, type, hasChildren }</c>) matches what
/// <c>wwwroot/js/storage-query-form.js</c> consumes for the query field picker.
/// </summary>
public sealed record SchemaFieldNode(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("hasChildren")] bool HasChildren);

public static class CollectionSchemaHelpers
{
    public static int CountFields(Dictionary<string, object>? schema) => GetSchemaProperties(schema).Count;

    public static IReadOnlyDictionary<string, object> GetSchemaProperties(Dictionary<string, object>? schema)
    {
        if (schema is null || schema.Count == 0) return ReadOnlyEmpty;

        if (schema.TryGetValue("properties", out var propertiesValue))
        {
            if (propertiesValue is JsonElement propertiesElement && propertiesElement.ValueKind == JsonValueKind.Object)
            {
                return propertiesElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => (object)p.Value);
            }

            if (propertiesValue is Dictionary<string, object> propertiesDictionary)
            {
                return propertiesDictionary;
            }
        }

        if (IsBareFieldMap(schema)) return schema;

        return ReadOnlyEmpty;
    }

    /// <summary>
    /// Flattens a collection schema into field nodes (parents + leaves), mirroring
    /// the legacy <c>schemaFieldMeta</c> (controllers/queries-controller.js) but
    /// built on <see cref="GetSchemaProperties"/> so the legacy bare string-valued
    /// schema form (<c>{"score":"number"}</c>) also yields leaf fields.
    /// </summary>
    public static IReadOnlyList<SchemaFieldNode> GetFieldMeta(Dictionary<string, object>? schema)
    {
        var result = new List<SchemaFieldNode>();
        AppendFields(GetSchemaProperties(schema), string.Empty, result);
        return result;
    }

    /// <summary>
    /// Leaf field paths only, mirroring the legacy <c>extractFieldPaths</c>
    /// (tools/sbox/queries.js).
    /// </summary>
    public static IReadOnlyList<string> GetFieldPaths(Dictionary<string, object>? schema)
    {
        var nodes = GetFieldMeta(schema);
        var paths = new List<string>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!node.HasChildren) paths.Add(node.Path);
        }
        return paths;
    }

    private static void AppendFields(IReadOnlyDictionary<string, object> props, string prefix, List<SchemaFieldNode> result)
    {
        foreach (var (key, raw) in props)
        {
            // A bare field map may carry the object's own "type":"object" declaration;
            // skip that, but keep a real field whose def is an object literally named "type".
            if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase) && IsScalarDef(raw)) continue;

            var path = prefix.Length == 0 ? key : string.Concat(prefix, ".", key);
            var children = ChildProperties(raw);
            if (children.Count > 0)
            {
                result.Add(new SchemaFieldNode(path, DefType(raw) ?? "object", true));
                AppendFields(children, path, result);
            }
            else
            {
                result.Add(new SchemaFieldNode(path, DefType(raw) ?? (HasEnum(raw) ? "enum" : "value"), false));
            }
        }
    }

    private static IReadOnlyDictionary<string, object> ChildProperties(object? def)
        => AsDictionary(def) is { } dict ? GetSchemaProperties(dict) : ReadOnlyEmpty;

    private static Dictionary<string, object>? AsDictionary(object? def)
    {
        if (def is Dictionary<string, object> d) return d;
        if (def is JsonElement { ValueKind: JsonValueKind.Object } je)
            return je.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        return null;
    }

    private static bool IsScalarDef(object? def)
        => def is not Dictionary<string, object> && def is not JsonElement { ValueKind: JsonValueKind.Object };

    private static string? DefType(object? def)
    {
        switch (def)
        {
            case string s:
                return s.Length > 0 ? s : null;
            case JsonElement { ValueKind: JsonValueKind.String } je:
                var sv = je.GetString();
                return string.IsNullOrEmpty(sv) ? null : sv;
            case JsonElement { ValueKind: JsonValueKind.Object } je:
                return je.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            case Dictionary<string, object> d when d.TryGetValue("type", out var tv):
                return tv switch
                {
                    string ts when ts.Length > 0 => ts,
                    JsonElement { ValueKind: JsonValueKind.String } tje => tje.GetString(),
                    _ => null
                };
            default:
                return null;
        }
    }

    private static bool HasEnum(object? def) => def switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } je => je.TryGetProperty("enum", out _),
        Dictionary<string, object> d => d.ContainsKey("enum"),
        _ => false
    };

    private static bool IsBareFieldMap(Dictionary<string, object> schema)
    {
        if (schema.Count == 0) return false;

        foreach (var (key, value) in schema)
        {
            if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)) continue;
            if (value is string) continue;
            if (value is JsonElement { ValueKind: JsonValueKind.String }) continue;
            if (value is Dictionary<string, object> or JsonElement { ValueKind: JsonValueKind.Object }) continue;
            return false;
        }

        return schema.Keys.Any(k => !string.Equals(k, "type", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<string, object> ReadOnlyEmpty =
        new Dictionary<string, object>();
}
