namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Network Storage ingress alias. <see cref="V3"/> is the canonical contract; the rest are
/// compatibility aliases that resolve to (or sit beside) the canonical v3 families.
/// </summary>
public enum NetworkStorageAlias
{
    /// <summary>Canonical <c>/v3/...</c> contract.</summary>
    V3,

    /// <summary>ASP.NET Core ingress alias <c>/api/v3/...</c> that normalizes to canonical <c>/v3/...</c>.</summary>
    ApiV3,

    /// <summary>Deprecated <c>/v1/...</c> alias mounted on the same handlers as a subset of v3.</summary>
    V1,

    /// <summary>Legacy v0 <c>/api/storage/...</c> data-plane alias.</summary>
    ApiStorage,

    /// <summary>Bare <c>/pages/...</c> client pages alias.</summary>
    Pages,

    /// <summary><c>/api/pages/...</c> client pages alias.</summary>
    ApiPages,

    /// <summary>Localhost-only <c>/_internal/...</c> storage-api endpoints; never public ingress.</summary>
    Internal,

    /// <summary>Not a recognized Network Storage route prefix.</summary>
    Unknown
}

public static class NetworkStorageAliasExtensions
{
    /// <summary>Stable diagnostic token, kept identical to the historical classifier tokens.</summary>
    public static string ToToken(this NetworkStorageAlias alias) => alias switch
    {
        NetworkStorageAlias.V3 => "v3",
        NetworkStorageAlias.ApiV3 => "api-v3",
        NetworkStorageAlias.V1 => "v1",
        NetworkStorageAlias.ApiStorage => "api-storage",
        NetworkStorageAlias.Pages => "pages",
        NetworkStorageAlias.ApiPages => "api-pages",
        NetworkStorageAlias.Internal => "internal",
        _ => "unknown"
    };

    /// <summary>True for the public Network Storage ingress aliases (everything except internal/unknown).</summary>
    public static bool IsPublicAlias(this NetworkStorageAlias alias) => alias switch
    {
        NetworkStorageAlias.V3 => true,
        NetworkStorageAlias.ApiV3 => true,
        NetworkStorageAlias.V1 => true,
        NetworkStorageAlias.ApiStorage => true,
        NetworkStorageAlias.Pages => true,
        NetworkStorageAlias.ApiPages => true,
        _ => false
    };

    /// <summary>True for a non-canonical compatibility alias (canonical v3 is not a compatibility alias).</summary>
    public static bool IsCompatibilityAlias(this NetworkStorageAlias alias) =>
        alias.IsPublicAlias() && alias != NetworkStorageAlias.V3;
}

/// <summary>Result of normalizing a request path to its Network Storage alias + canonical path.</summary>
public sealed record NetworkStorageNormalizedRoute(
    string OriginalPath,
    string CanonicalPath,
    NetworkStorageAlias Alias)
{
    public bool IsPublicAlias => Alias.IsPublicAlias();
    public bool IsCompatibilityAlias => Alias.IsCompatibilityAlias();
}

/// <summary>
/// Canonical alias normalization for the Network Storage ingress prefixes
/// (<c>/v3</c>, <c>/api/v3</c>, <c>/v1</c>, <c>/api/storage</c>, <c>/pages</c>, <c>/api/pages</c>).
/// Only the pure ASP.NET ingress alias <c>/api/v3/...</c> is rewritten to canonical <c>/v3/...</c>;
/// the other legacy aliases keep their declared paths because the catalog holds explicit entries for them.
/// </summary>
public static class NetworkStorageAliasNormalizer
{
    public static NetworkStorageNormalizedRoute Normalize(string? rawPath)
    {
        var path = NormalizePath(rawPath);

        if (StartsWithSegment(path, "/api/v3"))
        {
            var rest = path.Substring("/api/v3".Length);
            var canonical = rest.Length == 0 ? "/v3" : "/v3" + rest;
            return new NetworkStorageNormalizedRoute(path, canonical, NetworkStorageAlias.ApiV3);
        }

        if (StartsWithSegment(path, "/v3"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.V3);
        }

        if (StartsWithSegment(path, "/v1"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.V1);
        }

        if (StartsWithSegment(path, "/api/storage"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.ApiStorage);
        }

        if (StartsWithSegment(path, "/api/pages"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.ApiPages);
        }

        if (StartsWithSegment(path, "/pages"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.Pages);
        }

        if (StartsWithSegment(path, "/_internal"))
        {
            return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.Internal);
        }

        return new NetworkStorageNormalizedRoute(path, path, NetworkStorageAlias.Unknown);
    }

    private static string NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        var path = rawPath.Trim();
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path;
    }

    private static bool StartsWithSegment(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == prefix.Length || path[prefix.Length] == '/';
    }
}
