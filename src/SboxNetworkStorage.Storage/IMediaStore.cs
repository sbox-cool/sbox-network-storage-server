namespace SboxNetworkStorage.Storage;

/// <summary>Media object metadata and upload grant records (schema v8). Blob bytes live in the media blob store.</summary>
public interface IMediaStore
{
    /// <summary>
    /// Records an issued grant unless the owner already holds <paramref name="perOwnerLimit"/> grants for this purpose
    /// issued since <paramref name="windowStartUnixMs"/> (used or not). Returns false, and records nothing, when the
    /// limit is reached. The check, the prune of grants issued before the window, and the insert run as one unit:
    /// parallel calls for the same owner and purpose cannot exceed the limit.
    /// </summary>
    Task<bool> TryInsertMediaGrantAsync(MediaGrantRecord grant, long windowStartUnixMs, int perOwnerLimit, CancellationToken ct);

    /// <summary>
    /// Marks a grant used when it is unused and unexpired. Returns false when it was already used, has expired,
    /// or does not exist, so the caller reports one "already used" outcome for all three.
    /// </summary>
    Task<bool> TryConsumeGrantAsync(string projectId, string grantId, long nowUnixMs, CancellationToken ct);

    Task InsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct);

    /// <summary>Returns the object row, or null when the id is unknown in this project.</summary>
    Task<MediaObjectRecord?> GetMediaObjectAsync(string projectId, string mediaId, CancellationToken ct);

    /// <summary>
    /// Sum of stored object sizes for one purpose in one project, counting only rows still unexpired at
    /// <paramref name="nowUnixMs"/>. Expired rows stop counting toward the quota at read time; the sweeper that
    /// deletes them has not shipped yet.
    /// </summary>
    Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, long nowUnixMs, CancellationToken ct);
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
