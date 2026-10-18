namespace SboxNetworkStorage.Storage;

/// <summary>Media object metadata and upload grant records (schema v8). Blob bytes live in the media blob store.</summary>
public interface IMediaStore
{
    /// <summary>
    /// Records an issued grant. Grant rows issued before <paramref name="pruneIssuedBeforeUnixMs"/> are deleted in the
    /// same call; the caller passes a cutoff no later than its rate-limit window start, so pruned rows are never counted.
    /// </summary>
    Task InsertMediaGrantAsync(MediaGrantRecord grant, long pruneIssuedBeforeUnixMs, CancellationToken ct);

    /// <summary>
    /// Marks a grant used when it is unused and unexpired. Returns false when it was already used, has expired,
    /// or does not exist, so the caller reports one "already used" outcome for all three.
    /// </summary>
    Task<bool> TryConsumeGrantAsync(string projectId, string grantId, long nowUnixMs, CancellationToken ct);

    /// <summary>Grants issued to one owner for one purpose since <paramref name="sinceUnixMs"/>, used or not.</summary>
    Task<long> CountMediaGrantsSinceAsync(string projectId, string ownerSteamId, string purpose, long sinceUnixMs, CancellationToken ct);

    Task InsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct);

    /// <summary>Returns the object row, or null when the id is unknown in this project.</summary>
    Task<MediaObjectRecord?> GetMediaObjectAsync(string projectId, string mediaId, CancellationToken ct);

    /// <summary>Sum of stored object sizes for one purpose in one project (unexpired and expired rows alike, until the sweeper ships).</summary>
    Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, CancellationToken ct);
}

public sealed record MediaGrantRecord(
    string ProjectId,
    string GrantId,
    string Purpose,
    string OwnerSteamId,
    long IssuedAtUnixMs,
    long ExpiresAtUnixMs);

public sealed record MediaObjectRecord(
    string ProjectId,
    string MediaId,
    string Purpose,
    string Sha256,
    string ContentType,
    long SizeBytes,
    int Width,
    int Height,
    string OwnerSteamId,
    string Visibility,
    long CreatedAtUnixMs,
    long ExpiresAtUnixMs);
