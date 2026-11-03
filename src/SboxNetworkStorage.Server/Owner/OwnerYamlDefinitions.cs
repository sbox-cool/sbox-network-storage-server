using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization.EventEmitters;

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
        .WithEventEmitter(next => new LiteralMultilineStrings(next))
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
        var value = (Dictionary<string, object?>)ToYamlValue(definition)!;
        if (!definition.TryGetProperty("script", out var script) || script.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(script.GetString()))
            return YamlSerializer.Serialize(value);
        // The dashboard editor reads only a top-level `script: |` block. YamlDotNet quotes a multi-line string
        // when a line ends in spaces, so the script is serialised as a placeholder and spliced in as a literal block.
        var placeholder = "__script_" + Guid.NewGuid().ToString("N") + "__";
        value["script"] = placeholder;
        var lines = YamlSerializer.Serialize(value).Split('\n').ToList();
        var index = lines.FindIndex(line => line.TrimEnd('\r') == "script: " + placeholder);
        if (index < 0) throw new InvalidOperationException("The serialized script placeholder was not found.");
        lines.RemoveAt(index);
        lines.InsertRange(index, ScriptBlock(script.GetString()!));
        return string.Join('\n', lines);
    }

    // The literal block the editor reads. `|2` keeps a leading space on the first content line, and the
    // chomping indicator keeps the exact trailing line breaks, so the value round-trips unchanged.
    private static IEnumerable<string> ScriptBlock(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var trailingBreaks = normalized.Length - normalized.TrimEnd('\n').Length;
        var chomp = trailingBreaks == 0 ? "-" : trailingBreaks == 1 ? "" : "+";
        var lines = normalized.Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var firstContent = lines.FirstOrDefault(line => line.Length > 0);
        var indicator = firstContent is not null && firstContent[0] == ' ' ? "2" : "";
        yield return $"script: |{indicator}{chomp}";
        foreach (var line in lines)
            yield return line.Length == 0 ? "" : "  " + line;
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
        IDictionary<object, object> map => NormalizeYamlMapping(map),
        IDictionary<string, object> map => map.ToDictionary(
            pair => pair.Key,
            pair => NormalizeYamlValue(pair.Value),
            StringComparer.Ordinal),
        IEnumerable<object?> sequence when value is not string => sequence.Select(NormalizeYamlValue).ToList(),
        string s when bool.TryParse(s, out var b) => b,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => value,
    };

    // Keys stay case-sensitive, matching the JSON path: `Foo` and `foo` are two keys.
    // Keys that collide only after stringification are reported as invalid YAML
    // (ParseDefinition maps YamlException to ArgumentException) instead of crashing.
    private static Dictionary<string, object?> NormalizeYamlMapping(IDictionary<object, object> map)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            var key = Convert.ToString(pair.Key) ?? string.Empty;
            if (!normalized.TryAdd(key, NormalizeYamlValue(pair.Value)))
                throw new YamlException($"Duplicate mapping key '{key}'.");
        }
        return normalized;
    }

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

    // YamlDotNet writes some multi-line strings as folded `>` blocks, which join their lines. The
    // dashboard's script editor reads only `script: |` blocks, so every multi-line string is written
    // as a literal block. libyaml falls back to a quoted scalar when a literal block is not allowed,
    // for example when a line ends in spaces.
    private sealed class LiteralMultilineStrings(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string text && text.Contains('\n'))
            {
                eventInfo.Style = ScalarStyle.Literal;
            }

            base.Emit(eventInfo, emitter);
        }
    }
}
