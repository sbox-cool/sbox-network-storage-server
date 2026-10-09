using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Dashboard resource definitions are authored in YAML. Raw JSON is still
/// accepted on save for older dashboard edits but is deprecated: new edits
/// should use YAML so the stored definition keeps its source text.
/// </summary>
public static class OwnerYamlDefinitions
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .DisableAliases()
        .Build();

    /// <summary>
    /// Parses dashboard input. A JSON object is returned as-is (deprecated);
    /// otherwise the text is parsed as a YAML mapping with the same scalar
    /// semantics as the editor source compiler.
    /// </summary>
    public static JsonElement ParseDefinition(string text, out bool wasJson)
    {
        if (TryParseJsonObject(text, out var json))
        {
            wasJson = true;
            return json;
        }
        wasJson = false;
        try
        {
            var normalized = NormalizeYamlValue(YamlDeserializer.Deserialize<object?>(text));
            var element = JsonSerializer.SerializeToElement(normalized);
            if (element.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Definition must be a YAML mapping.");
            return element;
        }
        catch (YamlException ex)
        {
            throw new ArgumentException($"Invalid YAML definition: {ex.Message}");
        }
    }

    /// <summary>Renders a stored definition for the YAML-first editor.</summary>
    public static string ToYaml(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Definition must be an object.");
        return YamlSerializer.Serialize(ToYamlValue(definition));
    }

    private static bool TryParseJsonObject(string text, out JsonElement json)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                json = document.RootElement.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
        }
        json = default;
        return false;
    }

    internal static object? NormalizeYamlValue(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(
            pair => Convert.ToString(pair.Key) ?? string.Empty,
            pair => NormalizeYamlValue(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        IDictionary<string, object> map => map.ToDictionary(
            pair => pair.Key,
            pair => NormalizeYamlValue(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        IEnumerable<object?> sequence when value is not string => sequence.Select(NormalizeYamlValue).ToList(),
        string s when bool.TryParse(s, out var b) => b,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => value,
    };

    private static object? ToYamlValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ToYamlValue(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToYamlValue).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
