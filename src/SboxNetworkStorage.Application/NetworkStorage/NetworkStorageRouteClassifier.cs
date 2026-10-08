namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>Combined alias + catalog classification of a single Network Storage request.</summary>
public sealed record NetworkStorageRouteClassification(
    string Method,
    string OriginalPath,
    string CanonicalPath,
    NetworkStorageAlias Alias,
    NetworkStorageRouteEntry? Entry)
{
    /// <summary>True when the path resolves to a known cataloged Bun route.</summary>
    public bool IsKnownRoute => Entry is not null;

    /// <summary>True for a public Network Storage ingress alias (excludes internal/unknown).</summary>
    public bool IsPublicAlias => Alias.IsPublicAlias();

    /// <summary>True for a non-canonical compatibility alias (canonical v3 is not one).</summary>
    public bool IsCompatibilityAlias => Alias.IsCompatibilityAlias();

    public NetworkStorageRouteFamily? Family => Entry?.Family;

    public NetworkStoragePlane? Plane => Entry?.Plane;

    public NetworkStorageRouteDisposition? Disposition => Entry?.Disposition;

    /// <summary>True only when a cataloged mutating route matched; unknown routes are never treated as mutating.</summary>
    public bool IsMutating => Entry?.IsMutating ?? false;

    /// <summary>True when the matched route is in scope to be mirrored as a native candidate.</summary>
    public bool IsMirrored => Entry is { Disposition: NetworkStorageRouteDisposition.Mirror };

    /// <summary>Named route parameters (for example <c>projectId</c>, <c>steamId</c>) for the matched route.</summary>
    public IReadOnlyDictionary<string, string> RouteParameters { get; } = Entry is null
        ? EmptyParameters
        : Entry.ExtractParameters(CanonicalPath.Split('?', 2)[0].Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The <c>projectId</c> route parameter, when present.</summary>
    public string? ProjectId => RouteParameters.TryGetValue("projectId", out var value) ? value : null;

    /// <summary>The <c>steamId</c> route parameter, when present.</summary>
    public string? SteamId => RouteParameters.TryGetValue("steamId", out var value) ? value : null;

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Classifies a Network Storage request by normalizing its alias and matching it against the
/// <see cref="NetworkStorageRouteCatalog"/>.
/// </summary>
public static class NetworkStorageRouteClassifier
{
    public static NetworkStorageRouteClassification Classify(string method, string? path)
    {
        var normalizedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var normalized = NetworkStorageAliasNormalizer.Normalize(path);
        NetworkStorageRouteCatalog.TryMatch(normalizedMethod, normalized.CanonicalPath, out var entry);
        return new NetworkStorageRouteClassification(
            normalizedMethod,
            normalized.OriginalPath,
            normalized.CanonicalPath,
            normalized.Alias,
            entry);
    }
}
