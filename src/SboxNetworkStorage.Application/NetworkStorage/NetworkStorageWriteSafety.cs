namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Central policy for mutating Network Storage candidate execution. Decides whether a mutating route
/// may execute as a dry-run candidate, whether production writes are allowed, and whether intended
/// write paths must be rewritten under an isolated replay prefix.
/// </summary>
public sealed record NetworkStorageWriteSafetyDecision(
    bool ExecuteCandidate,
    bool ProductionWritesAllowed,
    bool SideEffectsSuppressed,
    bool IsolatedReplayEnabled,
    string? SuppressionReason,
    string? IsolatedReplayPrefix);

public interface INetworkStorageWriteSafetyService
{
    NetworkStorageWriteSafetyDecision Evaluate(
        NetworkStorageRouteClassification route,
        NetworkStorageDispatchDecision dispatchDecision);

    IReadOnlyList<string> RewriteIntendedWritePaths(
        IReadOnlyList<string> productionPaths,
        NetworkStorageWriteSafetyDecision decision);
}

public sealed class NetworkStorageWriteSafetyService : INetworkStorageWriteSafetyService
{
    private readonly NetworkStorageShadowOptions _options;

    public NetworkStorageWriteSafetyService(NetworkStorageShadowOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public NetworkStorageWriteSafetyDecision Evaluate(
        NetworkStorageRouteClassification route,
        NetworkStorageDispatchDecision dispatchDecision)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(dispatchDecision);

        if (!route.IsMirrored)
        {
            return new NetworkStorageWriteSafetyDecision(
                ExecuteCandidate: false,
                ProductionWritesAllowed: false,
                SideEffectsSuppressed: false,
                IsolatedReplayEnabled: false,
                SuppressionReason: null,
                IsolatedReplayPrefix: null);
        }

        if (!route.IsMutating)
        {
            return new NetworkStorageWriteSafetyDecision(
                ExecuteCandidate: true,
                ProductionWritesAllowed: false,
                SideEffectsSuppressed: false,
                IsolatedReplayEnabled: false,
                SuppressionReason: null,
                IsolatedReplayPrefix: null);
        }

        if (dispatchDecision.ProductionWritesAllowed)
        {
            return new NetworkStorageWriteSafetyDecision(
                ExecuteCandidate: true,
                ProductionWritesAllowed: true,
                SideEffectsSuppressed: false,
                IsolatedReplayEnabled: false,
                SuppressionReason: null,
                IsolatedReplayPrefix: null);
        }

        if (_options.IsolatedReplayEnabled)
        {
            return new NetworkStorageWriteSafetyDecision(
                ExecuteCandidate: true,
                ProductionWritesAllowed: false,
                SideEffectsSuppressed: false,
                IsolatedReplayEnabled: true,
                SuppressionReason: null,
                IsolatedReplayPrefix: string.IsNullOrWhiteSpace(_options.IsolatedReplayPrefix)
                    ? "isolated-replay"
                    : _options.IsolatedReplayPrefix.Trim());
        }

        return new NetworkStorageWriteSafetyDecision(
            ExecuteCandidate: true,
            ProductionWritesAllowed: false,
            SideEffectsSuppressed: true,
            IsolatedReplayEnabled: false,
            SuppressionReason: "Shadow/candidate mode computes intended operations only; production writers are suppressed.",
            IsolatedReplayPrefix: null);
    }

    public IReadOnlyList<string> RewriteIntendedWritePaths(
        IReadOnlyList<string> productionPaths,
        NetworkStorageWriteSafetyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(productionPaths);
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.IsolatedReplayEnabled || string.IsNullOrWhiteSpace(decision.IsolatedReplayPrefix))
        {
            return productionPaths;
        }

        return productionPaths
            .Select(path => $"{decision.IsolatedReplayPrefix}:{path}")
            .ToArray();
    }
}
