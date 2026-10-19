namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Optional capability: counts a collection's live rows without reading them. A scan over
/// <see cref="EndpointExecutor"/>'s row limit is refused from the count, before its rows are materialised.
/// A failed count must return 0, so the scan that follows still runs and reports its own error as before.
/// </summary>
public interface IEndpointScanCounter
{
    Task<long> CountCollectionAsync(string projectId, string collectionId, CancellationToken ct);
}
