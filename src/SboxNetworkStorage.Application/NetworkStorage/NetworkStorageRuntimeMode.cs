namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Runtime mode for a Network Storage route family during the Bun-authoritative + .NET-candidate
/// migration. Modes form a promotion state machine, not independent toggles: writes to production
/// stores are only ever permitted in <see cref="Serve"/> or an allowlisted <see cref="CanaryWrite"/>.
/// </summary>
public enum NetworkStorageRuntimeMode
{
    /// <summary>Bun receives and serves the request; the .NET candidate is not executed. Safe default.</summary>
    ProxyOnly,

    /// <summary>Bun serves; .NET executes candidate diagnostics for comparison without owning the response.</summary>
    Shadow,

    /// <summary>.NET serves only non-production / staging candidate traffic.</summary>
    Candidate,

    /// <summary>.NET may serve explicitly allowlisted read-only route/project/Steam ID traffic.</summary>
    CanaryRead,

    /// <summary>.NET may serve explicitly allowlisted mutations only when write gates are approved.</summary>
    CanaryWrite,

    /// <summary>.NET serves the route family after promotion.</summary>
    Serve
}
