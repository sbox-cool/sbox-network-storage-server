using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Redacts unchanged project-secret strings at output boundaries. Structured values are traversed before
/// serialization, including property names. This is accidental-disclosure protection, not information-flow
/// isolation from a trusted script that deliberately transforms a secret.
/// </summary>
public sealed class SecretRedactor
{
    private readonly (string Value, string Token)[] _pairs;

    public SecretRedactor(IEnumerable<KeyValuePair<string, string?>> secrets)
    {
        _pairs = secrets.Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => (pair.Value!, $"[secret:{pair.Key}]"))
            .OrderByDescending(pair => pair.Item1.Length).ToArray();
    }

    [return: NotNullIfNotNull(nameof(text))]
    public string? Text(string? text)
    {
        if (text is null || _pairs.Length == 0) return text;
        StringBuilder? output = null;
        var offset = 0;
        while (offset < text.Length)
        {
            var next = text.Length;
            (string Value, string Token) match = default;
            foreach (var pair in _pairs)
            {
                var index = text.IndexOf(pair.Value, offset, StringComparison.Ordinal);
                if (index >= 0 && index < next)
                {
                    next = index;
                    match = pair;
                }
            }
            if (match.Value is null) break;
            output ??= new StringBuilder(text.Length);
            output.Append(text, offset, next - offset).Append(match.Token);
            offset = next + match.Value.Length;
        }
        return output is null ? text : output.Append(text, offset, text.Length - offset).ToString();
    }

    public bool Contains(object? value) => value switch
    {
        string text => _pairs.Any(pair => text.Contains(pair.Value, StringComparison.Ordinal)),
        JsonElement json => Contains(ScriptJson.ToPlain(json)),
        IReadOnlyDictionary<string, object?> map => map.Any(pair => Contains(pair.Key) || Contains(pair.Value)),
        IEnumerable<object?> list => list.Any(Contains),
        _ => false,
    };

    public List<string> Logs(IEnumerable<JsonElement> values) => values.Select(value =>
        value.ValueKind == JsonValueKind.String ? Text(value.GetString())! : JsonSerializer.Serialize(Value(value))).ToList();

    public object? Value(object? value)
    {
        if (_pairs.Length == 0) return value;
        return value switch
        {
            string text => Text(text),
            JsonElement json => Value(ScriptJson.ToPlain(json)),
            IReadOnlyDictionary<string, object?> map => Map(map),
            IEnumerable<object?> list => list.Select(Value).ToList(),
            _ => value,
        };
    }

    public IReadOnlyDictionary<string, object?>? Map(IReadOnlyDictionary<string, object?>? map)
    {
        if (map is null || _pairs.Length == 0) return map;
        var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
        foreach (var pair in map)
            if (!result.TryAdd(Text(pair.Key), Value(pair.Value)))
                throw new SecretOutputCollisionException();
        return result;
    }

    public EndpointStepTrace Step(EndpointStepTrace step) =>
        _pairs.Length == 0 ? step : step with { Id = Text(step.Id), Type = Text(step.Type), Result = Value(step.Result) };

    public EndpointTraceWrite Write(EndpointTraceWrite write) =>
        _pairs.Length == 0 ? write : write with { Collection = Text(write.Collection), Key = Text(write.Key), Data = Map(write.Data) };
}

/// <summary>A redacted object would lose fields; do not emit a misleading or secret-bearing result.</summary>
public sealed class SecretOutputCollisionException : Exception;
