using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Updates;

public enum VerdictStatus
{
    /// <summary>At least one library's error rate jumped past both thresholds versus the previous binary.</summary>
    Regression,
    /// <summary>At least one library had enough traffic on both sides and none regressed.</summary>
    Ok,
    /// <summary>Traffic on the current binary is too thin on every library to judge.</summary>
    InsufficientData,
    /// <summary>No previous binary has traffic in the baseline window, so there is nothing to compare against.</summary>
    NoBaseline,
}

public enum LibraryOutcome
{
    /// <summary>Error rate rose by at least <see cref="VerdictThresholds.MinPointDelta"/> and at least <see cref="VerdictThresholds.MinRatio"/>× the baseline.</summary>
    Regressed,
    Ok,
    /// <summary>Either side has fewer than <see cref="VerdictThresholds.MinRequests"/> requests.</summary>
    LowTraffic,
    /// <summary>Not seen on the previous binary; a new library build has no baseline and is never judged.</summary>
    NewLibrary,
    NoBaseline,
}

/// <summary>Error rate = (rejected + server errors) / requests, over the window (current) or baseline (previous binary).</summary>
public sealed record LibraryVerdict(string Library, LibraryOutcome Outcome, long PostRequests, decimal PostErrorRate,
    long BaselineRequests, decimal BaselineErrorRate);

public sealed record PostUpdateVerdict(string Version, VerdictStatus Status, string? BaselineVersion, int WindowHours,
    IReadOnlyList<LibraryVerdict> Libraries)
{
    /// <summary>The JSON shape of <c>update verify --json</c>; the unattended updater reads the same shape back.</summary>
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static PostUpdateVerdict? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<PostUpdateVerdict>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record VerdictThresholds(long MinRequests = 50, decimal MinPointDelta = 0.10m, decimal MinRatio = 2.0m)
{
    public static VerdictThresholds Default { get; } = new();
}

/// <summary>Pure comparison of per-library outcome totals. The store read lives in <see cref="PostUpdateVerifier"/>.</summary>
public static class PostUpdateEvaluator
{
    public static PostUpdateVerdict Evaluate(string version, int windowHours, IEnumerable<ClientOutcomeTotals> current,
        IEnumerable<ClientOutcomeTotals> baselineCandidates, VerdictThresholds thresholds)
    {
        var currentRows = current.Where(row => row.ServerVersion == version).ToArray();
        var others = baselineCandidates.Where(row => row.ServerVersion != version).ToArray();
        var baselineVersion = others.Length == 0 ? null
            : others.GroupBy(row => row.ServerVersion).OrderByDescending(group => group.Max(row => row.LastHour)).ThenBy(group => group.Key, StringComparer.Ordinal).First().Key;
        var baselineRows = baselineVersion is null ? [] : others.Where(row => row.ServerVersion == baselineVersion).ToArray();
        var baselineByLibrary = Sum(baselineRows);
        var libraries = new List<LibraryVerdict>();
        foreach (var (library, post) in Sum(currentRows))
        {
            if (baselineVersion is null)
            {
                libraries.Add(new LibraryVerdict(library, LibraryOutcome.NoBaseline, post.Requests, Rate(post), 0, 0));
                continue;
            }
            if (!baselineByLibrary.TryGetValue(library, out var baseline))
            {
                libraries.Add(new LibraryVerdict(library, LibraryOutcome.NewLibrary, post.Requests, Rate(post), 0, 0));
                continue;
            }
            var postRate = Rate(post);
            var baseRate = Rate(baseline);
            var outcome = post.Requests < thresholds.MinRequests || baseline.Requests < thresholds.MinRequests ? LibraryOutcome.LowTraffic
                : postRate - baseRate >= thresholds.MinPointDelta && postRate >= thresholds.MinRatio * baseRate ? LibraryOutcome.Regressed
                : LibraryOutcome.Ok;
            libraries.Add(new LibraryVerdict(library, outcome, post.Requests, postRate, baseline.Requests, baseRate));
        }
        libraries.Sort((a, b) => string.CompareOrdinal(a.Library, b.Library));

        var status = baselineVersion is null ? VerdictStatus.NoBaseline
            : libraries.Any(library => library.Outcome == LibraryOutcome.Regressed) ? VerdictStatus.Regression
            : libraries.Any(library => library.Outcome == LibraryOutcome.Ok) ? VerdictStatus.Ok
            : VerdictStatus.InsufficientData;
        return new PostUpdateVerdict(version, status, baselineVersion, windowHours, libraries);
    }

    private static Dictionary<string, Totals> Sum(IEnumerable<ClientOutcomeTotals> rows)
    {
        var sums = new Dictionary<string, Totals>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            sums.TryGetValue(row.LibraryVersion, out var total);
            sums[row.LibraryVersion] = new Totals(total.Requests + row.Requests, total.Failures + row.Rejected + row.ServerErrors);
        }
        return sums;
    }

    private static decimal Rate(Totals totals) => totals.Requests == 0 ? 0 : (decimal)totals.Failures / totals.Requests;

    private readonly record struct Totals(long Requests, long Failures);
}

/// <summary>Reads the census for one binary and its predecessor, then evaluates the verdict.</summary>
public sealed class PostUpdateVerifier(IClientVersionStore store, TimeProvider time)
{
    /// <summary>The baseline reads seven days, so this is also the longest window the verdict can judge.</summary>
    public const int BaselineHours = 7 * 24;

    /// <summary>Default verdict window: the last day on the new binary.</summary>
    public const int DefaultWindowHours = 24;

    /// <summary>How long after an unattended update the watch and the auto-rollback check keep judging it.</summary>
    public const int WatchWindowHours = 48;

    public async Task<PostUpdateVerdict> VerifyAsync(string version, int windowHours, VerdictThresholds thresholds, CancellationToken ct)
    {
        var nowHour = ClientOutcomeTracker.HourOf(time.GetUtcNow());
        var current = await store.ReadClientOutcomeTotalsAsync(nowHour - windowHours + 1, ct);
        var baseline = await store.ReadClientOutcomeTotalsAsync(nowHour - BaselineHours, ct);
        return PostUpdateEvaluator.Evaluate(version, windowHours, current, baseline, thresholds);
    }
}
