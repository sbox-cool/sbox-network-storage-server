namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Network Storage route family taxonomy. Each value groups Bun storage-api / compatibility routes
/// that share a handler shape and migration risk profile.
/// </summary>
public enum NetworkStorageRouteFamily
{
    Values,
    SecurityConfig,
    AuthSession,
    Endpoint,
    Query,
    StorageRecord,
    StorageGlobal,
    StorageLedger,
    StorageRateLimits,
    Stats,
    Analytics,
    Management,
    Pages,
    SpacetimePrototype,
    Internal
}

/// <summary>Request plane classification used by strict gating and write-safety policy.</summary>
public enum NetworkStoragePlane
{
    ControlPlaneRead,
    DataPlaneRead,
    DataPlaneMutation,
    ManagementMutation,
    Auth,
    Diagnostic
}

/// <summary>How the .NET candidate treats a Bun route family during the shadow migration.</summary>
public enum NetworkStorageRouteDisposition
{
    /// <summary>In scope to mirror as a native candidate beside Bun.</summary>
    Mirror,
    /// <summary>In scope but deferred to a later migration slice.</summary>
    Deferred,
    /// <summary>Explicitly out of scope; Bun stays authoritative and no candidate is built.</summary>
    Excluded
}

/// <summary>
/// A single Bun Network Storage route declaration, mirrored into the .NET catalog. Templates use the
/// Bun ":param" segment syntax exactly as declared in storage-api/routes.js and the
/// routes/server.js compatibility router.
/// </summary>
public sealed class NetworkStorageRouteEntry
{
    private readonly string[] _segments;

    public NetworkStorageRouteEntry(
        string method,
        string template,
        NetworkStorageAlias alias,
        NetworkStorageRouteFamily family,
        NetworkStoragePlane plane,
        bool isMutating,
        NetworkStorageRouteDisposition disposition,
        string? exclusionReason)
    {
        Method = method;
        Template = template;
        Alias = alias;
        Family = family;
        Plane = plane;
        IsMutating = isMutating;
        Disposition = disposition;
        ExclusionReason = exclusionReason;
        _segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var literals = 0;
        foreach (var segment in _segments)
        {
            if (segment.Length == 0 || segment[0] != ':')
            {
                literals++;
            }
        }

        LiteralSegmentCount = literals;
    }

    public string Method { get; }
    public string Template { get; }
    public NetworkStorageAlias Alias { get; }
    public NetworkStorageRouteFamily Family { get; }
    public NetworkStoragePlane Plane { get; }
    public bool IsMutating { get; }
    public NetworkStorageRouteDisposition Disposition { get; }
    public string? ExclusionReason { get; }

    /// <summary>Number of literal (non-parameter) segments; higher wins when several templates match a path.</summary>
    public int LiteralSegmentCount { get; }

