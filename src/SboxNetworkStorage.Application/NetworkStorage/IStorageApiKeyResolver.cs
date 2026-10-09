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

/// <summary>Optional capability: drops cached key resolutions so permission,
/// enable/disable and revocation changes take effect immediately.</summary>
public interface IApiKeyCacheInvalidator
{
    void InvalidateProjectKeys(string projectId);
}

/// <summary>No-op invalidator for resolvers without a cache (tests, fakes).</summary>
public sealed class NullApiKeyCacheInvalidator : IApiKeyCacheInvalidator
{
    public static readonly NullApiKeyCacheInvalidator Instance = new();
    private NullApiKeyCacheInvalidator() { }
    public void InvalidateProjectKeys(string projectId) { }
}
