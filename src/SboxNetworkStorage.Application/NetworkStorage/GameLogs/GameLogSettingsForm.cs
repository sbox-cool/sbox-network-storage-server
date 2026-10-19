using System.Collections.Generic;
using System.Globalization;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>
/// Reads the owner's game-log settings form. Fields that are missing or out of range keep their current value,
/// so a malformed submission never silently widens what the project accepts.
/// </summary>
public static class GameLogSettingsForm
{
    /// <summary>Upper bound on the number of extra event names a project may allow.</summary>
    public const int MaxAllowedEvents = 64;

    /// <summary>Upper bound on the length of one allowed event name.</summary>
    public const int MaxEventNameLength = 128;

    private static readonly string[] Streams = [GameLogPolicy.DefaultStream, GameLogPolicy.ChatStream, GameLogPolicy.AuditStream];

    public static GameLogSettings Apply(GameLogSettings current, Func<string, string?> form)
    {
        var streams = new Dictionary<string, GameLogStreamSettings>();
        foreach (var stream in Streams)
        {
            var existing = current.Streams is not null && current.Streams.TryGetValue(stream, out var prior) ? prior : new GameLogStreamSettings();
            streams[stream] = new GameLogStreamSettings(
                Enabled: IsChecked(form($"gameLogs_{stream}_enabled")),
                RetentionDays: ParseRetention(form($"gameLogs_{stream}_retention")) ?? existing.RetentionDays,
                Sampling: ParseSampling(form($"gameLogs_{stream}_sampling")) ?? existing.Sampling);
        }

        return new GameLogSettings(
            Enabled: IsChecked(form("gameLogsEnabled")),
            Streams: streams,
            AllowedEvents: ParseAllowedEvents(form("gameLogsAllowedEvents")) ?? current.AllowedEvents,
            RateLimitPerMinute: ParseRateLimit(form("gameLogsRateLimitPerMinute")) ?? current.RateLimitPerMinute);
    }

    public static int? ParseRetention(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)) return null;
        return days is >= GameLogPolicy.MinRetentionDays and <= GameLogPolicy.MaxRetentionDays ? days : null;
    }

    public static double? ParseSampling(string? raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return value > 0 && value <= 1 ? value : null;
    }

    public static int? ParseRateLimit(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var perMinute)) return null;
        return perMinute >= 1 ? perMinute : null;
    }

    /// <summary>Parses comma- or newline-separated event names. Returns null when the field is absent.</summary>
    public static List<string>? ParseAllowedEvents(string? raw)
    {
        if (raw is null) return null;
        var events = new List<string>();
        foreach (var part in raw.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > MaxEventNameLength || part.Any(char.IsWhiteSpace)) continue;
            if (events.Contains(part, StringComparer.Ordinal)) continue;
            events.Add(part);
            if (events.Count == MaxAllowedEvents) break;
        }

        return events;
    }

    private static bool IsChecked(string? value)
        => string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
}
