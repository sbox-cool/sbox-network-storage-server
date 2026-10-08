using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Parity.Replay;

/// <summary>
/// Expands <c>${name}</c> placeholders (<c>${projectId}</c>, <c>${publicKey}</c>,
/// <c>${secretKey}</c>, <c>${steamId}</c>, <c>${steamId2}</c>, <c>${capture.NAME}</c>).
/// The <c>${...}</c> form never collides with endpoint <c>{{template}}</c> syntax.
/// </summary>
public sealed partial class TemplateContext
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _captures = new(StringComparer.Ordinal);

    public TemplateContext(string projectId, string publicKey, string secretKey, string steamId, string steamId2)
    {
        _variables["projectId"] = projectId;
        _variables["publicKey"] = publicKey;
        _variables["secretKey"] = secretKey;
        _variables["steamId"] = steamId;
        _variables["steamId2"] = steamId2;
    }

    public string ProjectId => _variables["projectId"];

    public string PublicKey => _variables["publicKey"];

    public string SecretKey => _variables["secretKey"];

    /// <summary>Captured value → stable placeholder, so generated ids keep their referential structure after masking.</summary>
    public IEnumerable<KeyValuePair<string, string>> CaptureReplacements =>
        _captures.Where(c => c.Value.Length >= 4).Select(c => new KeyValuePair<string, string>(c.Value, $"<capture:{c.Key}>"));

    public void SetCapture(string name, string value) => _captures[name] = value;

    public string Expand(string template) => Placeholder().Replace(template, match =>
    {
        var name = match.Groups[1].Value;
        if (name.StartsWith("capture.", StringComparison.Ordinal))
        {
            return _captures.TryGetValue(name["capture.".Length..], out var captured)
                ? captured
                : throw new ParityException($"capture '{name}' was not recorded by an earlier step");
        }

        return _variables.TryGetValue(name, out var value) ? value : throw new ParityException($"unknown template variable ${{{name}}}");
    });

    public JsonNode? Expand(JsonElement body)
    {
        var root = JsonNode.Parse(body.GetRawText());
        return ExpandedString(root) ?? ExpandInPlace(root);
    }

    private JsonNode? ExpandInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (ExpandedString(obj[name]) is { } replacement)
                    {
                        obj[name] = replacement;
                    }
                    else
                    {
                        ExpandInPlace(obj[name]);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (ExpandedString(array[i]) is { } replacement)
                    {
                        array[i] = replacement;
                    }
                    else
                    {
                        ExpandInPlace(array[i]);
                    }
                }

                break;
        }

        return node;
    }

    private JsonNode? ExpandedString(JsonNode? node) =>
        node is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>() is var text
        && text.Contains("${", StringComparison.Ordinal)
            ? JsonValue.Create(Expand(text))
            : null;

    [GeneratedRegex(@"\$\{([A-Za-z0-9_.]+)\}")]
    private static partial Regex Placeholder();
}
