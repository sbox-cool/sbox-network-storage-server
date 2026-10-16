using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>Whether a firing identity can take part in response evaluation.</summary>
public enum ResponseEligibility
{
    Eligible,
    Anonymous,
    Blank,
}

/// <summary>One stored integrity flag reduced to what the response model reads.</summary>
public readonly record struct ResponseHistoryRow(long CreatedAtUnixMs, string State);

/// <summary>
/// Typed dry-run decision. <see cref="Stage"/> is null when the identity is ineligible.
/// When <see cref="Approximate"/> is true the stage is a lower bound: the bounded
/// history read ended before the lookback boundary and before saturation.
/// </summary>
public sealed record ResponseDecision(ResponseEligibility Eligibility, string? Stage, int CountedFirings, bool Approximate);

/// <summary>
/// Dry-run response model for the integrity module. It maps a player's recent counted
/// firings of one detector to the stage enforcement would use and records that as
/// evidence only: it never changes a request, a block, or an alert.
/// </summary>
public static class IntegrityResponseEvaluator
{
    public const string ModeOff = "off";
    public const string ModeDryRun = "dry-run";
    public const int DefaultLookbackHours = 24;
    public const int MinLookbackHours = 1;
    public const int MaxLookbackHours = 168;

    /// <summary>Rows read per evaluation. Older history beyond this is not examined.</summary>
    public const int HistoryBound = 16;

    /// <summary>Counted firings at which the stage saturates at block-all.</summary>
    public const int SaturationCount = 4;

    public const int WouldApplyMaxBytes = 1024;
    public const int WouldApplySchemaVersion = 1;

    /// <summary>Identity the endpoint pipeline uses when no player is known.</summary>
    public const string AnonymousIdentity = "anonymous";

    private static readonly TimeSpan MinuteWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultConflictWindow = TimeSpan.FromMinutes(10);

    public static ResponseEligibility EligibilityOf(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return ResponseEligibility.Blank;
        return steamId == AnonymousIdentity ? ResponseEligibility.Anonymous : ResponseEligibility.Eligible;
    }

    /// <summary>Stage a firing reaches given the number of prior counted firings.</summary>
    public static string StageFor(int countedFirings) => countedFirings switch
    {
        <= 0 => "flag",
        1 => "rate-limit",
        2 => "shadow",
        3 => "block-writes",
        _ => "block-all",
    };

    /// <summary>
    /// Suppression window for a detector. Repeats inside the window of the immediately
    /// older flag do not count. Revision tampering and unknown detectors have no window
    /// (zero), so they are never suppressed.
    /// </summary>
    public static TimeSpan WindowFor(string detector, TimeSpan conflictWindow) => detector switch
    {
        IntegrityDetectors.CallRate or IntegrityDetectors.ValueDelta => MinuteWindow,
        IntegrityDetectors.AntiRollbackConflict or IntegrityDetectors.DirectWriteAttempt
            => conflictWindow == TimeSpan.Zero ? DefaultConflictWindow : conflictWindow,
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Pure decision over history rows for one player and detector, newest first.
    /// Rows older than the lookback boundary end the scan (boundary inclusive). A row is
    /// suppressed when it falls inside <paramref name="window"/> of the immediately older
    /// row, regardless of that row's state. Rows in a reviewed state are excluded from the
    /// count but still serve as the older neighbour for suppression. The oldest row of a
    /// full read has no visible neighbour, so it is not counted; the stage then stays a
    /// lower bound and <see cref="ResponseDecision.Approximate"/> is set.
    /// </summary>
    public static ResponseDecision Decide(IReadOnlyList<ResponseHistoryRow> newestFirst, long nowUnixMs,
        TimeSpan lookback, TimeSpan window)
    {
        var boundary = nowUnixMs - (long)lookback.TotalMilliseconds;
        var windowMs = (long)window.TotalMilliseconds;
        var counted = 0;
        var boundaryReached = false;
        for (var i = 0; i < newestFirst.Count && counted < SaturationCount; i++)
        {
            var row = newestFirst[i];
            if (row.CreatedAtUnixMs < boundary)
            {
                boundaryReached = true;
                break;
            }
            if (IsReviewed(row.State)) continue;
            if (i + 1 < newestFirst.Count)
            {
                if (row.CreatedAtUnixMs - newestFirst[i + 1].CreatedAtUnixMs < windowMs) continue;
            }
            else if (newestFirst.Count >= HistoryBound)
            {
                continue;
            }
            counted++;
        }
        var approximate = !boundaryReached && counted < SaturationCount && newestFirst.Count >= HistoryBound;
        return new ResponseDecision(ResponseEligibility.Eligible, StageFor(counted), counted, approximate);
    }

    /// <summary>
    /// Reads at most <see cref="HistoryBound"/> flags for the player and detector, newest first,
    /// and decides. Ineligible identities return without touching the store.
    /// </summary>
    public static async Task<ResponseDecision> EvaluateAsync(IModuleDataStore modules, string projectId,
        string steamId, string detector, TimeSpan window, TimeSpan lookback, DateTimeOffset at, CancellationToken ct)
    {
        var eligibility = EligibilityOf(steamId);
        if (eligibility != ResponseEligibility.Eligible)
            return new ResponseDecision(eligibility, null, 0, false);

        var rows = await modules.ListFlagsAsync(projectId, state: null, detector: detector, severity: null,
            steamId: steamId, limit: HistoryBound, ct);
        var history = new List<ResponseHistoryRow>(rows.Count);
        foreach (var row in rows) history.Add(ToHistoryRow(row));
        return Decide(history, at.ToUnixTimeMilliseconds(), lookback, window);
    }

    /// <summary>
    /// Builds the dry-run <c>wouldApply</c> evidence. Optional fields are dropped in order
    /// until the UTF-8 size is within <see cref="WouldApplyMaxBytes"/>; stage and detector are always kept.
    /// </summary>
    public static JsonObject BuildWouldApply(ResponseDecision decision, string detector, string hookSource, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(decision.Stage);
        var evidence = new JsonObject
        {
            ["stage"] = decision.Stage,
            ["detector"] = detector,
            ["hookSource"] = hookSource,
            ["windowSeconds"] = (long)window.TotalSeconds,
            ["schemaVersion"] = WouldApplySchemaVersion,
            ["priorCount"] = decision.CountedFirings,
            ["approximate"] = decision.Approximate,
        };
        foreach (var optional in new[] { "hookSource", "windowSeconds", "schemaVersion", "priorCount", "approximate" })
        {
            if (Encoding.UTF8.GetByteCount(evidence.ToJsonString()) <= WouldApplyMaxBytes) break;
            evidence.Remove(optional);
        }
        return evidence;
    }

    private static bool IsReviewed(string state) => state is "dismissed" or "reviewed-legitimate";

    private static ResponseHistoryRow ToHistoryRow(JsonElement row)
    {
        var created = row.TryGetProperty("created_at_unix_ms", out var at) && at.ValueKind == JsonValueKind.Number
            ? at.GetInt64() : 0;
        var state = row.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? string.Empty : string.Empty;
        return new ResponseHistoryRow(created, state);
    }
}
