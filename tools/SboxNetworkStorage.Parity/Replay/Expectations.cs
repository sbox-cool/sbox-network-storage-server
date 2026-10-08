using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Parity.Corpus;
using SboxNetworkStorage.Parity.Normalization;

namespace SboxNetworkStorage.Parity.Replay;

/// <summary>Evaluates a step's authored expectations against the raw (un-normalized) response.</summary>
public static class Expectations
{
    public static List<string> Evaluate(Expectation? expect, int status, JsonElement? body, TemplateContext context)
    {
        var failures = new List<string>();
        if (expect is null)
        {
            return failures;
        }

        if (expect.Status is { } expectedStatus && expectedStatus != status)
        {
            failures.Add($"status {status}, expected {expectedStatus}");
        }

        foreach (var path in expect.RequiredFields ?? [])
        {
            if (body is not { } root || !JsonPath.Parse(path).Select(root).Any())
            {
                failures.Add($"missing required field {path}");
            }
        }

        foreach (var (path, expected) in expect.FieldEquals ?? [])
        {
            var actual = body is { } root ? JsonPath.Parse(path).Select(root).Cast<JsonElement?>().FirstOrDefault() : null;
            var expectedNode = context.Expand(expected);
            var actualNode = actual is { } value ? JsonNode.Parse(value.GetRawText()) : null;
            if (actual is null)
            {
                failures.Add($"{path} missing, expected {expectedNode?.ToJsonString() ?? "null"}");
            }
            else if (!JsonNode.DeepEquals(actualNode, expectedNode))
            {
                failures.Add($"{path} = {actualNode?.ToJsonString() ?? "null"}, expected {expectedNode?.ToJsonString() ?? "null"}");
            }
        }

        return failures;
    }
}
