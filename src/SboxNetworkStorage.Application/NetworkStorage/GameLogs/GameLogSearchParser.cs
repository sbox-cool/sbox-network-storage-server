using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>Outcome of <see cref="GameLogSearchParser.Parse"/>: a query, or a typed 400 error.</summary>
public sealed record GameLogSearchParseResult(GameLogQuery? Query, string? ErrorCode, string? ErrorMessage)
{
    public bool Ok => Query is not null;
}

/// <summary>
/// Parses the L2 search parameters: <c>from</c>, <c>to</c>, <c>level</c>, <c>event</c>, <c>stream</c>
/// (repeatable), <c>player</c>, <c>attr.&lt;key&gt;</c> (repeatable, AND), <c>q</c> (free text, AND of whole
/// tokens), <c>cursor</c> and <c>limit</c>. Unknown parameters are rejected rather than ignored.
/// </summary>
public static class GameLogSearchParser
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    private const int MaxAttrValueLength = 256;
    private const long MaxTextSearchSpanMs = 31L * 86_400_000L;

    private static readonly Regex AttrKeyPattern = new(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex EventPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex EntryIdPattern = new(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Levels = new(StringComparer.Ordinal) { "debug", "info", "warn", "error" };
    private static readonly HashSet<string> ScalarParameters = new(StringComparer.Ordinal)
    {
        "from", "to", "level", "event", "player", "q", "cursor", "limit",
    };

    public static GameLogSearchParseResult Parse(
        string projectId,
        IEnumerable<KeyValuePair<string, string>> parameters,
        GameLogPolicy policy,
        Func<string, bool> isSteamId,
        DateTimeOffset now)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var streams = new List<string>();
        var attrs = new List<GameLogAttrFilter>();

        foreach (var (key, value) in parameters)
        {
            if (key == "stream")
            {
                if (!GameLogPolicy.IsKnownStream(value)) return Fail("INVALID_QUERY", "stream must be default, chat or audit");
                if (!policy.IsStreamEnabled(value)) return Fail("STREAM_DISABLED", $"stream '{value}' is not enabled for this project");
                if (!streams.Contains(value, StringComparer.Ordinal)) streams.Add(value);
                continue;
            }

            if (key.StartsWith("attr.", StringComparison.Ordinal))
            {
                var attrKey = key["attr.".Length..];
                if (!AttrKeyPattern.IsMatch(attrKey)) return Fail("INVALID_QUERY", "attr keys are 1-64 letters, digits or underscores");
                if (value.Length == 0 || value.Length > MaxAttrValueLength) return Fail("INVALID_QUERY", "attr values must be 1-256 characters");
                attrs.Add(new GameLogAttrFilter(attrKey, GameLogAttributes.CanonicalNumber(value) ?? value));
                continue;
            }

            if (!ScalarParameters.Contains(key)) return Fail("UNKNOWN_PARAMETER", $"unknown parameter '{key}'");
            if (scalars.ContainsKey(key)) return Fail("INVALID_QUERY", $"parameter '{key}' may appear only once");
            scalars[key] = value;
        }

        long? from = null;
        long? to = null;
        if (scalars.TryGetValue("from", out var fromText))
        {
            from = ParseUnixMs(fromText);
            if (from is null) return Fail("INVALID_QUERY", "from must be unix milliseconds or an ISO-8601 timestamp");
        }

        if (scalars.TryGetValue("to", out var toText))
        {
            to = ParseUnixMs(toText);
            if (to is null) return Fail("INVALID_QUERY", "to must be unix milliseconds or an ISO-8601 timestamp");
        }

        if (from is not null && to is not null && from >= to) return Fail("INVALID_QUERY", "from must be before to");

        string? level = null;
        if (scalars.TryGetValue("level", out var levelText))
        {
            if (!Levels.Contains(levelText)) return Fail("INVALID_QUERY", "level must be debug, info, warn or error");
            level = levelText;
        }

        string? eventName = null;
        if (scalars.TryGetValue("event", out var eventText))
        {
            if (eventText.Length > 128 || !EventPattern.IsMatch(eventText)) return Fail("INVALID_QUERY", "event is not a valid event name");
            eventName = eventText;
        }

        string? player = null;
        if (scalars.TryGetValue("player", out var playerText))
        {
            if (!isSteamId(playerText)) return Fail("INVALID_QUERY", "player must be a Steam ID");
            player = playerText;
        }

        var tokens = Array.Empty<string>();
        if (scalars.TryGetValue("q", out var queryText) && queryText.Length > 0)
        {
            var parsed = GameLogTokenizer.Tokenize(queryText);
            if (parsed.Count == 0) return Fail("INVALID_QUERY", "q contains no searchable terms");
            if (parsed.Count > GameLogTokenizer.MaxQueryTokens)
                return Fail("INVALID_QUERY", $"q may contain at most {GameLogTokenizer.MaxQueryTokens} terms");
            tokens = parsed.ToArray();
        }

        var limit = DefaultLimit;
        if (scalars.TryGetValue("limit", out var limitText))
        {
            if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLimit)
                return Fail("INVALID_QUERY", $"limit must be between 1 and {MaxLimit}");
        }

        GameLogCursor? cursor = null;
        if (scalars.TryGetValue("cursor", out var cursorText))
        {
            cursor = ParseCursor(cursorText);
            if (cursor is null) return Fail("INVALID_CURSOR", "cursor is not valid");
        }

        if (tokens.Length > 0 || attrs.Count > 0)
        {
            if (from is null || to is null) return Fail("TIME_RANGE_REQUIRED", "q and attr.* searches require from and to");
            if (to - from > MaxTextSearchSpanMs) return Fail("TIME_RANGE_TOO_LARGE", "q and attr.* searches may span at most 31 days");
        }

        var effectiveStreams = streams.Count > 0
            ? streams
            : policy.Streams.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

        // Each stream is searchable only inside its retention window, so expired rows are never returned.
        var nowMs = now.ToUnixTimeMilliseconds();
        var floors = effectiveStreams.ToDictionary(
            s => s, s => nowMs - policy.RetentionDaysFor(s) * 86_400_000L, StringComparer.Ordinal);

        var query = new GameLogQuery(
            ProjectId: projectId,
            StreamFloorsUnixMs: floors,
            FromUnixMs: from,
            ToUnixMs: to,
            Level: level,
            Event: eventName,
            PlayerSteamId: player,
            Attrs: attrs,
            SearchTokens: tokens,
            Cursor: cursor,
            Limit: limit);
        return new GameLogSearchParseResult(query, null, null);
    }

    /// <summary>Encodes a cursor as <c>{ts}.{entryId}</c>, the form the endpoint returns in <c>nextCursor</c>.</summary>
    public static string FormatCursor(GameLogCursor cursor) =>
        cursor.TsUnixMs.ToString(CultureInfo.InvariantCulture) + "." + cursor.EntryId;

    private static GameLogCursor? ParseCursor(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0) return null;
        if (!long.TryParse(text.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return null;
        var id = text[(dot + 1)..];
        return EntryIdPattern.IsMatch(id) ? new GameLogCursor(ts, id) : null;
    }

    private static long? ParseUnixMs(string text)
    {
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) return ms;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    private static GameLogSearchParseResult Fail(string code, string message) => new(null, code, message);
}
