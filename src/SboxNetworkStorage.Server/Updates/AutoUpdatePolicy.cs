using System.Globalization;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// A daily UTC window <c>HH:MM-HH:MM</c>. The start is inclusive and the end exclusive;
/// a window whose end is before its start wraps past midnight, and <c>00:00-24:00</c> is always open.
/// </summary>
public sealed record UpdateWindow(TimeSpan Start, TimeSpan End)
{
    public static bool TryParse(string? text, out UpdateWindow window)
    {
        window = new UpdateWindow(TimeSpan.Zero, TimeSpan.Zero);
        var parts = text?.Split('-') ?? [];
        if (parts.Length != 2 || !TryParseTime(parts[0], allowEndOfDay: false, out var start) || !TryParseTime(parts[1], allowEndOfDay: true, out var end)
            || start == end)
        {
            return false;
        }

        window = new UpdateWindow(start, end);
        return true;
    }

    public bool Contains(DateTimeOffset now)
    {
        var time = now.UtcDateTime.TimeOfDay;
        return Start < End
            ? time >= Start && time < End
            : time >= Start || time < End;
    }

    public override string ToString()
        => $"{(int)Start.TotalHours:00}:{Start.Minutes:00}-{(int)End.TotalHours:00}:{End.Minutes:00} UTC";

    private static bool TryParseTime(string text, bool allowEndOfDay, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var value = text.Trim();
        if (value.Length != 5 || value[2] != ':'
            || !int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59 || hours > 24 || (hours == 24 && (minutes != 0 || !allowEndOfDay)))
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }
}

/// <summary>The <c>updates.*</c> settings that gate unattended installs.</summary>
public sealed record AutoUpdateSettings(bool AutoInstall, string Channel, UpdateWindow Window, int MinReleaseAgeHours)
{
    public const string StableChannel = "stable";
    public const string CanaryChannel = "canary";

    public static AutoUpdateSettings FromConfig(EffectiveConfig config)
    {
        var channel = config.GetString("updates.channel");
        UpdateWindow.TryParse(config.GetString("updates.window"), out var window);
        var configuredAge = config.GetInteger("updates.min_release_age_hours");
        var age = configuredAge >= 0 ? (int)Math.Min(configuredAge, int.MaxValue) : channel == CanaryChannel ? 0 : 24;
        return new AutoUpdateSettings(config.GetBoolean("updates.auto_install"), channel, window, age);
    }
}

/// <summary>Whether an unattended run installs, and why not.</summary>
public sealed record AutoUpdateDecision(bool Install, string Reason)
{
    public static AutoUpdateDecision Skip(string reason) => new(false, reason);
}

/// <summary>Pure gating rules for <c>sbox-ns update --auto</c>; no I/O, so every boundary is testable.</summary>
public static class AutoUpdatePolicy
{
    /// <summary>
    /// Instances share one binary, so they must agree on the channel and window. Every
    /// instance must opt in; the strictest release age wins.
    /// </summary>
    public static AutoUpdateSettings Combine(IReadOnlyList<(string Instance, AutoUpdateSettings Settings)> instances)
    {
        if (instances.Count == 0)
        {
            throw new InvalidOperationException("no sbox-ns instances were found");
        }

        var first = instances[0];
        foreach (var (name, settings) in instances.Skip(1))
        {
            if (settings.Channel != first.Settings.Channel || settings.Window != first.Settings.Window)
            {
                throw new InvalidOperationException(
                    $"instances share one binary but disagree on updates.channel/updates.window ({first.Instance}: {first.Settings.Channel} {first.Settings.Window}; {name}: {settings.Channel} {settings.Window})");
            }
        }

        var optedOut = instances.Where(i => !i.Settings.AutoInstall).Select(i => i.Instance).ToList();
        return first.Settings with
        {
            AutoInstall = optedOut.Count == 0,
            MinReleaseAgeHours = instances.Max(i => i.Settings.MinReleaseAgeHours),
        };
    }

    /// <summary>Checks that need no network: the opt-in and the time window.</summary>
    public static AutoUpdateDecision EvaluateSchedule(AutoUpdateSettings settings, DateTimeOffset now)
    {
        if (!settings.AutoInstall)
        {
            return AutoUpdateDecision.Skip("unattended updates are off (updates.auto_install = false on at least one instance)");
        }

        return settings.Window.Contains(now)
            ? new AutoUpdateDecision(true, "inside the update window")
            : AutoUpdateDecision.Skip($"outside the update window {settings.Window}");
    }

    /// <summary>
    /// Checks against the release the feed returned for the channel. A held channel
    /// returns its pinned version, which is installed only when it is newer: unattended
    /// updates never downgrade. A version whose unattended install already failed is not retried.
    /// </summary>
    public static AutoUpdateDecision EvaluateRelease(AutoUpdateSettings settings, string currentVersion, ReleaseInfo release,
        string? failedVersion, DateTimeOffset now)
    {
        if (!SemanticVersion.TryParse(release.Version, out var candidate) || !SemanticVersion.TryParse(currentVersion, out var current))
        {
            return AutoUpdateDecision.Skip($"cannot compare versions ({currentVersion} installed, {release.Version} offered)");
        }

        var held = release.Held ? $" (channel {settings.Channel} is held at {release.Version})" : string.Empty;
        if (candidate.CompareTo(current) <= 0)
        {
            return AutoUpdateDecision.Skip($"up to date: {currentVersion} installed, {release.Version} offered{held}");
        }

        if (failedVersion is not null && SemanticVersion.TryParse(failedVersion, out var failed) && failed.CompareTo(candidate) == 0)
        {
            return AutoUpdateDecision.Skip(
                $"{release.Version} failed to install unattended before; it is not retried. Install it by hand with `sbox-ns update --version {release.Version}`");
        }

        if (settings.MinReleaseAgeHours > 0)
        {
            var since = release.PromotedAt ?? release.PublishedAt;
            if (since is null)
            {
                return AutoUpdateDecision.Skip($"{release.Version} has no promotedAt/publishedAt, so its age cannot be checked");
            }

            var age = now - since.Value;
            if (age < TimeSpan.FromHours(settings.MinReleaseAgeHours))
            {
                return AutoUpdateDecision.Skip(
                    $"{release.Version} has been on {settings.Channel} for {Math.Max(0, age.TotalHours):F1} h; waiting for {settings.MinReleaseAgeHours} h (updates.min_release_age_hours)");
            }
        }

        return new AutoUpdateDecision(true, $"installing {release.Version} over {currentVersion}{held}");
    }
}
