using System.Globalization;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates.ReleaseStatus;

namespace SboxNetworkStorage.Server.Owner;

public enum ReleaseCardState
{
    /// <summary>No fetch has finished yet.</summary>
    Checking,
    /// <summary>The last fetch failed and no copy was ever read.</summary>
    Unavailable,
    Available,
}

public enum ServerReleaseState
{
    /// <summary>The running version or the latest release version is unknown.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable,
    /// <summary>Running a build newer than the latest published release, such as a pre-release.</summary>
    AheadOfRelease,
}

/// <summary>
/// The owner dashboard's release status card. It is built from the last good snapshot and the state of the
/// latest attempt, so the view never reads the service or the network itself.
/// </summary>
/// <param name="Document">The last document read successfully, or null when none was read.</param>
/// <param name="FetchedAt">When <paramref name="Document"/> was read.</param>
/// <param name="LastAttemptAt">When the most recent attempt finished, or null before the first one.</param>
/// <param name="LastAttemptFailed">True when the most recent attempt failed; the document above may then be stale.</param>
/// <param name="Now">The render time, used for relative ages.</param>
/// <param name="RunningVersion">This server's version, compared against its latest release.</param>
public sealed record OwnerReleaseStatusCard(ReleaseStatusDocument? Document, DateTimeOffset? FetchedAt, DateTimeOffset? LastAttemptAt,
    bool LastAttemptFailed, DateTimeOffset Now, string RunningVersion)
{
    public static OwnerReleaseStatusCard From(ReleaseStatusService service, DateTimeOffset now)
    {
        var snapshot = service.Current;
        return new OwnerReleaseStatusCard(snapshot?.Document, snapshot?.FetchedAt, service.LastAttemptAt, service.LastAttemptFailed, now, BuildInfo.Version);
    }

    public ReleaseCardState State => Document is not null ? ReleaseCardState.Available
        : LastAttemptAt is null ? ReleaseCardState.Checking
        : ReleaseCardState.Unavailable;

    /// <summary>A copy is shown, but the latest attempt failed, so it may no longer be current.</summary>
    public bool Stale => Document is not null && LastAttemptFailed;

    /// <summary>The oldest merged change still waiting for a release: the document's value, or the oldest pending item.</summary>
    public DateTimeOffset? LibraryPendingSince => Document is null ? null
        : Document.Library.PendingSince ?? Oldest(Document.Library.Pending);

    public DateTimeOffset? ServerPendingSince => Document is null ? null
        : Document.Server.PendingSince ?? Oldest(Document.Server.Pending);

    public ServerReleaseState ServerState
    {
        get
        {
            var latest = Document?.Server.LatestRelease.Version;
            if (!SemanticVersion.TryParse(RunningVersion, out var running) || !SemanticVersion.TryParse(latest, out var newest))
            {
                return ServerReleaseState.Unknown;
            }

            return running.CompareTo(newest) switch
            {
                < 0 => ServerReleaseState.UpdateAvailable,
                0 => ServerReleaseState.UpToDate,
                _ => ServerReleaseState.AheadOfRelease,
            };
        }
    }

    /// <summary>Badge text and CSS class for a CI conclusion.</summary>
    public static (string Label, string CssClass) CiBadge(string? conclusion) => conclusion switch
    {
        "success" => ("CI passing", "badge positive"),
        "failure" => ("CI failing", "badge failed"),
        "pending" => ("CI running", "badge"),
        _ => ("CI status unknown", "badge"),
    };

    public static (string Label, string CssClass) ServerBadge(ServerReleaseState state, string? latestVersion) => state switch
    {
        ServerReleaseState.UpToDate => ("Up to date", "badge positive"),
        ServerReleaseState.UpdateAvailable => ($"Update available: {latestVersion}", "badge attention"),
        ServerReleaseState.AheadOfRelease => ("Ahead of the latest release", "badge"),
        _ => ("Version comparison unavailable", "badge"),
    };

    public static string Count(int count, string singularNoun) => count == 1 ? $"1 {singularNoun}" : $"{count} {singularNoun}s";

    /// <summary>"N days behind", or "behind" when the number of days is unknown.</summary>
    public static string BehindText(int? days) => days is { } known ? $"{Count(known, "day")} behind" : "behind";

    /// <summary>The first seven characters of a commit, the form the dashboard shows.</summary>
    public static string? Short(string? commit) => commit is null ? null : commit.Length > 7 ? commit[..7] : commit;

    /// <summary>"today", "1 day ago" or "N days ago". A time in the future reads as today.</summary>
    public static string AgeText(DateTimeOffset since, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now - since).TotalDays);
        return days <= 0 ? "today" : days == 1 ? "1 day ago" : $"{days} days ago";
    }

    public static string Date(DateTimeOffset? at) => at is null ? "unknown date" : at.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Stamp(DateTimeOffset? at) => at is null ? "unknown time" : at.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Oldest(IEnumerable<PendingChange> changes)
    {
        DateTimeOffset? oldest = null;
        foreach (var change in changes)
        {
            if (change.MergedAt is { } at && (oldest is null || at < oldest))
            {
                oldest = at;
            }
        }

        return oldest;
    }
}
