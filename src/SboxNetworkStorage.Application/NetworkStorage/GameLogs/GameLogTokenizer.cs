using System;
using System.Collections.Generic;
using System.Text;

namespace SboxNetworkStorage.Application.NetworkStorage.GameLogs;

/// <summary>
/// Whole-token tokenizer shared by ingest (<c>search_text</c>) and search (query terms), so both
/// providers match on identical tokens. A token is a maximal run of letters or digits (Unicode
/// <c>L*</c> and <c>Nd</c>), lowercased with invariant rules; duplicates are dropped.
/// </summary>
public static class GameLogTokenizer
{
    public const int MaxQueryTokens = 8;

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune).ToString());
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            var token = current.ToString();
            current.Clear();
            if (seen.Add(token)) tokens.Add(token);
        }
    }

    /// <summary>The stored <c>search_text</c> for a message: its tokens joined by single spaces.</summary>
    public static string SearchText(string message) => string.Join(' ', Tokenize(message));
}
