using System.Text.Json.Nodes;
using SboxNetworkStorage.Parity.Normalization;
using SboxNetworkStorage.Parity.Results;

namespace SboxNetworkStorage.Parity.Compare;

/// <summary>A step whose normalized responses differ between the two runs.</summary>
public sealed record StepMismatch(
    string Scenario,
    string Step,
    RecordedRequest? Request,
    NormalizedResponse? Stable,
    NormalizedResponse? Candidate,
    IReadOnlyList<Difference> Unlisted,
    IReadOnlyList<(Difference Difference, string Reason)> Allowed);

public static class ResultComparer
{
    public static List<StepMismatch> Compare(RunResults stable, RunResults candidate, IntentionalDifferences allow)
    {
        var mismatches = new List<StepMismatch>();
        var candidateScenarios = candidate.Scenarios.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var scenario in stable.Scenarios)
        {
            if (!candidateScenarios.Remove(scenario.Name, out var other))
            {
                mismatches.Add(Missing(scenario.Name, "*", "scenario", allow, DifferenceKind.OnlyStable));
                continue;
            }

            var otherSteps = other.Steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var step in scenario.Steps)
            {
                if (!otherSteps.Remove(step.Id, out var otherStep))
                {
                    mismatches.Add(Missing(scenario.Name, step.Id, "step", allow, DifferenceKind.OnlyStable));
                    continue;
                }

                var differences = CompareSteps(step, otherStep);
                if (differences.Count > 0)
                {
                    mismatches.Add(Classify(scenario.Name, step.Id, step.Request, step.Response, otherStep.Response, differences, allow));
                }
            }

            foreach (var extra in otherSteps.Keys)
            {
                mismatches.Add(Missing(scenario.Name, extra, "step", allow, DifferenceKind.OnlyCandidate));
            }
        }

        foreach (var extra in candidateScenarios.Keys)
        {
            mismatches.Add(Missing(extra, "*", "scenario", allow, DifferenceKind.OnlyCandidate));
        }

        return mismatches;
    }

    public static List<Difference> CompareSteps(StepResult stable, StepResult candidate)
    {
        var differences = CompareResponses(stable.Response, candidate.Response, stable.Error, candidate.Error);
        JsonDiff.Compare(
            System.Text.Json.JsonSerializer.SerializeToNode(stable.Request, Json.Options),
            System.Text.Json.JsonSerializer.SerializeToNode(candidate.Request, Json.Options),
            "request:$", differences);
        return differences;
    }

    public static List<Difference> CompareResponses(NormalizedResponse? stable, NormalizedResponse? candidate, string? stableError = null, string? candidateError = null)
    {
        var differences = new List<Difference>();
        if (stable is null || candidate is null)
        {
            differences.Add(new Difference("response", DifferenceKind.Changed,
                stable is null ? $"<no response: {stableError}>" : $"status {stable.Status}",
                candidate is null ? $"<no response: {candidateError}>" : $"status {candidate.Status}"));

            return differences;
        }

        if (stable.Status != candidate.Status)
        {
            differences.Add(new Difference("status", DifferenceKind.Changed, stable.Status.ToString(), candidate.Status.ToString()));
        }

        if (!string.Equals(stable.ContentType, candidate.ContentType, StringComparison.Ordinal))
        {
            differences.Add(new Difference("contentType", DifferenceKind.Changed, stable.ContentType ?? "<none>", candidate.ContentType ?? "<none>"));
        }

        var stableHeaders = stable.Headers ?? [];
        var candidateHeaders = candidate.Headers ?? [];
        foreach (var name in stableHeaders.Keys.Union(candidateHeaders.Keys).Order(StringComparer.Ordinal))
        {
            var inStable = stableHeaders.TryGetValue(name, out var l);
            var inCandidate = candidateHeaders.TryGetValue(name, out var r);
            if (inStable && inCandidate && l != r)
            {
                differences.Add(new Difference($"header:{name}", DifferenceKind.Changed, l, r));
            }
            else if (inStable != inCandidate)
            {
                differences.Add(new Difference($"header:{name}", inStable ? DifferenceKind.OnlyStable : DifferenceKind.OnlyCandidate, l, r));
            }
        }

        JsonDiff.Compare(stable.Body, candidate.Body, "body:$", differences);
        return differences;
    }

    private static StepMismatch Classify(
        string scenario,
        string step,
        RecordedRequest? request,
        NormalizedResponse? stable,
        NormalizedResponse? candidate,
        List<Difference> differences,
        IntentionalDifferences allow)
    {
        var unlisted = new List<Difference>();
        var allowed = new List<(Difference, string)>();
        foreach (var difference in differences)
        {
            if (allow.Match(scenario, step, difference.Path) is { } reason)
            {
                allowed.Add((difference, reason));
            }
            else
            {
                unlisted.Add(difference);
            }
        }

        return new StepMismatch(scenario, step, request, stable, candidate, unlisted, allowed);
    }

    private static StepMismatch Missing(string scenario, string step, string what, IntentionalDifferences allow, DifferenceKind kind)
    {
        var difference = new Difference(what, kind, kind == DifferenceKind.OnlyStable ? "present" : null, kind == DifferenceKind.OnlyCandidate ? "present" : null);
        return Classify(scenario, step, null, null, null, [difference], allow);
    }

    public static string Describe(NormalizedResponse? response)
    {
        if (response is null)
        {
            return "<no response>";
        }

        var headers = response.Headers is { Count: > 0 } h ? "\n    headers: " + string.Join(", ", h.Select(p => $"{p.Key}={p.Value}")) : "";
        var body = response.Body is null ? "<empty>" : Indent(response.Body.ToJsonString(Json.Options), 4);
        return $"{response.Status} {response.ContentType ?? "<no content-type>"}{headers}\n    body: {body}";
    }

    public static string DescribeRequest(RecordedRequest request)
    {
        var query = request.Query is { Count: > 0 } q ? "?" + string.Join("&", q.Select(p => $"{p.Key}={p.Value}")) : "";
        var headers = request.Headers is { Count: > 0 } h ? "\n    headers: " + string.Join(", ", h.Select(p => $"{p.Key}: {p.Value}")) : "";
        var body = request.Body is JsonNode node ? "\n    body: " + Truncate(node.ToJsonString(), 600) : "";
        return $"{request.Method} {request.Path}{query}{headers}{body}";
    }

    private static string Indent(string text, int spaces)
    {
        var lines = Truncate(text, 4000).Split('\n');
        return string.Join("\n" + new string(' ', spaces), lines);
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}
