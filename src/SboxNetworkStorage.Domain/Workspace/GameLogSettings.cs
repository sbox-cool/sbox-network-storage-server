using System.Collections.Generic;

namespace SboxNetworkStorage.Domain.Workspace;

/// <summary>
/// Per-project structured game-log settings (epic #111, L1/L2). Off unless <see cref="Enabled"/> is true;
/// while off every game-log route answers as if it did not exist.
/// </summary>
/// <param name="Enabled">Master switch. Default off.</param>
/// <param name="Streams">Per-stream overrides keyed by stream name (<c>default</c>, <c>chat</c>, <c>audit</c>).</param>
/// <param name="AllowedEvents">Event names (exact, or <c>prefix.*</c>) that public-key client callers may emit besides <c>client.*</c> and <c>chat.*</c>.</param>
/// <param name="RateLimitPerMinute">Project-wide ingest budget in entries per minute. Null uses the server default.</param>
public sealed record GameLogSettings(
    bool Enabled = false,
    Dictionary<string, GameLogStreamSettings>? Streams = null,
    List<string>? AllowedEvents = null,
    int? RateLimitPerMinute = null);

/// <summary>One stream's settings. Null fields take the stream's default.</summary>
/// <param name="Enabled">Accept entries for this stream. Defaults: default on, audit on, chat off.</param>
/// <param name="RetentionDays">Days to keep entries (1..3650). Defaults: default 30, chat 14, audit 365.</param>
/// <param name="Sampling">Fraction of entries kept at ingest (0..1]. Default 1.</param>
public sealed record GameLogStreamSettings(bool? Enabled = null, int? RetentionDays = null, double? Sampling = null);
