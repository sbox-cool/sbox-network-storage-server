using System.Text.Json;

namespace SboxNetworkStorage.EndpointSpike.Plan;

/// <summary>Converts JSON text into the value model the step executor consumes: Dictionary&lt;string,object?&gt;, List&lt;object?&gt;, double, string, bool, null.</summary>
public static class PlanReader
{
    public static Dictionary<string, object?> ParseObject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Expected a JSON object at the root.");
        }
        return (Dictionary<string, object?>)Convert(doc.RootElement)!;
    }

    public static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => ConvertArray(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new FormatException($"Unsupported JSON value kind {element.ValueKind}."),
    };

    /// <summary>Clones a value graph into a standalone JsonElement suitable for the store APIs.</summary>
    public static JsonElement ToJsonElement(object? value)
    {
        using var doc = JsonDocument.Parse(CanonicalJson.Serialize(value));
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = Convert(property.Value);
        }
        return result;
    }

    private static List<object?> ConvertArray(JsonElement element)
    {
        var result = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(Convert(item));
        }
        return result;
    }
}
