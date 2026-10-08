using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

internal static class NetworkStorageSourceResourceCompiler
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly string[] AuthoringFields =
        ["authoringMode", "sourceFormat", "sourcePath", "sourceText", "sourceVersion"];

    public static bool TryCompile(
        JsonElement resource,
        string expectedKind,
        out JsonElement compiled,
        out string? error)
    {
        compiled = resource.Clone();
        error = null;

        if (resource.ValueKind != JsonValueKind.Object
            || !resource.TryGetProperty("sourceText", out var sourceTextProperty)
            || sourceTextProperty.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var sourceText = sourceTextProperty.GetString();
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            error = "Source text is empty.";
            return false;
        }

        try
        {
            var sourceFormat = ReadString(resource, "sourceFormat")
                ?? InferFormat(ReadString(resource, "sourcePath"));
            var source = string.Equals(sourceFormat, "json", StringComparison.OrdinalIgnoreCase)
                ? ParseJson(sourceText)
                : ParseYaml(sourceText);

            if (source is null)
            {
                error = "Source text must contain an object.";
                return false;
            }

            var declaredKind = ReadString(source, "kind") ?? ReadString(source, "resourceKind");
            if (!string.IsNullOrWhiteSpace(declaredKind)
                && !string.Equals(declaredKind, expectedKind, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Source kind '{declaredKind}' does not match '{expectedKind}'.";
                return false;
            }

            var body = UnwrapSourceBody(source, expectedKind);
            body.Remove("kind");
            body.Remove("resourceKind");

            foreach (var field in AuthoringFields)
            {
                if (resource.TryGetProperty(field, out var value))
                    body[field] = JsonNode.Parse(value.GetRawText());
            }

            var wrapperId = ReadString(resource, "id");
            if (!body.ContainsKey("id") && !string.IsNullOrWhiteSpace(wrapperId))
                body["id"] = wrapperId;

            var resolvedId = ReadString(body, "id") ?? wrapperId;
            if (expectedKind == "endpoint" && !body.ContainsKey("slug") && !string.IsNullOrWhiteSpace(resolvedId))
                body["slug"] = resolvedId;
            if (expectedKind is "collection" or "workflow"
                && !body.ContainsKey("name")
                && !string.IsNullOrWhiteSpace(resolvedId))
                body["name"] = resolvedId;

            compiled = JsonSerializer.SerializeToElement(body);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON source: {ex.Message}";
            return false;
        }
        catch (YamlException ex)
        {
            error = $"Invalid YAML source: {ex.Message}";
            return false;
        }
    }

    private static JsonObject ParseJson(string sourceText)
    {
        var node = JsonNode.Parse(sourceText);
        return node as JsonObject
            ?? throw new JsonException("The root value must be an object.");
    }

    private static JsonObject ParseYaml(string sourceText)
    {
        var yaml = YamlDeserializer.Deserialize<object?>(sourceText);
        var normalized = NormalizeYamlValue(yaml);
        var element = JsonSerializer.SerializeToElement(normalized);
        return JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new YamlException("The root value must be a mapping.");
    }

    private static object? NormalizeYamlValue(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(
            pair => Convert.ToString(pair.Key) ?? string.Empty,
            pair => NormalizeYamlValue(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        IDictionary<string, object> map => map.ToDictionary(
            pair => pair.Key,
            pair => NormalizeYamlValue(pair.Value),
            StringComparer.OrdinalIgnoreCase),
        IEnumerable sequence when value is not string => sequence.Cast<object?>().Select(NormalizeYamlValue).ToList(),
        // YamlDotNet's default schema resolves nothing for an `object` target: an
        // unquoted YAML `true` arrives as the STRING "true" and `403` as "403".
        // Compiled that way, `check: { field: "{{_hasSecretKey}}", op: "==",
        // value: true }` became "value": "true" and the boolean comparison
        // (bool == "true") was always false — the SERVER_ONLY gate rejected every
        // dedicated-server request. Coerce YAML boolean/number scalars here so
        // compiled definitions carry real booleans and numbers, matching the JS
        // source compiler's YAML semantics.
        string s when bool.TryParse(s, out var b) => b,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => value,
    };

    private static JsonObject UnwrapSourceBody(JsonObject source, string expectedKind)
    {
        if (source[expectedKind] is JsonObject kindBody)
            return Merge(source, kindBody, expectedKind, "resource");
        if (source["resource"] is JsonObject resourceBody)
            return Merge(source, resourceBody, "resource", expectedKind);
        if (source["definition"] is JsonObject definitionBody)
            return Merge(definitionBody, source, "definition", expectedKind, "resource");

        return (JsonObject)source.DeepClone();
    }

    private static JsonObject Merge(JsonObject first, JsonObject second, params string[] excludedKeys)
    {
        var excluded = new HashSet<string>(excludedKeys, StringComparer.OrdinalIgnoreCase);
        var merged = new JsonObject();
        Copy(first, merged, excluded);
        Copy(second, merged, excluded);
        return merged;
    }

    private static void Copy(JsonObject source, JsonObject target, HashSet<string> excluded)
    {
        foreach (var pair in source)
        {
            if (!excluded.Contains(pair.Key))
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static string InferFormat(string? sourcePath) =>
        string.Equals(Path.GetExtension(sourcePath), ".json", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "yaml";

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue property
        && property.TryGetValue<string>(out var text)
            ? text
            : null;
}
