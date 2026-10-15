using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>One <c>release.json</c> <c>testedLibraries</c> entry: the release's own test evidence for one library build.</summary>
public sealed record TestedLibrary(string Library, long? Revision, string? Source, string? Engine, string? Target, string Result);

/// <summary>
/// Result of matching a release's test evidence against the library builds this server actually serves.
/// <see cref="Failed"/> blocks an install; <see cref="Untested"/> is a warning only.
/// </summary>
public sealed record CompatibilityReview(IReadOnlyList<string> Failed, IReadOnlyList<string> Untested)
{
    public bool Blocked => Failed.Count > 0;
}

/// <summary>Preflight contract: the signed <c>release.json</c> evidence checked against the observed library mix.</summary>
public static class ReleaseCompatibility
{
    /// <summary>Library builds below this share of requests are ignored: they cannot justify blocking an update.</summary>
    public const decimal MinimumShare = 0.01m;

    /// <summary>Same JSON casing as <c>release.json</c> and the host's <c>update observed-libraries --json</c>.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    private sealed record ReleaseManifest(List<TestedLibrary>? TestedLibraries);

    /// <summary>
    /// The tested libraries of a release, or null when the release predates the evidence (absent field = no evidence,
    /// never a failure). Malformed JSON is also treated as no evidence; the signature already covered the file.
    /// </summary>
    public static IReadOnlyList<TestedLibrary>? ParseTestedLibraries(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseManifest>(json, Json)?.TestedLibraries;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CompatibilityReview Review(IReadOnlyList<TestedLibrary> tested, IReadOnlyList<ObservedLibrary> observed)
    {
        var failed = new List<string>();
        var untested = new List<string>();
        var total = observed.Sum(library => library.Requests);
        if (total == 0)
        {
            return new CompatibilityReview(failed, untested);
        }

        foreach (var library in observed.Where(library => (decimal)library.Requests / total >= MinimumShare))
        {
            var name = Describe(library);
            var matches = tested.Where(entry => Matches(entry, library)).ToList();
            var share = (decimal)library.Requests / total;
            if (matches.Any(entry => entry.Result == "fail"))
            {
                failed.Add($"{name} ({share * 100:0.0}% of requests)");
            }
            else if (!matches.Any(entry => entry.Result is "ci-only" or "pass"))
            {
                untested.Add($"{name} ({share * 100:0.0}% of requests)");
            }
        }

        return new CompatibilityReview(failed, untested);
    }

    /// <summary>
    /// Revisions decide when both sides know theirs. Most games send no package revision (and pre-1.1 libraries send no
    /// version: <c>legacy</c>), so otherwise the library version decides: a tested legacy revision covers legacy traffic.
    /// </summary>
    internal static bool Matches(TestedLibrary entry, ObservedLibrary observed)
        => entry.Revision is { } revision && observed.LibraryRevision is { } seen
            ? seen == revision
            : string.Equals(entry.Library, observed.LibraryVersion, StringComparison.Ordinal);

    private static string Describe(ObservedLibrary library)
        => library.LibraryRevision is { } revision ? $"{library.LibraryVersion} r{revision}" : library.LibraryVersion;
}
