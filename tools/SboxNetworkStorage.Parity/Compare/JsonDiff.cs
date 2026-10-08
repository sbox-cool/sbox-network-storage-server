using System.Text.Json;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Parity.Compare;

public enum DifferenceKind
{
    Changed,
    OnlyStable,
    OnlyCandidate,
}

/// <param name="Path">e.g. <c>status</c>, <c>contentType</c>, <c>header:cache-control</c>, <c>body:$.items[0].name</c>.</param>
public sealed record Difference(string Path, DifferenceKind Kind, string? Stable, string? Candidate)
{
    public override string ToString() => Kind switch
    {
        DifferenceKind.Changed => $"~ {Path}: {Stable} → {Candidate}",
        DifferenceKind.OnlyStable => $"- {Path}: {Stable} (only in stable)",
        _ => $"+ {Path}: {Candidate} (only in candidate)",
    };
}

/// <summary>Structural JSON diff over already-normalized documents (keys sorted, unordered arrays sorted).</summary>
public static class JsonDiff
{
    private const int ValuePreview = 160;

    public static void Compare(JsonNode? stable, JsonNode? candidate, string path, List<Difference> differences)
    {
        switch (stable, candidate)
        {
            case (JsonObject left, JsonObject right):
                foreach (var name in left.Select(p => p.Key).Union(right.Select(p => p.Key)).OrderBy(n => n, StringComparer.Ordinal))
                {
                    var childPath = $"{path}{Member(name)}";
                    var inLeft = left.TryGetPropertyValue(name, out var l);
                    var inRight = right.TryGetPropertyValue(name, out var r);
                    if (inLeft && inRight)
                    {
                        Compare(l, r, childPath, differences);
                    }
                    else if (inLeft)
                    {
                        differences.Add(new Difference(childPath, DifferenceKind.OnlyStable, Preview(l), null));
                    }
                    else
                    {
                        differences.Add(new Difference(childPath, DifferenceKind.OnlyCandidate, null, Preview(r)));
                    }
                }

                break;
            case (JsonArray left, JsonArray right):
                for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (i >= right.Count)
                    {
                        differences.Add(new Difference(childPath, DifferenceKind.OnlyStable, Preview(left[i]), null));
                    }
                    else if (i >= left.Count)
                    {
                        differences.Add(new Difference(childPath, DifferenceKind.OnlyCandidate, null, Preview(right[i])));
                    }
                    else
                    {
                        Compare(left[i], right[i], childPath, differences);
                    }
                }

                break;
            default:
                if (!JsonNode.DeepEquals(stable, candidate))
                {
                    differences.Add(new Difference(path, DifferenceKind.Changed, Preview(stable), Preview(candidate)));
                }

                break;
        }
    }

    public static string Preview(JsonNode? node)
    {
        var text = node?.ToJsonString(CompactOptions) ?? "null";
        return text.Length > ValuePreview ? text[..ValuePreview] + "…" : text;
    }

    private static readonly JsonSerializerOptions CompactOptions = new(Json.Options) { WriteIndented = false };

    private static string Member(string name) =>
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') && name.Length > 0 ? $".{name}" : $"['{name}']";
}
