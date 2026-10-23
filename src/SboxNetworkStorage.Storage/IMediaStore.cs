namespace SboxNetworkStorage.Storage;

/// <summary>Media object metadata and upload grant records (schema v9). Blob bytes live in the media blob store.</summary>
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

    /// <summary>
    /// Every object row in the project, including expired rows, so a portable export keeps objects whose retention has
    /// lapsed exactly as they were. Upload grants are not listed: they are single-use operational state.
    /// </summary>
    Task<IReadOnlyList<MediaObjectRecord>> ListMediaObjectsAsync(string projectId, CancellationToken ct);

    /// <summary>
    /// Inserts the object row, or replaces every column of the existing row with the same project and media id. Used by
    /// archive import, so importing the same archive twice converges to the same rows.
    /// </summary>
    Task UpsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct);

    /// <summary>Returns the object row, or null when the id is unknown in this project.</summary>
    Task<MediaObjectRecord?> GetMediaObjectAsync(string projectId, string mediaId, CancellationToken ct);

    /// <summary>
    /// Sum of stored object sizes for one purpose in one project, counting only rows still unexpired at
    /// <paramref name="nowUnixMs"/>. Expired rows stop counting toward the quota at read time, before the retention
    /// sweeper deletes them.
    /// </summary>
    Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, long nowUnixMs, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="limit"/> object rows whose expiry is at or before <paramref name="nowUnixMs"/>, across every
    /// project, in (project, media id) order after <paramref name="after"/> (null starts at the beginning). Rows are read
    /// in pages so the retention sweeper stays bounded; pass the last row's key as the next cursor.
    /// </summary>
    Task<IReadOnlyList<MediaObjectRecord>> ListExpiredMediaObjectsAsync(long nowUnixMs, (string ProjectId, string MediaId)? after, int limit, CancellationToken ct);

    /// <summary>Every object row in the project that stores these bytes, expired rows included, with each row's expiry.</summary>
    Task<IReadOnlyList<MediaReference>> ListMediaReferencesBySha256Async(string projectId, string sha256, CancellationToken ct);

    /// <summary>
    /// Deletes the object row only while it is still expired at <paramref name="nowUnixMs"/>. Returns false when the row is
    /// missing or unexpired, so the sweeper can never remove a live object.
    /// </summary>
    Task<bool> DeleteExpiredMediaObjectAsync(string projectId, string mediaId, long nowUnixMs, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="work"/> while holding the blob lock for (project, sha256). Uploads, archive imports and
    /// sweeps all take this lock. On PostgreSQL it is a transaction advisory lock that holds across server instances, and
    /// <paramref name="work"/> runs on that transaction, so the rows it writes commit with the lock release. Inside a
    /// project import the lock lasts until the import transaction ends.
    /// </summary>
    Task RunUnderMediaBlobLockAsync(string projectId, string sha256, Func<IMediaStore, Task> work, CancellationToken ct);

    /// <summary>
    /// Serialises quota accounting for one project and purpose, across store instances. The callback uses the bound
    /// transaction: checking bytes, consuming a grant and inserting its object either commit together or roll back.
    /// Take the blob lock first when both locks are needed.
    /// </summary>
    Task RunUnderMediaQuotaLockAsync(string projectId, string purpose, Func<IMediaStore, Task> work, CancellationToken ct);

    /// <summary>
    /// Waits until no project import is running in this process, and holds that position until the returned handle is
    /// disposed. A sweep takes it before any blob lock, so it never deletes bytes whose rows have not committed yet, and
    /// a sweep waiting out an import holds no blob lock that uploads need.
    /// </summary>
    Task<IDisposable> EnterMediaSweepGateAsync(CancellationToken ct);
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

public sealed record MediaReference(string MediaId, long ExpiresAtUnixMs);