    /// <summary>Matches pre-split, query-stripped path segments against this template's segments.</summary>
    public bool MatchesPath(IReadOnlyList<string> pathSegments)
    {
        if (pathSegments.Count != _segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            var template = _segments[i];
            if (template.Length > 0 && template[0] == ':')
            {
                continue;
            }

            if (!string.Equals(template, pathSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts the named route parameters (for example <c>projectId</c>, <c>steamId</c>) from
    /// pre-split, query-stripped path segments. Returns an empty map when the segment count differs.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtractParameters(IReadOnlyList<string> pathSegments)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pathSegments.Count != _segments.Length)
        {
            return parameters;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            var template = _segments[i];
            if (template.Length > 0 && template[0] == ':')
            {
                parameters[template[1..]] = pathSegments[i];
            }
        }

        return parameters;
    }
}

/// <summary>
/// Authoritative .NET-side inventory of every in-scope Bun Network Storage route. Built from
/// storage-api/routes.js and the routes/server.js compatibility router. The fixture
/// Fixtures/network-storage-shadow/bun-route-inventory.json mirrors the Bun source and a test fails
/// when a Bun route is absent here or lacks an explicit disposition.
/// </summary>
public static class NetworkStorageRouteCatalog
{
    public static IReadOnlyList<NetworkStorageRouteEntry> Entries { get; } = new NetworkStorageRouteEntry[]
    {
        // Values (2)
        new("GET", "/v1/values/:projectId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Values, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/values/:projectId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Values, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // SecurityConfig (1)
        new("GET", "/v3/security-config/:projectId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SecurityConfig, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // AuthSession (16)
        new("POST", "/v1/auth-sessions/:projectId/create", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/auth-sessions/:projectId/reauth", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/auth-sessions/:projectId/refresh", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/auth-sessions/:projectId/revoke", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/sessions/:projectId/create", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/sessions/:projectId/reauth", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/sessions/:projectId/refresh", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/sessions/:projectId/revoke", NetworkStorageAlias.V1, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/auth-sessions/:projectId/create", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/auth-sessions/:projectId/reauth", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/auth-sessions/:projectId/refresh", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/auth-sessions/:projectId/revoke", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/sessions/:projectId/create", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/sessions/:projectId/reauth", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/sessions/:projectId/refresh", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/sessions/:projectId/revoke", NetworkStorageAlias.V3, NetworkStorageRouteFamily.AuthSession, NetworkStoragePlane.Auth, true, NetworkStorageRouteDisposition.Mirror, null),
        // Endpoint (6)
        new("POST", "/v1/endpoints/:projectId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/endpoints/:projectId/:endpointSlug", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/endpoints/:projectId/:endpointSlug", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/endpoints/:projectId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/endpoints/:projectId/:endpointSlug", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/endpoints/:projectId/:endpointSlug", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Endpoint, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        // Query (3)
        new("GET", "/api/storage/:projectId/queries/:queryId", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.Query, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/queries/:projectId/:queryId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Query, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/queries/:projectId/:queryId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Query, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // StorageRateLimits (2)
        new("GET", "/v1/storage/:projectId/rate-limits", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRateLimits, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/rate-limits", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRateLimits, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // Stats (6)
        new("GET", "/api/storage/:projectId/stats/:steamId", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/api/storage/:projectId/stats/heartbeat", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/storage/:projectId/stats/:steamId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/storage/:projectId/stats/heartbeat", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/stats/:steamId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/storage/:projectId/stats/heartbeat", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Stats, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        // Analytics (3)
        new("POST", "/api/storage/:projectId/analytics/events", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.Analytics, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/storage/:projectId/analytics/events", NetworkStorageAlias.V1, NetworkStorageRouteFamily.Analytics, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/storage/:projectId/analytics/events", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Analytics, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        // StorageLedger (2)
        new("GET", "/v1/storage/:projectId/:collectionId/:steamId/ledger", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageLedger, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/:collectionId/:steamId/ledger", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageLedger, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // StorageGlobal (6)
        new("POST", "/v1/storage/:projectId/:collectionId/append", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/storage/:projectId/:collectionId/list", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/storage/:projectId/:collectionId/record/:recordId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/storage/:projectId/:collectionId/append", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/:collectionId/list", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/:collectionId/record/:recordId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageGlobal, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // StorageRecord (21)
        new("DELETE", "/api/storage/:projectId/:collectionId/:key", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/api/storage/:projectId/:collectionId/:key", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/api/storage/:projectId/:collectionId/:key", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/api/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/api/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/api/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/api/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.ApiStorage, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v1/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v1/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v1/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v1/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/v1/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.V1, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/storage/:projectId/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/storage/:projectId/:collectionId/:steamId/records", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/v3/storage/:projectId/:collectionId/:steamId/records/:recordId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.StorageRecord, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        // Management (43)
        new("GET", "/v3/manage/:projectId/agent-manifest", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/auto-test", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/v3/manage/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/collections/:collectionId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/config", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/v3/manage/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/endpoints/:endpointId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/game-package", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/keys", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/queries", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/queries", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/queries/:queryId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/package-sync", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/run-tests", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/settings", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/settings", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/source-upgrade", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/suggest-tests", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/sync", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/sync-jobs/:jobId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/sync/preflight", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/test-endpoint", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/tests", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/tests", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/validate", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/v3/manage/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("PATCH", "/v3/manage/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("PUT", "/v3/manage/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("POST", "/v3/manage/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        new("DELETE", "/v3/manage/:projectId/workflows/:workflowId", NetworkStorageAlias.V3, NetworkStorageRouteFamily.Management, NetworkStoragePlane.ManagementMutation, true, NetworkStorageRouteDisposition.Mirror, null),
        // Pages (2)
        new("GET", "/api/pages/:projectId/:pageSlug", NetworkStorageAlias.ApiPages, NetworkStorageRouteFamily.Pages, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        new("GET", "/pages/:projectId/:pageSlug", NetworkStorageAlias.Pages, NetworkStorageRouteFamily.Pages, NetworkStoragePlane.ControlPlaneRead, false, NetworkStorageRouteDisposition.Mirror, null),
        // SpacetimePrototype (19)
        new("POST", "/v3/prototype/spacetimedb/:projectId/checkpoint", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/collections", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/endpoints", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/game-values", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/project", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/project", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/queries", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/queries", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/rate-limit-rules", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/status", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("DELETE", "/v3/prototype/spacetimedb/:projectId/storage/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/storage/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("POST", "/v3/prototype/spacetimedb/:projectId/storage/:collectionId/:key", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("GET", "/v3/prototype/spacetimedb/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneRead, false, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        new("PUT", "/v3/prototype/spacetimedb/:projectId/workflows", NetworkStorageAlias.V3, NetworkStorageRouteFamily.SpacetimePrototype, NetworkStoragePlane.DataPlaneMutation, true, NetworkStorageRouteDisposition.Excluded, "historical prototype prototype routes are explicit test-only; mirrored as test-only or documented excluded per design."),
        // Internal (4)
        new("GET", "/_internal/health", NetworkStorageAlias.Internal, NetworkStorageRouteFamily.Internal, NetworkStoragePlane.Diagnostic, false, NetworkStorageRouteDisposition.Excluded, "Localhost-only internal health/stats/ready/self-test; not public ingress."),
        new("GET", "/_internal/ready", NetworkStorageAlias.Internal, NetworkStorageRouteFamily.Internal, NetworkStoragePlane.Diagnostic, false, NetworkStorageRouteDisposition.Excluded, "Localhost-only internal health/stats/ready/self-test; not public ingress."),
        new("GET", "/_internal/self-test", NetworkStorageAlias.Internal, NetworkStorageRouteFamily.Internal, NetworkStoragePlane.Diagnostic, false, NetworkStorageRouteDisposition.Excluded, "Localhost-only internal health/stats/ready/self-test; not public ingress."),
        new("GET", "/_internal/stats", NetworkStorageAlias.Internal, NetworkStorageRouteFamily.Internal, NetworkStoragePlane.Diagnostic, false, NetworkStorageRouteDisposition.Excluded, "Localhost-only internal health/stats/ready/self-test; not public ingress."),
    };

    /// <summary>
    /// Resolves a concrete (method, canonical-path) request to the most specific catalog entry.
    /// The path must already be alias-normalized (for example "/api/v3/..." rewritten to "/v3/..."
    /// via NetworkStorageAliasNormalizer); v1/api-storage/pages aliases keep their own declared templates.
    /// </summary>
    public static bool TryMatch(string method, string canonicalPath, out NetworkStorageRouteEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(canonicalPath))
        {
            return false;
        }

        var normalizedMethod = method.Trim();
        var pathOnly = canonicalPath.Split('?', 2)[0];
        var segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);
        NetworkStorageRouteEntry? best = null;
        foreach (var candidate in Entries)
        {
            if (!string.Equals(candidate.Method, normalizedMethod, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!candidate.MatchesPath(segments))
            {
                continue;
            }

            if (best is null || candidate.LiteralSegmentCount > best.LiteralSegmentCount)
            {
                best = candidate;
            }
        }

        entry = best;
        return best is not null;
    }
}
