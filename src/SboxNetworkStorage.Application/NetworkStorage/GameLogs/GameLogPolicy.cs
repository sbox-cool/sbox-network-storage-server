using System;
using System.Collections.Generic;
using System.Linq;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>Effective settings for one stream after defaults are applied.</summary>
public sealed record GameLogStreamPolicy(bool Enabled, int RetentionDays, double Sampling);

/// <summary>
/// Effective per-project game-log policy (epic #111 L1/L2): stream defaults, retention,
/// sampling, client-allowed events and the ingest limits. Built once per request from
/// the project's <see cref="GameLogSettings"/>.
/// </summary>
public sealed class GameLogPolicy
{
    public const string DefaultStream = "default";
    public const string ChatStream = "chat";
    public const string AuditStream = "audit";

    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const int DefaultRateLimitPerMinute = 12_000;
    public const int MaxBatchEntries = 500;
    public const int MaxBatchBytes = 256 * 1024;
    public const int MaxEntryBytes = 8 * 1024;
    public const int MaxFutureSkewMs = 60_000;
    public const int MaxMessageLength = 2048;
    public const int MaxTraceIdLength = 128;
    public const int MaxClientFieldLength = 64;

    private static readonly IReadOnlyDictionary<string, GameLogStreamPolicy> Defaults =
        new Dictionary<string, GameLogStreamPolicy>(StringComparer.Ordinal)
        {
            [DefaultStream] = new(Enabled: true, RetentionDays: 30, Sampling: 1.0),
            [ChatStream] = new(Enabled: false, RetentionDays: 14, Sampling: 1.0),
            [AuditStream] = new(Enabled: true, RetentionDays: 365, Sampling: 1.0),
        };

    public GameLogPolicy(GameLogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var streams = new Dictionary<string, GameLogStreamPolicy>(StringComparer.Ordinal);
        foreach (var (name, fallback) in Defaults)
        {
            var overrides = settings.Streams is not null && settings.Streams.TryGetValue(name, out var o) ? o : null;
            streams[name] = new GameLogStreamPolicy(
                Enabled: overrides?.Enabled ?? fallback.Enabled,
                RetentionDays: Math.Clamp(overrides?.RetentionDays ?? fallback.RetentionDays, MinRetentionDays, MaxRetentionDays),
                Sampling: Math.Clamp(overrides?.Sampling ?? fallback.Sampling, 0.0, 1.0));
        }

        Streams = streams;
        AllowedEvents = settings.AllowedEvents?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? [];
        RateLimitPerMinute = settings.RateLimitPerMinute is > 0 and var limit ? limit : DefaultRateLimitPerMinute;
    }

    public IReadOnlyDictionary<string, GameLogStreamPolicy> Streams { get; }

    public IReadOnlyList<string> AllowedEvents { get; }

    public int RateLimitPerMinute { get; }

    public static bool IsKnownStream(string stream) => stream is DefaultStream or ChatStream or AuditStream;

    /// <summary>Stream for an entry that does not name one: chat.* → chat, admin.*/command.* → audit, else default.</summary>
    public static string DeriveStream(string eventName)
    {
        if (eventName.StartsWith("chat.", StringComparison.Ordinal)) return ChatStream;
        if (eventName.StartsWith("admin.", StringComparison.Ordinal) || eventName.StartsWith("command.", StringComparison.Ordinal))
            return AuditStream;
        return DefaultStream;
    }

    public bool IsStreamEnabled(string stream) => Streams.TryGetValue(stream, out var s) && s.Enabled;

    public int RetentionDaysFor(string stream) => Streams[stream].RetentionDays;

    /// <summary>Public-key client callers may emit client.*, chat.* and the project's allow-listed events.</summary>
    public bool IsEventAllowedForClient(string eventName)
    {
        if (eventName.StartsWith("client.", StringComparison.Ordinal) || eventName.StartsWith("chat.", StringComparison.Ordinal))
            return true;
        foreach (var allowed in AllowedEvents)
        {
            if (allowed.EndsWith(".*", StringComparison.Ordinal)
                    ? eventName.StartsWith(allowed[..^1], StringComparison.Ordinal)
                    : string.Equals(allowed, eventName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
