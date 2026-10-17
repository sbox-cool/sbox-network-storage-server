namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// The documented Network Storage route roots. <c>/api</c> is the documented root (first shipped in
/// 0.5.5); <c>/v3</c> is its permanent alias for libraries that build URLs from the compiled-in
/// version segment, and stays supported indefinitely. The two roots serve the same handlers with
/// identical auth, limits, metering, policy, logging and error capture.
/// </summary>
public static class NetworkStorageRoots
{
    /// <summary>The documented root.</summary>
    public const string Api = "/api";

    /// <summary>The permanent alias root.</summary>
    public const string V3 = "/v3";

    /// <summary>The deprecated alias root.</summary>
    public const string V1 = "/v1";

    /// <summary>First server version documenting <c>/api</c> in server-info.</summary>
    public const string ApiSince = "0.5.5";

    /// <summary>Data-plane path prefixes for metering and rate limiting (segment-boundary match).</summary>
    public static readonly string[] DataPlanePrefixes = [V3, V1, Api, "/pages"];

    /// <summary>First path segments under <c>/api</c> that keep their own meaning and are never rewritten to <c>/v3</c>.</summary>
    private static readonly HashSet<string> ReservedApiFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "storage",
        "pages",
        "network-storage",
        "v3",
    };

    /// <summary>
    /// Rewrites a documented-root request path to its canonical alias path (<c>/api/{rest}</c> to
    /// <c>/v3/{rest}</c>). Returns false for <c>/v3</c> paths, reserved <c>/api</c> families
    /// (<c>storage</c>, <c>pages</c>, <c>network-storage</c>, <c>v3</c>), bare <c>/api</c> and
    /// anything outside both roots, which all keep today's behavior.
    /// </summary>
    public static bool TryRewriteApiRoot(string? rawPath, out string rewritten)
    {
        rewritten = "";
        if (string.IsNullOrEmpty(rawPath) || !HasSegmentPrefix(rawPath, Api) || rawPath.Length <= Api.Length)
        {
            return false;
        }

        var rest = rawPath.Substring(Api.Length);
        var family = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (ReservedApiFamilies.Contains(family))
        {
            return false;
        }

        rewritten = V3 + rest;
        return true;
    }

    /// <summary>True for secret-key management paths on either root (<c>/v3/manage/...</c>, <c>/api/manage/...</c>).</summary>
    public static bool IsManagementPath(string? path)
        => HasSegmentPrefix(path, V3 + "/manage") || HasSegmentPrefix(path, Api + "/manage");

    /// <summary>Segment-boundary prefix match: "/api/storage" does NOT match "/api/storage-browse".</summary>
    public static bool HasSegmentPrefix(string? path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == prefix.Length || path[prefix.Length] == '/';
    }
}
