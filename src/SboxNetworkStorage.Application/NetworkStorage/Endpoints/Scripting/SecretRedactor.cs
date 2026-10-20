using System.Diagnostics.CodeAnalysis;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Replaces the values of project secrets a script read with <c>[secret:NAME]</c> wherever one run's output leaves the
/// script: logs, the try-it trace, failure text, and the response body. Matching is exact text, longest value first.
/// A value that the script transformed (encoded, split, or converted to a number) is not caught. Record keys are never
/// rewritten. Persisted record data is not changed: the trace copy of a write is redacted, the stored data is not.
/// </summary>
public sealed class SecretRedactor
{
    private readonly (string Value, string Token)[] _pairs;

    /// <summary>Builds the redactor from the secrets a run read. Null and empty values are dropped.</summary>
    public SecretRedactor(IEnumerable<KeyValuePair<string, string?>> secrets)
    {
        _pairs = secrets
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => (pair.Value!, $"[secret:{pair.Key}]"))
            .OrderByDescending(pair => pair.Item1.Length)
            .ToArray();
    }

    /// <summary>Redacts a string. Returns it unchanged when no secret was read.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public string? Text(string? text)
    {
        if (text is null || _pairs.Length == 0) return text;
        foreach (var (value, token) in _pairs)
            text = text.Replace(value, token, StringComparison.Ordinal);
        return text;
    }

    public List<string> Lines(IEnumerable<string> lines) => lines.Select(line => Text(line)).ToList();

    /// <summary>Redacts every string inside a plain object graph (dictionaries, lists and scalars). Keys are kept.</summary>
    public object? Value(object? value)
    {
        if (_pairs.Length == 0) return value;
        return value switch
        {
            string text => Text(text),
            IReadOnlyDictionary<string, object?> map => Map(map),
            IEnumerable<object?> list => list.Select(Value).ToList(),
            _ => value,
        };
    }

    public IReadOnlyDictionary<string, object?>? Map(IReadOnlyDictionary<string, object?>? map) =>
        map is null || _pairs.Length == 0
            ? map
            : map.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);

    public EndpointStepTrace Step(EndpointStepTrace step) =>
        _pairs.Length == 0 ? step : step with { Result = Value(step.Result) };

    public EndpointTraceWrite Write(EndpointTraceWrite write) =>
        _pairs.Length == 0 ? write : write with { Data = Map(write.Data) };
}
