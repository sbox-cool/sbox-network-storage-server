using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// The project's custom analytics event allowlist (<c>analytics.allowedEventTypes</c>). The game
/// library sends a custom event only when its normalized name is on the list from the signed
/// security config, so the server publishes this list and accepts owner input only in the form the
/// library compares. The defaults match the hosted reference.
/// </summary>
public static class PlayerAnalyticsAllowedEvents
{
    public const int MaxNames = 100;
    public const int MaxNameLength = 48;

    public static readonly IReadOnlyList<string> Defaults = ["death", "respawn", "cast_line", "caught_fish", "sold_fish"];

    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9_.:-]{0,47}\\z", RegexOptions.CultureInvariant);

    /// <summary>Names from the stored analytics object; <see cref="Defaults"/> when the project has no list.</summary>
    public static IReadOnlyList<string> ReadStored(JsonNode? analytics)
    {
        if (analytics is not JsonObject obj || obj["allowedEventTypes"] is not JsonArray entries) return Defaults;

        var names = new List<string>();
        foreach (var entry in entries)
        {
            if (names.Count == MaxNames) break;
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            var name = Normalize(text);
            if (IsValid(name) && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Parses the owner's textarea (one name per line or comma-separated). Returns null when a name is
    /// not valid or there are more than <see cref="MaxNames"/> distinct names. Names are not rewritten:
    /// a name the library would change (for example one containing a space) is refused.
    /// </summary>
    public static IReadOnlyList<string>? ParseForm(string? raw)
    {
        var names = new List<string>();
        foreach (var part in (raw ?? string.Empty).Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (!IsValid(name)) return null;
            if (!names.Contains(name)) names.Add(name);
        }
        return names.Count <= MaxNames ? names : null;
    }

    /// <summary>Mirrors the game library's event-name normalization: lowercase, spaces become '_', other characters are dropped.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':') builder.Append(c);
            else if (char.IsWhiteSpace(c)) builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }

    private static bool IsValid(string name) => ValidName.IsMatch(name);
}
