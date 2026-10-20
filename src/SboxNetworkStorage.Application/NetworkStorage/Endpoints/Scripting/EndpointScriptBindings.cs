using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// The storage, workflow and module operations a script host performs on behalf of one endpoint execution. The
/// endpoint executor supplies these closures so scripts reuse its storage paths: read sets, pending writes, the
/// flush transaction, the size gate, and the workflow executor. Nothing here talks to the store directly.
/// </summary>
public sealed record EndpointScriptBindings(
    Func<string, string, object?> ReadRecord,
    Action<string, string, Dictionary<string, object?>> WriteRecord,
    Action<string, string> DeleteRecord,
    Func<string, List<object?>> ScanCollection,
    IReadOnlyDictionary<string, object?> GameValues,
    DateTimeOffset Now,
    Func<string, Dictionary<string, object?>, object?> RunWorkflow,
    Func<IReadOnlyList<string>> WorkflowIds,
    Func<IReadOnlyList<string>> EndpointIds,
    Func<string, string?> GlobalSource,
    ScriptFetchSession Fetch);

/// <summary>Converts <see cref="JsonElement"/> values into the plain object model the endpoint executor uses.</summary>
public static class ScriptJson
{
    /// <summary>Maps JSON to <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c>, <c>long</c>, <c>double</c>, <c>string</c>, <c>bool</c> or null.</summary>
    public static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlain(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
