namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Provides the values context for Network Storage query execution — the
/// flattened game-values + collection constants/tables that query expressions
/// resolve against. Mirrors the legacy server getQueryValuesContext + flattenGameValues path.
/// </summary>
public interface IQueryValuesContextProvider
{
    /// <summary>
    /// Build the values context for a project. Returns a nested dictionary
    /// keyed by group/table id (e.g. combat.xp_per_kill, mining_nodes[0].ore_type).
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(string projectId, CancellationToken cancellationToken);
}
