using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage;

public interface IStorageApiKeyResolver
{
    /// <summary>
    /// Resolve an API key for a project by reading the Bunny CDN key file (public or secret).
    /// Returns null if the key is missing, disabled, or the hash/identifier doesn't match.
    /// </summary>
    Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken);
}
