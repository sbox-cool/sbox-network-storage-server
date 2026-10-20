using System.Net;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// A project's outbound policy for <c>ns.fetch</c>. The default (no entries, allow-all off) refuses every host, so a
/// project that never configured fetch behaves as before.
/// </summary>
public sealed record EndpointFetchPolicy(IReadOnlyList<string> AllowedHosts, bool AllowAllPublic)
{
    /// <summary>Refuses every host. What a project without fetch settings gets.</summary>
    public static EndpointFetchPolicy Closed { get; } = new([], false);
}

/// <summary>Validation and matching of allow-list entries and request hosts.</summary>
public static class FetchHostRules
{
    /// <summary>Most allow-list entries one project may store.</summary>
    public const int MaxEntries = 100;

    private const string Label = "[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?";
    private static readonly Regex HostNamePattern = new($"^{Label}(?:\\.{Label})*$", RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Pattern = new(@"^\d{1,3}(?:\.\d{1,3}){3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The host an outbound request will be checked as: lower case, no trailing dot, IPv6 brackets removed, and a
    /// canonical form for IP literals.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        var text = host.Trim().ToLowerInvariant();
        if (text.Length > 1 && text[0] == '[' && text[^1] == ']')
            text = text[1..^1];
        text = text.TrimEnd('.');
        return IPAddress.TryParse(text, out var address) && IsIpLiteral(text) ? address.ToString() : text;
    }

    /// <summary>
    /// Validates one entry the owner typed: an exact host (<c>api.example.com</c>), a wildcard for subdomains
    /// (<c>*.example.com</c>, never the apex), or an IP literal. Returns the stored form.
    /// </summary>
    public static bool TryNormalizeEntry(string? raw, out string entry)
    {
        entry = string.Empty;
        var text = raw?.Trim().ToLowerInvariant().TrimEnd('.') ?? string.Empty;
        if (text.Length == 0 || text.Length > 253)
            return false;

        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = text[2..];
            if (!suffix.Contains('.') || !HostNamePattern.IsMatch(suffix))
                return false;
            entry = "*." + suffix;
            return true;
        }

        if (IsIpLiteral(text))
        {
            if (!IPAddress.TryParse(text, out var address))
                return false;
            entry = address.ToString();
            return true;
        }

        if (!HostNamePattern.IsMatch(text))
            return false;
        entry = text;
        return true;
    }

    /// <summary>
    /// Whether a normalized host is on the allow-list. <c>*.example.com</c> matches any subdomain of
    /// <c>example.com</c> and never the apex. IP literals match exactly.
    /// </summary>
    public static bool IsAllowed(string host, IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(entry[1..], StringComparison.Ordinal))
                    return true;
            }
            else if (string.Equals(host, entry, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses a normalized host that is an IP literal. DNS names return false.</summary>
    public static bool TryGetIpLiteral(string host, out IPAddress address)
    {
        if (IsIpLiteral(host) && IPAddress.TryParse(host, out var parsed))
        {
            address = parsed;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    private static bool IsIpLiteral(string text) => text.Contains(':') || Ipv4Pattern.IsMatch(text);
}
