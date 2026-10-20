namespace SboxNetworkStorage.Storage;

/// <summary>Name and last-write time of a project secret. The sealed value is never listed.</summary>
public sealed record ProjectSecretSummary(string Name, long UpdatedAtUnixMs);

public enum ProjectSecretWriteResult
{
    Created,
    Replaced,
    /// <summary>The project already holds the maximum number of secrets and the name is new.</summary>
    LimitReached,
}

/// <summary>
/// Per-project named secrets (schema v11). Values are stored sealed (encrypted by the application layer);
/// this store never sees plaintext.
/// </summary>
public interface IProjectSecretStore
{
    Task<IReadOnlyList<ProjectSecretSummary>> ListProjectSecretsAsync(string projectId, CancellationToken ct);

    /// <summary>Returns the sealed value, or null when the name is not set in this project.</summary>
    Task<string?> GetProjectSecretSealedAsync(string projectId, string name, CancellationToken ct);

    /// <summary>
    /// Replaces the value of an existing name, or creates the name when the project holds fewer than
    /// <paramref name="maxSecrets"/> secrets. The count check and the write run as one unit: parallel calls
    /// cannot exceed the cap.
    /// </summary>
    Task<ProjectSecretWriteResult> SetProjectSecretAsync(string projectId, string name, string sealedValue, long updatedAtUnixMs, int maxSecrets, CancellationToken ct);

    /// <summary>Returns false when the name was not set.</summary>
    Task<bool> DeleteProjectSecretAsync(string projectId, string name, CancellationToken ct);
}
