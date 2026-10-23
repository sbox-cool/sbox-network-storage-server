using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>A normalized entry ready for the writer, with its index in the submitted batch.</summary>
public sealed record GameLogIngestRow(int Index, GameLogRow Row);

/// <summary>An entry that was not stored, with a typed reason code.</summary>
public sealed record GameLogRejection(int Index, string Code);

/// <summary>
/// Outcome of normalizing one batch. <see cref="Sampled"/> entries were dropped by the stream's
/// sampling ratio on purpose; they are counted, not rejected.
/// </summary>
public sealed record GameLogIngestOutcome(
    IReadOnlyList<GameLogIngestRow> Rows,
    IReadOnlyList<GameLogRejection> Rejected,
    IReadOnlyList<int> Truncated,
    int Sampled);

/// <summary>Per-request inputs to <see cref="GameLogEntryNormalizer"/>.</summary>
/// <param name="Trusted">True for secret-key callers (dedicated servers). Only they may set actor, or emit events outside client.*/chat.*/allow-list.</param>
/// <param name="VerifiedActorSteamId">Default actor when the entry names none: the session-verified player for public-key callers, or the validated <c>x-on-behalf-of</c> player for trusted callers.</param>
/// <param name="NextRandom">Uniform [0,1) source for sampling. Injected for deterministic tests.</param>
public sealed record GameLogIngestContext(
    string ProjectId,
    DateTimeOffset Now,
    GameLogPolicy Policy,
    bool Trusted,
    string? VerifiedActorSteamId,
    Func<string, bool> IsSteamId,
    Func<double> NextRandom);

/// <summary>
/// Turns a submitted JSON batch into canonical <see cref="GameLogRow"/>s (epic #111 L1). Identity
/// is server-derived: public-key callers may not send <c>actor</c> at all, so a client cannot forge
/// who did something. Every rejection carries a code; nothing is dropped silently.
/// </summary>
public static class GameLogEntryNormalizer
{
    private const int MaxEventLength = 128;
    private const int MaxClientFieldLength = GameLogPolicy.MaxClientFieldLength;
    private const long ClientClockSkewMs = 5 * 60_000;
    private static readonly Regex EventPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Levels = new(StringComparer.Ordinal) { "debug", "info", "warn", "error" };

    private readonly record struct EntryResult(GameLogRow? Row, string? Reject, bool Truncated, bool Sampled);

    public static GameLogIngestOutcome Normalize(JsonElement batch, GameLogIngestContext context)
    {
        var rows = new List<GameLogIngestRow>();
        var rejected = new List<GameLogRejection>();
        var truncated = new List<int>();
        var sampled = 0;
        var index = 0;
        foreach (var entry in batch.EnumerateArray())
        {
            var result = NormalizeEntry(entry, context);
            if (result.Reject is not null)
            {
                rejected.Add(new GameLogRejection(index, result.Reject));
            }
            else if (result.Sampled)
            {
                sampled++;
            }
            else
            {
                if (result.Truncated) truncated.Add(index);
                rows.Add(new GameLogIngestRow(index, result.Row!));
            }

            index++;
        }

        return new GameLogIngestOutcome(rows, rejected, truncated, sampled);
    }

    private static EntryResult Reject(string code) => new(null, code, false, false);

