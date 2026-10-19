using System.Text;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// The camelCase names scripts call workflows by (<c>ns.workflows.grantXp(input)</c>). The host catalog and the
/// typings generator both use this rule, so they always agree.
/// <list type="bullet">
/// <item>Split the id on runs of non-alphanumeric characters.</item>
/// <item>The first segment starts lowercase; later segments start uppercase. The rest of each segment is kept.</item>
/// <item><c>grant_xp</c> is <c>grantXp</c>; <c>Grant_XP</c> is <c>grantXP</c>.</item>
/// <item>An alias must match <c>^[A-Za-z][A-Za-z0-9]*$</c>; otherwise the workflow has no alias.</item>
/// <item>When two ids share an alias, neither gets it. Both stay callable through <c>ns.workflows.run</c>.</item>
/// </list>
/// </summary>
public static class ScriptWorkflowAliases
{
    private static readonly Regex Separators = new("[^A-Za-z0-9]+", RegexOptions.CultureInvariant);
    private static readonly Regex ValidAlias = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);

    /// <summary>Returns alias to id, ordered by alias. Ids without a valid, unique alias are absent.</summary>
    public static Dictionary<string, string> Build(IEnumerable<string> ids)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var alias = ToAlias(id);
            if (alias is null)
                continue;
            if (!owners.TryGetValue(alias, out var list))
                owners[alias] = list = [];
            list.Add(id);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, list) in owners.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (list.Count == 1)
                result[alias] = list[0];
        }
        return result;
    }

    /// <summary>The alias for one id, or null when the id has none.</summary>
    public static string? ToAlias(string id)
    {
        var segments = Separators.Split(id).Where(segment => segment.Length > 0).ToArray();
        if (segments.Length == 0)
            return null;

        var builder = new StringBuilder();
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            builder.Append(index == 0 ? char.ToLowerInvariant(segment[0]) : char.ToUpperInvariant(segment[0]));
            builder.Append(segment, 1, segment.Length - 1);
        }

        var alias = builder.ToString();
        return ValidAlias.IsMatch(alias) ? alias : null;
    }
}
