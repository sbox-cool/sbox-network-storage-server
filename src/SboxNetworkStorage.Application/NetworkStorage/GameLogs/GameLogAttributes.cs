using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>
/// Canonical attribute JSON for game-log entries (epic #111 L1/L2). Output has sorted keys,
/// numbers in canonical decimal form, and no NUL characters, so SQLite (text) and PostgreSQL
/// (jsonb) store and read back the same values. Limits: 64 keys in total, 128-char keys, nesting
/// depth 4, strings truncated to <see cref="MaxStringLength"/> with <see cref="TruncationSuffix"/>.
/// </summary>
public static class GameLogAttributes
{
    public const int MaxKeys = 64;
    public const int MaxKeyLength = 128;
    public const int MaxDepth = 4;
    public const int MaxStringLength = 1024;
    public const string TruncationSuffix = "…[truncated]";

    /// <summary>
    /// Result of canonicalizing one attribute object. Exactly one of <see cref="Json"/> and
    /// <see cref="RejectCode"/> is set. <see cref="Truncated"/> is true when any string was cut.
    /// </summary>
    public sealed record Result(string? Json, string? RejectCode, bool Truncated);

    public static Result Canonicalize(JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object) return new Result(null, "INVALID_ATTRS", false);

        var state = new State();
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteValue(attrs, 1, writer, state);
            }
        }
        catch (ArgumentException)
        {
            return new Result(null, "INVALID_ATTRS", false);
        }

        if (state.RejectCode is not null) return new Result(null, state.RejectCode, false);
        return new Result(Encoding.UTF8.GetString(buffer.WrittenSpan), null, state.Truncated);
    }

    /// <summary>Re-emits stored attribute JSON with sorted keys. PostgreSQL jsonb reorders keys on read; this makes both providers return the same text.</summary>
    public static string Reformat(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = Canonicalize(document.RootElement);
        return result.Json ?? json;
    }

    /// <summary>
    /// Canonical decimal text for a JSON number, or null when it is outside the supported range:
    /// zero, or 0.0001 ≤ |x| &lt; 10^15 with at most 15 significant digits. The range keeps SQLite's
    /// text form identical to the stored value.
    /// </summary>
    public static string? CanonicalNumber(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble) && asDouble == 0
                ? "0"
                : null;
        }

        if (value == 0) return "0";
        var magnitude = Math.Abs(value);
        if (magnitude < 0.0001m || magnitude >= 1_000_000_000_000_000m) return null;

        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        var digits = text.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        return digits.Length > 15 ? null : text;
    }

    /// <summary>Removes NUL characters and replaces unpaired surrogates, which PostgreSQL text and jsonb reject.</summary>
    public static string Sanitize(string text)
    {
        if (text.Length == 0) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == 0) continue;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Cuts a sanitized string to fit <paramref name="max"/> UTF-16 units, appending the truncation marker.</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var keep = max - TruncationSuffix.Length;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..Math.Max(keep, 0)] + TruncationSuffix;
    }

    private sealed class State
    {
        public int Keys;
        public bool Truncated;
        public string? RejectCode;

        public void Reject(string code) => RejectCode ??= code;
    }

    private static void WriteValue(JsonElement element, int depth, Utf8JsonWriter writer, State state)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth > MaxDepth) state.Reject("ATTR_TOO_DEEP");
                var properties = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                writer.WriteStartObject();
                string? previous = null;
                foreach (var property in properties)
                {
                    var name = property.Name;
                    if (name.Contains('\0')) state.Reject("INVALID_ATTR_KEY");
                    if (name.Length > MaxKeyLength) state.Reject("ATTR_KEY_TOO_LONG");
                    if (string.Equals(name, previous, StringComparison.Ordinal)) state.Reject("DUPLICATE_ATTR_KEY");
                    previous = name;
                    if (++state.Keys > MaxKeys) state.Reject("TOO_MANY_ATTRS");
                    writer.WritePropertyName(name);
                    WriteValue(property.Value, depth + 1, writer, state);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                if (depth > MaxDepth) state.Reject("ATTR_TOO_DEEP");
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteValue(item, depth + 1, writer, state);
                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                var text = Sanitize(element.GetString() ?? "");
                if (text.Length > MaxStringLength)
                {
                    text = Truncate(text, MaxStringLength);
                    state.Truncated = true;
                }

                writer.WriteStringValue(text);
                return;

            case JsonValueKind.Number:
                var canonical = CanonicalNumber(element.GetRawText());
                if (canonical is null)
                {
                    state.Reject("ATTR_NUMBER_OUT_OF_RANGE");
                    writer.WriteNumberValue(0);
                    return;
                }

                writer.WriteRawValue(canonical);
                return;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;

            default:
                state.Reject("INVALID_ATTRS");
                return;
        }
    }
}
