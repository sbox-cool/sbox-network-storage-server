using System.Collections.Generic;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Extracts the dotted field paths actually present in a record payload sample.
/// Used to surface saved-but-undeclared fields (e.g. a player display name) in
/// the query builder, which is otherwise driven by the declared schema only.
/// </summary>
public static class ObservedRecordFields
{
    /// <summary>
    /// Flattens leaf field paths of <paramref name="element"/> into <paramref name="into"/>
    /// (path → type), depth-capped. Existing entries are preserved so schema-declared
    /// types win over inferred ones. Nested objects recurse; arrays/scalars are leaves.
    /// </summary>
    public static void Collect(JsonElement element, IDictionary<string, string> into, int maxDepth = 4)
        => CollectInternal(element, prefix: string.Empty, depth: 0, maxDepth, into);

    private static void CollectInternal(JsonElement element, string prefix, int depth, int maxDepth, IDictionary<string, string> into)
    {
        if (depth > maxDepth || element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CollectInternal(property.Value, path, depth + 1, maxDepth, into);
            }
            else if (!into.ContainsKey(path))
            {
                into[path] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => "number",
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    JsonValueKind.Array => "array",
                    JsonValueKind.String => "string",
                    _ => "value"
                };
            }
        }
    }
}
