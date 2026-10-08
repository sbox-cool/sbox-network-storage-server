using System.Text.Json;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Parity.Normalization;

/// <summary>
/// Minimal JSON path: <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>[3]</c> and the
/// <c>[*]</c> / <c>.*</c> wildcards (every array item or object member).
/// </summary>
public sealed class JsonPath
{
    private readonly Segment[] _segments;

    private JsonPath(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    public string Text { get; }

    public static JsonPath Parse(string text)
    {
        if (!text.StartsWith('$'))
        {
            throw new ParityException($"JSON path must start with '$': {text}");
        }

        var segments = new List<Segment>();
        var i = 1;
        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                var start = ++i;
                while (i < text.Length && text[i] is not ('.' or '['))
                {
                    i++;
                }

                var name = text[start..i];
                if (name.Length == 0)
                {
                    throw new ParityException($"empty member name in JSON path: {text}");
                }

                segments.Add(name == "*" ? Segment.Wildcard : Segment.Member(name));
            }
            else if (text[i] == '[')
            {
                var end = text.IndexOf(']', i);
                if (end < 0)
                {
                    throw new ParityException($"unterminated '[' in JSON path: {text}");
                }

                var inner = text[(i + 1)..end];
                i = end + 1;
                if (inner == "*")
                {
                    segments.Add(Segment.Wildcard);
                }
                else if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0])
                {
                    segments.Add(Segment.Member(inner[1..^1]));
                }
                else if (int.TryParse(inner, out var index))
                {
                    segments.Add(Segment.Index(index));
                }
                else
                {
                    throw new ParityException($"unsupported JSON path segment [{inner}]: {text}");
                }
            }
            else
            {
                throw new ParityException($"unexpected '{text[i]}' in JSON path: {text}");
            }
        }

        return new JsonPath(text, [.. segments]);
    }

    /// <summary>Every element the path selects in a read-only document.</summary>
    public IEnumerable<JsonElement> Select(JsonElement root)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (var segment in _segments)
        {
            current = current.SelectMany(element => Step(element, segment)).ToList();
        }

        return current;
    }

    /// <summary>Replaces every selected node with <paramref name="transform"/>'s result; returns the (possibly new) root.</summary>
    public JsonNode? Apply(JsonNode? root, Func<JsonNode?, JsonNode?> transform)
    {
        if (_segments.Length == 0)
        {
            return transform(root);
        }

        ApplyAt(root, 0, transform);
        return root;
    }

    private void ApplyAt(JsonNode? node, int depth, Func<JsonNode?, JsonNode?> transform)
    {
        var segment = _segments[depth];
        var last = depth == _segments.Length - 1;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (segment.IsWildcard || (segment.Name is not null && segment.Name == name))
                    {
                        if (last)
                        {
                            obj[name] = Detach(transform(obj[name]));
                        }
                        else
                        {
                            ApplyAt(obj[name], depth + 1, transform);
                        }
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (segment.IsWildcard || segment.ArrayIndex == i)
                    {
                        if (last)
                        {
                            array[i] = Detach(transform(array[i]));
                        }
                        else
                        {
                            ApplyAt(array[i], depth + 1, transform);
                        }
                    }
                }

                break;
        }
    }

    private static JsonNode? Detach(JsonNode? node) => node?.Parent is null ? node : node.DeepClone();

    private static IEnumerable<JsonElement> Step(JsonElement element, Segment segment)
    {
        if (segment.IsWildcard)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject().Select(p => p.Value),
                JsonValueKind.Array => element.EnumerateArray(),
                _ => [],
            };
        }

        if (segment.Name is not null)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment.Name, out var value) ? [value] : [];
        }

        return element.ValueKind == JsonValueKind.Array && segment.ArrayIndex < element.GetArrayLength()
            ? [element[segment.ArrayIndex]]
            : [];
    }

    private readonly record struct Segment(string? Name, int ArrayIndex, bool IsWildcard)
    {
        public static readonly Segment Wildcard = new(null, -1, true);

        public static Segment Member(string name) => new(name, -1, false);

        public static Segment Index(int index) => new(null, index, false);
    }
}
