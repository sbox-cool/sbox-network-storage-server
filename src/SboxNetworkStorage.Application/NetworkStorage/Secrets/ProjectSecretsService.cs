using System.Text;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.Secrets;

/// <summary>
/// Seals and opens project secret values. Implemented in Infrastructure with AES-256-GCM over a key derived from
/// STORAGE_ENCRYPTION_KEY; the application layer never sees key material.
/// </summary>
public interface IProjectSecretCipher
{
    /// <summary>False when STORAGE_ENCRYPTION_KEY is not configured: sealing and opening both fail.</summary>
    bool IsAvailable { get; }

    /// <summary>Seals a plaintext bound to its project and name. Throws <see cref="ProjectSecretUnavailableException"/> without a key.</summary>
    string Seal(string projectId, string name, string plaintext);

    /// <summary>Opens a sealed value. Throws <see cref="ProjectSecretUnavailableException"/> without a key or when the value does not authenticate.</summary>
    string Open(string projectId, string name, string sealedValue);
}

/// <summary>A secret cannot be read or written because the storage key is missing or does not match the stored value.</summary>
public sealed class ProjectSecretUnavailableException(string message) : Exception(message);

/// <summary>A secret name, value or count breaks a documented rule. The message is safe to show to the owner.</summary>
public sealed class ProjectSecretValidationException(string message) : Exception(message);

public enum ProjectSecretSetOutcome
{
    Created,
    Replaced,
}

/// <summary>
/// Named per-project secrets for script endpoints (<c>ns.secrets.get</c>) and the owner panel. Values are write-only
/// from the panel's side: the only read path is <see cref="ReadAsync"/>, used by script execution.
/// </summary>
public sealed class ProjectSecretsService(IProjectSecretStore store, IProjectSecretCipher cipher, TimeProvider time)
{
    public const int MaxSecretsPerProject = 64;
    public const int MaxValueBytes = 4096;

    /// <summary>Case-sensitive; 1 to 64 characters; starts with a letter or digit. <c>\z</c>, not <c>$</c>, so a trailing newline is rejected.</summary>
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}\z", RegexOptions.CultureInvariant);

    public bool IsAvailable => cipher.IsAvailable;

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>Lists names and last-write times. Values are never returned.</summary>
    public Task<IReadOnlyList<ProjectSecretSummary>> ListAsync(string projectId, CancellationToken ct) =>
        store.ListProjectSecretsAsync(projectId, ct);

    /// <summary>Creates or replaces a secret. Enforces the name, size and per-project count rules.</summary>
    public async Task<ProjectSecretSetOutcome> SetAsync(string projectId, string name, string value, CancellationToken ct)
    {
        if (!IsValidName(name))
            throw new ProjectSecretValidationException("Secret names are 1 to 64 characters: letters, digits, '_', '.' and '-', starting with a letter or digit.");
        if (value.Length == 0)
            throw new ProjectSecretValidationException("Secret value must not be empty.");
        if (Encoding.UTF8.GetByteCount(value) > MaxValueBytes)
            throw new ProjectSecretValidationException($"Secret value must be at most {MaxValueBytes} bytes.");

        var sealedValue = cipher.Seal(projectId, name, value);
        var result = await store.SetProjectSecretAsync(projectId, name, sealedValue, time.GetUtcNow().ToUnixTimeMilliseconds(), MaxSecretsPerProject, ct);
        return result switch
        {
            ProjectSecretWriteResult.Created => ProjectSecretSetOutcome.Created,
            ProjectSecretWriteResult.Replaced => ProjectSecretSetOutcome.Replaced,
            _ => throw new ProjectSecretValidationException($"This project already has {MaxSecretsPerProject} secrets. Delete one before adding another."),
        };
    }

    /// <summary>Deletes a secret. Returns false when the name was not set.</summary>
    public Task<bool> DeleteAsync(string projectId, string name, CancellationToken ct) =>
        store.DeleteProjectSecretAsync(projectId, name, ct);

    /// <summary>
    /// Reads and opens a secret for script execution. Returns null only when the name is not set. Throws
    /// <see cref="ProjectSecretUnavailableException"/> when the value exists but cannot be opened: callers must fail
    /// closed rather than treat it as unset.
    /// </summary>
    public async Task<string?> ReadAsync(string projectId, string name, CancellationToken ct)
    {
        var sealedValue = await store.GetProjectSecretSealedAsync(projectId, name, ct);
        return sealedValue is null ? null : cipher.Open(projectId, name, sealedValue);
    }
}