    private static EntryResult NormalizeEntry(JsonElement entry, GameLogIngestContext context)
    {
        if (entry.ValueKind != JsonValueKind.Object) return Reject("INVALID_ENTRY");

        if (!TryString(entry, "event", out var eventName) || eventName.Length > MaxEventLength || !EventPattern.IsMatch(eventName))
            return Reject("INVALID_EVENT");

        var derived = GameLogPolicy.DeriveStream(eventName);
        var stream = derived;
        if (entry.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind != JsonValueKind.Null)
        {
            if (streamProp.ValueKind != JsonValueKind.String || !GameLogPolicy.IsKnownStream(streamProp.GetString()!))
                return Reject("INVALID_STREAM");
            stream = streamProp.GetString()!;
        }

        // Public keys cannot choose a stream: it follows the event, and audit is never open to them.
        if (!context.Trusted && (stream != derived || derived == "audit")) return Reject("STREAM_NOT_PERMITTED");

        if (!context.Policy.IsStreamEnabled(stream)) return Reject("STREAM_DISABLED");

        var level = "info";
        if (entry.TryGetProperty("level", out var levelProp) && levelProp.ValueKind != JsonValueKind.Null)
        {
            if (levelProp.ValueKind != JsonValueKind.String || !Levels.Contains(levelProp.GetString()!))
                return Reject("INVALID_LEVEL");
            level = levelProp.GetString()!;
        }

        var nowMs = context.Now.ToUnixTimeMilliseconds();
        var tsMs = nowMs;
        if (entry.TryGetProperty("ts", out var tsProp) && tsProp.ValueKind != JsonValueKind.Null)
        {
            var parsed = ParseTimestamp(tsProp);
            if (parsed is null) return Reject("INVALID_TIMESTAMP");
            tsMs = parsed.Value;
        }

        // Client clocks are not trusted: a public-key timestamp is clamped to receive time +/- 5 minutes and the
        // entry is reported as truncated. Secret-key timestamps keep the strict window below.
        var tsClamped = false;
        if (!context.Trusted)
        {
            var clamped = Math.Clamp(tsMs, nowMs - ClientClockSkewMs, nowMs + ClientClockSkewMs);
            tsClamped = clamped != tsMs;
            tsMs = clamped;
        }

        var retentionMs = context.Policy.RetentionDaysFor(stream) * 86_400_000L;
        if (context.Trusted && (tsMs > nowMs + GameLogPolicy.MaxFutureSkewMs || tsMs < nowMs - retentionMs))
            return Reject("TIMESTAMP_OUT_OF_WINDOW");

        if (!context.Trusted && !context.Policy.IsEventAllowedForClient(eventName)) return Reject("EVENT_NOT_ALLOWED");

        string? actor;
        var hasActorField = entry.TryGetProperty("actor", out var actorProp) && actorProp.ValueKind != JsonValueKind.Null;
        if (context.Trusted && hasActorField)
        {
            actor = ReadSteamId(actorProp);
            if (actor is null || !context.IsSteamId(actor)) return Reject("INVALID_ACTOR");
        }
        else if (context.Trusted)
        {
            actor = context.VerifiedActorSteamId;
        }
        else
        {
            if (hasActorField) return Reject("ACTOR_NOT_PERMITTED");
            actor = context.VerifiedActorSteamId;
        }

        string? target = null;
        if (entry.TryGetProperty("target", out var targetProp) && targetProp.ValueKind != JsonValueKind.Null)
        {
            target = ReadSteamId(targetProp);
            if (target is null || !context.IsSteamId(target)) return Reject("INVALID_TARGET");
        }

        var truncated = tsClamped;
        var message = "";
        if (entry.TryGetProperty("message", out var messageProp) && messageProp.ValueKind != JsonValueKind.Null)
        {
            if (messageProp.ValueKind != JsonValueKind.String) return Reject("INVALID_MESSAGE");
            message = GameLogAttributes.Sanitize(messageProp.GetString()!);
            if (message.Length > GameLogPolicy.MaxMessageLength)
            {
                message = GameLogAttributes.Truncate(message, GameLogPolicy.MaxMessageLength);
                truncated = true;
            }
        }

        var attrsJson = "{}";
        if (entry.TryGetProperty("attrs", out var attrsProp) && attrsProp.ValueKind != JsonValueKind.Null)
        {
            var canonical = GameLogAttributes.Canonicalize(attrsProp);
            if (canonical.RejectCode is not null) return Reject(canonical.RejectCode);
            attrsJson = canonical.Json!;
            truncated |= canonical.Truncated;
        }

        long? revisionId = null;
        if (entry.TryGetProperty("revisionId", out var revisionProp) && revisionProp.ValueKind != JsonValueKind.Null)
        {
            if (!revisionProp.TryGetInt64(out var revision) || revision < 0) return Reject("INVALID_REVISION");
            revisionId = revision;
        }

        if (!TryOptionalString(entry, "clientType", MaxClientFieldLength, out var clientType)
            || !TryOptionalString(entry, "libraryVersion", MaxClientFieldLength, out var libraryVersion)
            || !TryOptionalString(entry, "traceId", GameLogPolicy.MaxTraceIdLength, out var traceId))
            return Reject("INVALID_FIELD");

        var size = Utf8Length(eventName) + Utf8Length(message) + Utf8Length(attrsJson)
                   + Utf8Length(clientType) + Utf8Length(libraryVersion) + Utf8Length(traceId)
                   + Utf8Length(actor) + Utf8Length(target);
        if (size > GameLogPolicy.MaxEntryBytes) return Reject("ENTRY_TOO_LARGE");

        var sampling = context.Policy.Streams[stream].Sampling;
        if (sampling < 1.0 && context.NextRandom() >= sampling) return new EntryResult(null, null, truncated, true);

        var row = new GameLogRow(
            ProjectId: context.ProjectId,
            EntryId: Guid.CreateVersion7(context.Now).ToString("N"),
            TsUnixMs: tsMs,
            Stream: stream,
            Level: level,
            Event: eventName,
            ActorSteamId: actor,
            TargetSteamId: target,
            RevisionId: revisionId,
            ClientType: clientType,
            LibraryVersion: libraryVersion,
            Message: message,
            AttrsJson: attrsJson,
            TraceId: traceId,
            SearchText: GameLogTokenizer.SearchText(message),
            Source: context.Trusted ? "server" : "client");
        return new EntryResult(row, null, truncated, false);
    }

    private static bool TryString(JsonElement entry, string name, out string value)
    {
        value = "";
        if (!entry.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString() ?? "";
        return value.Length > 0;
    }

    /// <summary>Optional string field: absent or null is fine; wrong type, empty or over-long is invalid (never silently truncated).</summary>
    private static bool TryOptionalString(JsonElement entry, string name, int max, out string? value)
    {
        value = null;
        if (!entry.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null) return true;
        if (prop.ValueKind != JsonValueKind.String) return false;
        var text = GameLogAttributes.Sanitize(prop.GetString() ?? "");
        if (text.Length == 0 || text.Length > max) return false;
        value = text;
        return true;
    }

    private static long? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out var ms) ? ms : null;
        if (value.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    /// <summary>A Steam ID given as a string or as <c>{"steamId": "..."}</c>; null when neither shape matches.</summary>
    private static string? ReadSteamId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("steamId", out var id)
            && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        return null;
    }

    private static int Utf8Length(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
}
