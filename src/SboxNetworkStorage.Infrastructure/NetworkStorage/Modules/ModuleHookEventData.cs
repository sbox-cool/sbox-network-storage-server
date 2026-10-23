using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Emission contract for data-plane hook events. Emission call sites fill
/// <c>Data</c> with these well-known keys; subscribers read only what they
/// need. Building the payload never throws: failures yield an empty payload
/// rather than breaking the request.
/// </summary>
public static class ModuleHookEventData
{
    public const string Outcome = "outcome";
    public const string ErrorCode = "errorCode";
    public const string ChangedFields = "changedFields";
    public const string Body = "body";
    public const string Field = "field";
    public const string Delta = "delta";
    public const string Reason = "reason";
    public const string RevisionKnown = "known";

    /// <summary>Changed-field paths from an endpoint definition's write/delete steps.</summary>
    public static IReadOnlyList<string> ChangedFieldsOf(JsonElement? definition)
        => ChangedFieldsOf(definition.HasValue ? (object?)definition.Value : null);

    /// <summary>Changed-field paths from a stored endpoint definition (JSON or materialized).</summary>
    public static IReadOnlyList<string> ChangedFieldsOf(object? definition)
    {
        var fields = new List<string>();
        try
        {
            foreach (var step in WriteStepsOf(definition))
            {
                foreach (var path in OpPaths(step))
                {
                    if (!fields.Contains(path, StringComparer.Ordinal))
                        fields.Add(path);
                }
            }
        }
        catch
        {
            // Emission must never break the request.
        }
        return fields;
    }

    /// <summary>Conservatively classifies calls that can mutate data, including scripts and delegated workflows.</summary>
    public static bool HasWriteSteps(object? definition)
    {
        try
        {
            var runtime = definition switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("runtime", out var value) && value.ValueKind == JsonValueKind.String
                    => value.GetString(),
                IReadOnlyDictionary<string, object?> map when map.TryGetValue("runtime", out var value)
                    => value as string,
                _ => null,
            };
            if (!string.IsNullOrEmpty(runtime)) return true;
            return StepsOf(definition).Any(step => TypeOf(step) is "write" or "delete" or "workflow");
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<object?> WriteStepsOf(object? definition)
    {
        var steps = StepsOf(definition);
        foreach (var step in steps)
        {
            if (TypeOf(step) is "write" or "delete")
                yield return step;
        }
    }

    private static IEnumerable<object?> StepsOf(object? definition)
    {
        switch (definition)
        {
            case JsonElement element when element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array:
                foreach (var step in steps.EnumerateArray())
                    yield return step;
                break;
            case IReadOnlyDictionary<string, object?> map when map.TryGetValue("steps", out var raw):
                foreach (var step in AsList(raw))
                    yield return step;
                break;
        }
    }

    private static IEnumerable<object?> AsList(object? value)
    {
        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            foreach (var item in sequence)
                yield return item;
        }
    }

    private static string? TypeOf(object? step) => step switch
    {
        JsonElement element when element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            => type.GetString(),
        IReadOnlyDictionary<string, object?> map when map.TryGetValue("type", out var raw)
            => raw as string,
        _ => null,
    };

    private static IEnumerable<string> OpPaths(object? step)
    {
        var ops = step switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("ops", out var list) && list.ValueKind == JsonValueKind.Array
                => list.EnumerateArray().Cast<object?>(),
            IReadOnlyDictionary<string, object?> map when map.TryGetValue("ops", out var raw)
                => AsList(raw),
            _ => [],
        };
        foreach (var op in ops)
        {
            var path = op switch
            {
                JsonElement element when element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                    => value.GetString(),
                IReadOnlyDictionary<string, object?> map when map.TryGetValue("path", out var raw)
                    => raw as string,
                _ => null,
            };
            if (!string.IsNullOrEmpty(path))
                yield return path;
        }
    }

    public static Dictionary<string, object?> ForEndpointExecution(object? definition, bool ok, string? errorCode)
        => new(StringComparer.Ordinal)
        {
            [Outcome] = ok ? "ok" : errorCode ?? "error",
            [ErrorCode] = errorCode,
            [ChangedFields] = ChangedFieldsOf(definition),
        };
}
