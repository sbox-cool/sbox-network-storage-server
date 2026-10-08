namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>Where a Network Storage request should be served from.</summary>
public enum NetworkStorageDispatchTarget
{
    /// <summary>Proxy to the authoritative Bun storage-api.</summary>
    Bun,

    /// <summary>Serve from the in-process .NET native candidate.</summary>
    Candidate
}

/// <summary>
/// Resolved dispatch decision for a single request. <see cref="ProductionWritesAllowed"/> is the
/// load-bearing write-safety invariant and is only ever true for <see cref="NetworkStorageRuntimeMode.Serve"/>
/// or an allowlisted <see cref="NetworkStorageRuntimeMode.CanaryWrite"/> mutation.
/// </summary>
public sealed record NetworkStorageDispatchDecision(
    NetworkStorageRuntimeMode EffectiveMode,
    NetworkStorageDispatchTarget Target,
    bool ExecuteShadowCandidate,
    bool ProductionWritesAllowed,
    string Reason);

public interface INetworkStorageModeResolver
{
    NetworkStorageDispatchDecision Resolve(
        NetworkStorageRouteClassification classification,
        string? projectId = null,
        string? steamId = null);
}

/// <summary>
/// Resolves the effective runtime mode and dispatch target for a classified Network Storage request.
/// Pure, deterministic, and side-effect free so the safe-default invariant is unit-testable.
/// </summary>
public sealed class NetworkStorageModeResolver : INetworkStorageModeResolver
{
    private readonly NetworkStorageShadowOptions _options;
    private readonly HashSet<string> _canaryProjectIds;
    private readonly HashSet<string> _canarySteamIds;

    public NetworkStorageModeResolver(NetworkStorageShadowOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _canaryProjectIds = new HashSet<string>(options.CanaryProjectIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _canarySteamIds = new HashSet<string>(options.CanarySteamIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public NetworkStorageDispatchDecision Resolve(
        NetworkStorageRouteClassification classification,
        string? projectId = null,
        string? steamId = null)
    {
        ArgumentNullException.ThrowIfNull(classification);

        var mode = ResolveEffectiveMode(classification);

        // Routes that are not mirrored (unknown, excluded, or deferred) always stay on Bun and never
        // run a candidate or a production write, regardless of the configured mode.
        if (!classification.IsMirrored)
        {
            return Proxy(mode, "route-not-mirrored");
        }

        return mode switch
        {
            NetworkStorageRuntimeMode.ProxyOnly => Proxy(mode, "proxy-only"),
            NetworkStorageRuntimeMode.Shadow => new NetworkStorageDispatchDecision(
                mode, NetworkStorageDispatchTarget.Bun, ExecuteShadowCandidate: true, ProductionWritesAllowed: false, "shadow-diagnostics"),
            NetworkStorageRuntimeMode.Candidate => new NetworkStorageDispatchDecision(
                mode, NetworkStorageDispatchTarget.Candidate, ExecuteShadowCandidate: false, ProductionWritesAllowed: false, "candidate-serves"),
            NetworkStorageRuntimeMode.CanaryRead => ResolveCanaryRead(mode, classification, projectId, steamId),
            NetworkStorageRuntimeMode.CanaryWrite => ResolveCanaryWrite(mode, classification, projectId, steamId),
            NetworkStorageRuntimeMode.Serve => new NetworkStorageDispatchDecision(
                mode, NetworkStorageDispatchTarget.Candidate, ExecuteShadowCandidate: false, ProductionWritesAllowed: classification.IsMutating, "served"),
            _ => Proxy(NetworkStorageRuntimeMode.ProxyOnly, "unknown-mode-defaults-proxy")
        };
    }

    private NetworkStorageRuntimeMode ResolveEffectiveMode(NetworkStorageRouteClassification classification)
    {
        if (classification.Family is { } family
            && _options.RouteFamilyModes is { Count: > 0 }
            && _options.RouteFamilyModes.TryGetValue(family.ToString(), out var familyMode))
        {
            return familyMode;
        }

        return _options.Mode;
    }

    private NetworkStorageDispatchDecision ResolveCanaryRead(
        NetworkStorageRuntimeMode mode,
        NetworkStorageRouteClassification classification,
        string? projectId,
        string? steamId)
    {
        if (classification.IsMutating)
        {
            return Proxy(mode, "canary-read-skips-mutation");
        }

        if (!IsAllowlisted(projectId, steamId))
        {
            return Proxy(mode, "canary-read-not-allowlisted");
        }

        return new NetworkStorageDispatchDecision(
            mode, NetworkStorageDispatchTarget.Candidate, ExecuteShadowCandidate: false, ProductionWritesAllowed: false, "canary-read-serves");
    }

    private NetworkStorageDispatchDecision ResolveCanaryWrite(
        NetworkStorageRuntimeMode mode,
        NetworkStorageRouteClassification classification,
        string? projectId,
        string? steamId)
    {
        if (!IsAllowlisted(projectId, steamId))
        {
            return Proxy(mode, "canary-write-not-allowlisted");
        }

        return new NetworkStorageDispatchDecision(
            mode,
            NetworkStorageDispatchTarget.Candidate,
            ExecuteShadowCandidate: false,
            ProductionWritesAllowed: classification.IsMutating,
            classification.IsMutating ? "canary-write-serves-mutation" : "canary-write-serves-read");
    }

    private bool IsAllowlisted(string? projectId, string? steamId)
    {
        if (projectId is not null && _canaryProjectIds.Contains(projectId))
        {
            return true;
        }

        return steamId is not null && _canarySteamIds.Contains(steamId);
    }

    private static NetworkStorageDispatchDecision Proxy(NetworkStorageRuntimeMode mode, string reason) =>
        new(mode, NetworkStorageDispatchTarget.Bun, ExecuteShadowCandidate: false, ProductionWritesAllowed: false, reason);
}
