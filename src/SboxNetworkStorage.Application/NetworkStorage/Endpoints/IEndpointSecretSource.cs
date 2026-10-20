namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Optional capability of an endpoint data source: reads project secrets for <c>ns.secrets.get</c>. A source without it
/// fails a script that reads a secret closed rather than returning null.
/// </summary>
public interface IEndpointSecretSource
{
    /// <summary>Returns the secret's value, or null when the name is not set. Throws when the stored value cannot be opened.</summary>
    Task<string?> ReadProjectSecretAsync(string projectId, string name, CancellationToken ct);
}
