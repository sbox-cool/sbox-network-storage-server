namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Cuts an endpoint's Discord embed text to the documented limits so an oversized value cannot make the queued
/// send fail permanently at Discord. Limits are from Discord's embed object documentation.
/// </summary>
public static class DiscordEmbedLimits
{
    public const int MaxTitle = 256;
    public const int MaxDescription = 4096;
    public const int MaxFields = 25;
    public const int MaxFieldName = 256;
    public const int MaxFieldValue = 1024;
    public const int MaxFooter = 2048;
    public const int MaxTotal = 6000;

    /// <summary>Returns a copy of <paramref name="payload"/> whose embeds fit Discord's limits. The input is not modified.</summary>
    public static Dictionary<string, object?> Apply(IReadOnlyDictionary<string, object?> payload)
    {
        var result = new Dictionary<string, object?>(payload);
        if (payload.GetValueOrDefault("embeds") is IEnumerable<object?> embeds)
        {
            var copies = new List<object?>();
            foreach (var embed in embeds)
            {
                if (embed is IReadOnlyDictionary<string, object?> dict) copies.Add(ApplyEmbed(dict));
                else copies.Add(embed);
            }
            result["embeds"] = copies;
        }
        return result;
    }

    private static Dictionary<string, object?> ApplyEmbed(IReadOnlyDictionary<string, object?> embed)
    {
        var copy = new Dictionary<string, object?>(embed);
        var title = Cut(embed.GetValueOrDefault("title") as string, MaxTitle);
        var footer = embed.GetValueOrDefault("footer") is IReadOnlyDictionary<string, object?> footerDict
            ? new Dictionary<string, object?>(footerDict) { ["text"] = Cut(footerDict.GetValueOrDefault("text") as string, MaxFooter) }
            : null;
        var fields = new List<Dictionary<string, object?>>();
        if (embed.GetValueOrDefault("fields") is IEnumerable<object?> rawFields)
        {
            foreach (var raw in rawFields)
            {
                if (fields.Count == MaxFields) break;
                if (raw is not IReadOnlyDictionary<string, object?> field) continue;
                fields.Add(new Dictionary<string, object?>(field)
                {
                    ["name"] = Cut(field.GetValueOrDefault("name") as string, MaxFieldName),
                    ["value"] = Cut(field.GetValueOrDefault("value") as string, MaxFieldValue),
                });
            }
        }
        var description = embed.GetValueOrDefault("description") as string;

        // Description is trimmed first to fit the embed total. Trailing fields are dropped only when the rest alone exceed it.
        int Budget() => MaxTotal - Length(title) - Length(footer?["text"]) - fields.Sum(f => Length(f["name"]) + Length(f["value"]));
        while (Budget() < 0 && fields.Count > 0) fields.RemoveAt(fields.Count - 1);
        description = Cut(description, Math.Min(MaxDescription, Math.Max(0, Budget())));

        copy["title"] = title;
        copy["description"] = description;
        copy["fields"] = fields;
        if (footer is not null) copy["footer"] = footer;
        return copy;
    }

    private static int Length(object? value) => value as string is { } text ? text.Length : 0;

    /// <summary>Cuts to at most <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    private static string? Cut(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;
        var length = Math.Max(0, max);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}

/// <summary>
/// Send-time check for queued webhook targets: the same Discord host and path shape the owner page and step validator accept,
/// re-checked so a row written any other way is never sent.
/// </summary>
public static class DiscordWebhookUrl
{
    public static bool IsAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.IsDefaultPort
            && uri.Host is "discord.com" or "discordapp.com"
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }
}
