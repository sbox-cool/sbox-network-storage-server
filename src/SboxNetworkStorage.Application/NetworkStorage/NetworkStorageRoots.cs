namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>Public roots. Existing paths and their meanings remain supported indefinitely.</summary>
public static class NetworkStorageRoots
{
    public const string Api = "/api";
    public const string ApiV3 = "/api/v3";
    public const string V3 = "/v3";
    public const string V1 = "/v1";
    public const string ApiSince = "0.5.5";

    public static readonly string[] DataPlanePrefixes = [V3, V1, Api, "/pages"];

    /// <summary>
    /// /api/v3 mirrors the complete /v3 surface. Other /api families also mirror /v3,
    /// except existing storage, pages and network-storage routes, whose legacy meanings stay unchanged.
    /// </summary>
    public static bool TryRewriteApiRoot(string? path, out string rewritten)
    {
        rewritten = "";
        if (HasSegmentPrefix(path, ApiV3))
        {
            rewritten = V3 + path![ApiV3.Length..];
            return true;
        }
        if (!HasSegmentPrefix(path, Api) || path!.Length <= Api.Length
            || HasSegmentPrefix(path, Api + "/storage")
            || HasSegmentPrefix(path, Api + "/pages")
            || HasSegmentPrefix(path, Api + "/network-storage")) return false;

        rewritten = V3 + path[Api.Length..];
        return true;
    }

    public static bool IsManagementPath(string? path)
        => HasSegmentPrefix(path, V3 + "/manage")
            || HasSegmentPrefix(path, Api + "/manage")
            || HasSegmentPrefix(path, ApiV3 + "/manage");

    public static bool HasSegmentPrefix(string? path, string prefix)
        => path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
