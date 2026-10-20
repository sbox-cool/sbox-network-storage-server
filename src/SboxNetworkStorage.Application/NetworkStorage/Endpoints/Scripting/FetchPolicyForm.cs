namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>Thrown when owner fetch settings are refused. Nothing is written; the owner page shows the message.</summary>
public sealed class FetchPolicyValidationException(string message) : Exception(message);

/// <summary>Owner-form parsing for the <c>ns.fetch</c> host allow-list.</summary>
public static class FetchPolicyForm
{
    /// <summary>
    /// Parses the allow-list textarea: one entry per line. Blank lines are ignored and duplicates collapse.
    /// Returns null for an empty list so the stored project carries no field.
    /// </summary>
    public static List<string>? ParseAllowList(string? text)
    {
        var entries = new List<string>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!FetchHostRules.TryNormalizeEntry(line, out var entry))
                throw new FetchPolicyValidationException($"'{line}' is not a valid host. Use api.example.com, *.example.com or an IP address.");
            if (!entries.Contains(entry, StringComparer.Ordinal))
                entries.Add(entry);
        }
        if (entries.Count > FetchHostRules.MaxEntries)
            throw new FetchPolicyValidationException($"Keep the fetch allow-list to {FetchHostRules.MaxEntries} hosts or fewer.");
        return entries.Count == 0 ? null : entries;
    }

    /// <summary>
    /// Adds one exact host from the "Allow this host" action on a blocked call. Wildcards are refused here: the action
    /// names a single host the script actually tried.
    /// </summary>
    public static List<string> AddExactHost(IReadOnlyList<string>? current, string? host)
    {
        if (!FetchHostRules.TryNormalizeEntry(host, out var entry) || entry.StartsWith("*.", StringComparison.Ordinal))
            throw new FetchPolicyValidationException("Only an exact host can be allowed from a blocked call.");
        var entries = current?.ToList() ?? [];
        if (entries.Contains(entry, StringComparer.Ordinal))
            return entries;
        if (entries.Count >= FetchHostRules.MaxEntries)
            throw new FetchPolicyValidationException($"Keep the fetch allow-list to {FetchHostRules.MaxEntries} hosts or fewer.");
        entries.Add(entry);
        return entries;
    }
}
