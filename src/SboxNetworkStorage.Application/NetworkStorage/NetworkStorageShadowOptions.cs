namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Bound from <c>Sboxcool:NetworkStorageShadow</c>. Controls the Bun-authoritative + .NET-candidate
/// runtime modes. Defaults are deliberately conservative: global <see cref="Mode"/> is
/// <see cref="NetworkStorageRuntimeMode.ProxyOnly"/> and all allowlists are empty, so production
/// behavior stays Bun proxy-only until a route family is explicitly promoted.
/// </summary>
public sealed class NetworkStorageShadowOptions
{
    /// <summary>Global default runtime mode applied to every route family unless overridden.</summary>
    public NetworkStorageRuntimeMode Mode { get; set; } = NetworkStorageRuntimeMode.ProxyOnly;

    /// <summary>
    /// Per-family mode overrides keyed by <see cref="NetworkStorageRouteFamily"/> name
    /// (for example <c>"StorageRecord"</c>). Missing families fall back to <see cref="Mode"/>.
    /// </summary>
    public Dictionary<string, NetworkStorageRuntimeMode> RouteFamilyModes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project ids eligible for canary read/write serving. Empty means nothing is allowlisted.</summary>
    public List<string> CanaryProjectIds { get; set; } = new();

    /// <summary>Steam ids eligible for canary read/write serving. Empty means nothing is allowlisted.</summary>
    public List<string> CanarySteamIds { get; set; } = new();

    /// <summary>
    /// When true, mutating candidates may rewrite intended write paths to a synthetic isolated target
    /// instead of the production path. Never enables production writes on its own; gated by
    /// runtime mode + write-safety policy.
    /// </summary>
    public bool IsolatedReplayEnabled { get; set; }

    /// <summary>
    /// Prefix stamped onto intended write paths when <see cref="IsolatedReplayEnabled"/> is active,
    /// e.g. <c>"isolated-replay"</c> produces <c>isolated-replay:network-storage/users/…</c>.
    /// </summary>
    public string IsolatedReplayPrefix { get; set; } = "isolated-replay";
}
