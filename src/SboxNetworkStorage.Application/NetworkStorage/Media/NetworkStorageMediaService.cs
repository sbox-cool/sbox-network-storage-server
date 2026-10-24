using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.Media;

public sealed record MediaGrantRequest(string? Purpose, string? ContentType, long SizeBytes, string? Sha256, string? ForSteamId);

public sealed record IssuedMediaGrant(string GrantToken, string MediaId, string ContentType, long MaxBytes, long ExpiresAtUnixMs);

/// <summary>
/// What one retention sweep removed. <c>BlobFailures</c> lists blob deletes that failed; their expired rows stay,
/// so the next sweep retries them.
/// </summary>
public sealed record MediaSweepResult(int ObjectsDeleted, int BlobsDeleted, IReadOnlyList<Exception> BlobFailures);

/// <summary>
/// Media grant and upload rules, free of HTTP. The endpoint layer resolves identity and maps
/// <see cref="MediaFailure"/> to status codes; every check that matters lives here.
/// </summary>
public sealed class NetworkStorageMediaService
{
    private const long MillisPerHour = 3_600_000;
    private const long MillisPerDay = 86_400_000;
    private const string OctetStream = "application/octet-stream";

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex SteamIdPattern = new("^[0-9]{17}\\z", RegexOptions.CultureInvariant);

    private readonly IMediaStore _store;
    private readonly IMediaBlobStore _blobs;
    private readonly MediaGrantTokens _tokens;
    private const int BlobLockStripes = 64;
    private readonly SemaphoreSlim[] _blobLocks = Enumerable.Range(0, BlobLockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public NetworkStorageMediaService(IMediaStore store, IMediaBlobStore blobs, MediaGrantTokens tokens)
    {
        _store = store;
        _blobs = blobs;
        _tokens = tokens;
    }

    /// <summary>
    /// First step of a grant request, before any identity work: the project and purpose must be enabled,
    /// public-key callers need the per-purpose self-grant opt-in, and the declared shape must be valid.
    /// Returns the purpose policy to pass on to <see cref="IssueGrantAsync"/>.
    /// </summary>
    public MediaResult<MediaPurposePolicy> AuthorizeGrant(MediaProjectPolicy policy, MediaGrantRequest request, bool callerIsSecret)
    {
        if (!policy.Enabled)
            return MediaResult<MediaPurposePolicy>.Fail(404, "NOT_FOUND", "Not found.");
        if (!MediaPolicyParser.IsValidPurposeName(request.Purpose) || !policy.Purposes.TryGetValue(request.Purpose!, out var purpose))
            return MediaResult<MediaPurposePolicy>.Fail(400, "MEDIA_INVALID_REQUEST", "Unknown media purpose.");
        if (!purpose.Enabled)
            return MediaResult<MediaPurposePolicy>.Fail(403, "MEDIA_PURPOSE_DISABLED", "This media purpose is disabled for the project.");
        if (!callerIsSecret && !purpose.AllowClientSelfGrant)
            return MediaResult<MediaPurposePolicy>.Fail(403, "MEDIA_GRANT_FORBIDDEN",
                "Client upload grants are not enabled for this purpose. Request a grant from your server with the secret key.");
        if (request.ContentType is null || !purpose.ContentTypes.Contains(request.ContentType))
            return MediaResult<MediaPurposePolicy>.Fail(415, "MEDIA_TYPE_MISMATCH", "Content type is not allowed for this purpose.");
        if (request.SizeBytes < 1)
            return MediaResult<MediaPurposePolicy>.Fail(400, "MEDIA_INVALID_REQUEST", "sizeBytes must be at least 1.");
        if (request.SizeBytes > purpose.MaxBytes)
            return MediaResult<MediaPurposePolicy>.Fail(413, "MEDIA_TOO_LARGE", $"Object exceeds the {purpose.MaxBytes} byte limit for this purpose.");
        if (request.Sha256 is null || !Sha256Pattern.IsMatch(request.Sha256))
            return MediaResult<MediaPurposePolicy>.Fail(400, "MEDIA_INVALID_REQUEST", "sha256 must be 64 lowercase hex characters.");
        return MediaResult<MediaPurposePolicy>.Success(purpose);
    }

    /// <summary>
    /// Second step, after identity is resolved. <paramref name="ownerSteamId"/> is the verified player,
    /// or the <c>forSteamId</c> of a secret-key caller (which must be plausible).
    /// </summary>
    public async Task<MediaResult<IssuedMediaGrant>> IssueGrantAsync(
        string projectId, MediaPurposePolicy purpose, MediaGrantRequest request, string ownerSteamId, long nowUnixMs, CancellationToken ct)
    {
        if (!SteamIdPattern.IsMatch(ownerSteamId))
            return MediaResult<IssuedMediaGrant>.Fail(400, "MEDIA_INVALID_REQUEST", "forSteamId must be a 17-digit Steam ID.");

        var quotaFailure = await CheckQuotaAsync(_store, projectId, purpose, request.SizeBytes, nowUnixMs, ct);
        if (quotaFailure is not null) return MediaResult<IssuedMediaGrant>.Fail(quotaFailure.Status, quotaFailure.Code, quotaFailure.Message);

        var expiresAt = nowUnixMs + MediaGrantTokens.TtlSeconds * 1000L;
        var mediaId = MediaGrantTokens.NewId();
        var grantId = MediaGrantTokens.NewId();
        // Grants count, used or not, so minting is rate limited as well as completed uploads. The limit check and the
        // insert are one store call, so parallel requests for one player cannot overrun the limit.
        var recorded = await _store.TryInsertMediaGrantAsync(
            new MediaGrantRecord(projectId, grantId, purpose.Name, ownerSteamId, nowUnixMs, expiresAt),
            nowUnixMs - MillisPerHour, purpose.PerPlayerPerHour, ct);
        if (!recorded)
            return MediaResult<IssuedMediaGrant>.Fail(429, "MEDIA_RATE_LIMITED", "Upload limit reached for this player. Try again later.");

        var claims = new MediaGrantClaims(
            Version: 1, ProjectId: projectId, Purpose: purpose.Name, ContentType: request.ContentType!,
            MaxBytes: request.SizeBytes, Sha256: request.Sha256!, MediaId: mediaId, OwnerSteamId: ownerSteamId,
            ExpiresAtUnixMs: expiresAt, GrantId: grantId);
        return MediaResult<IssuedMediaGrant>.Success(new IssuedMediaGrant(
            _tokens.Issue(claims), mediaId, request.ContentType!, request.SizeBytes, expiresAt));
    }

    /// <summary>Verifies signature, project binding, and expiry. Does not consume the grant.</summary>
    public MediaResult<MediaGrantClaims> VerifyGrant(string projectId, string token, long nowUnixMs)
    {
        var verdict = _tokens.Verify(token, out var claims);
        if (verdict != MediaGrantVerification.Ok || claims is null || claims.ProjectId != projectId)
            return MediaResult<MediaGrantClaims>.Fail(401, "GRANT_INVALID", "Upload grant is invalid.");
        if (claims.ExpiresAtUnixMs <= nowUnixMs)
            return MediaResult<MediaGrantClaims>.Fail(410, "GRANT_EXPIRED", "Upload grant has expired.");
        return MediaResult<MediaGrantClaims>.Success(claims);
    }

    /// <summary>
    /// Validates the uploaded bytes against the grant, then consumes the grant and stores them. Every check that
    /// can reject the bytes runs before consume, so a rejected upload leaves the grant usable for a corrected retry.
    /// Consume is the atomic single-use step: a second upload on the same grant gets 409 GRANT_USED.
    /// <paramref name="declaredContentType"/> is the request's Content-Type header, if any.
    /// </summary>
    public async Task<MediaResult<MediaObjectRecord>> CompleteUploadAsync(
        MediaGrantClaims claims, MediaPurposePolicy purpose, byte[] body, string? declaredContentType, long nowUnixMs, CancellationToken ct)
    {
        if (body.LongLength > claims.MaxBytes)
            return MediaResult<MediaObjectRecord>.Fail(413, "MEDIA_TOO_LARGE", "Upload exceeds the granted size.");

        var declared = NormalizeMediaType(declaredContentType);
        if (declared is not null && declared != OctetStream && declared != claims.ContentType)
            return MediaResult<MediaObjectRecord>.Fail(415, "MEDIA_TYPE_MISMATCH", "Content-Type does not match the grant.");

        var sniffed = MediaSniffer.Sniff(body);
        if (sniffed is not { } detected || detected.ContentType != claims.ContentType)
            return MediaResult<MediaObjectRecord>.Fail(415, "MEDIA_TYPE_MISMATCH", "Body is not the granted content type.");
        if (detected.Width < 1 || detected.Height < 1)
            return MediaResult<MediaObjectRecord>.Fail(422, "MEDIA_INVALID_IMAGE", "Image dimensions could not be read.");
        if (detected.Width > purpose.MaxWidth || detected.Height > purpose.MaxHeight)
            return MediaResult<MediaObjectRecord>.Fail(422, "MEDIA_DIMENSIONS_EXCEEDED", "Image dimensions exceed the limit for this purpose.");

        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (digest != claims.Sha256)
            return MediaResult<MediaObjectRecord>.Fail(422, "MEDIA_HASH_MISMATCH", "Body sha256 does not match the grant.");

        var quotaFailure = await CheckQuotaAsync(_store, claims.ProjectId, purpose, body.LongLength, nowUnixMs, ct);
        if (quotaFailure is not null) return MediaResult<MediaObjectRecord>.Fail(quotaFailure.Status, quotaFailure.Code, quotaFailure.Message);

        var record = new MediaObjectRecord(
            ProjectId: claims.ProjectId, MediaId: claims.MediaId, Purpose: claims.Purpose, Sha256: digest,
            ContentType: claims.ContentType, SizeBytes: body.LongLength, Width: detected.Width, Height: detected.Height,
            OwnerSteamId: claims.OwnerSteamId, Visibility: purpose.Visibility, CreatedAtUnixMs: nowUnixMs,
            ExpiresAtUnixMs: nowUnixMs + purpose.RetentionDays * MillisPerDay);

        // Content-addressed and idempotent, so the bytes are written before the blob lock: no pooled connection is held
        // across blob I/O. The sweeper deletes bytes only under the lock, so the locked step re-checks and rewrites.
        if (!await _blobs.ExistsAsync(claims.ProjectId, digest, ct))
            await _blobs.WriteAsync(claims.ProjectId, digest, body, ct);

        using var _ = await LockBlobAsync(claims.ProjectId, digest, ct);
        await _store.RunUnderMediaBlobLockAsync(claims.ProjectId, digest, async store =>
        {
            if (!await _blobs.ExistsAsync(claims.ProjectId, digest, ct))
                await _blobs.WriteAsync(claims.ProjectId, digest, body, ct);
            await store.RunUnderMediaQuotaLockAsync(claims.ProjectId, purpose.Name, async quotaStore =>
            {
                quotaFailure = await CheckQuotaAsync(quotaStore, claims.ProjectId, purpose, body.LongLength, nowUnixMs, ct);
                if (quotaFailure is not null) return;
                if (!await quotaStore.TryConsumeGrantAsync(claims.ProjectId, claims.GrantId, nowUnixMs, ct))
                {
                    quotaFailure = new MediaFailure(409, "GRANT_USED", "Upload grant has already been used.");
                    return;
                }
                await quotaStore.InsertMediaObjectAsync(record, ct);
            }, ct);
            // A rejected parallel upload must not strand bytes that no committed object references.
            if (quotaFailure is not null && (await store.ListMediaReferencesBySha256Async(claims.ProjectId, digest, ct)).Count == 0)
                await _blobs.DeleteAsync(claims.ProjectId, digest, ct);
        }, ct);
        return quotaFailure is null ? MediaResult<MediaObjectRecord>.Success(record)
            : MediaResult<MediaObjectRecord>.Fail(quotaFailure.Status, quotaFailure.Code, quotaFailure.Message);
    }

    /// <summary>
    /// Deletes expired object rows, and each blob that no row (expired or not) still points at. Pages through expired
    /// rows <paramref name="batchSize"/> at a time in key order, so a blob that fails to delete keeps its expired rows
    /// and the next sweep retries it. Per (project, sha256) the blob goes first and the rows second: a crash between
    /// the two leaves an expired row with no bytes, which readers already treat as missing and the next sweep removes.
    /// The reverse order would strand a blob with no record that could ever find it.
    /// </summary>
    public async Task<MediaSweepResult> SweepExpiredAsync(long nowUnixMs, int batchSize, CancellationToken ct)
    {
        var tally = new SweepTally();
        (string ProjectId, string MediaId)? cursor = null;
        while (true)
        {
            var batch = await _store.ListExpiredMediaObjectsAsync(nowUnixMs, cursor, batchSize, ct);
            if (batch.Count == 0) break;

            foreach (var group in batch.GroupBy(r => (r.ProjectId, r.Sha256)))
                await SweepBlobAsync(group.Key.ProjectId, group.Key.Sha256, group.ToList(), nowUnixMs, tally, ct);

            var last = batch[^1];
            cursor = (last.ProjectId, last.MediaId);
            if (batch.Count < batchSize) break;
        }
        return new MediaSweepResult(tally.ObjectsDeleted, tally.BlobsDeleted, tally.BlobFailures);
    }

    private async Task SweepBlobAsync(string projectId, string sha256, List<MediaObjectRecord> expired, long nowUnixMs,
        SweepTally tally, CancellationToken ct)
    {
        // Gate before stripe: a sweep waiting out a project import must not hold a stripe that uploads need.
        using var _gate = await _store.EnterMediaSweepGateAsync(ct);
        using var _ = await LockBlobAsync(projectId, sha256, ct);
        await _store.RunUnderMediaBlobLockAsync(projectId, sha256, async store =>
        {
            // Read under the lock, after the batch was read: an upload or import that re-pointed one of these rows at
            // live bytes is seen here. The blob goes only when every row that points at it is expired and in this batch.
            var expiredIds = expired.Select(r => r.MediaId).ToHashSet(StringComparer.Ordinal);
            var references = await store.ListMediaReferencesBySha256Async(projectId, sha256, ct);
            if (references.All(r => r.ExpiresAtUnixMs <= nowUnixMs && expiredIds.Contains(r.MediaId)))
            {
                try
                {
                    await _blobs.DeleteAsync(projectId, sha256, ct);
                    tally.BlobsDeleted++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Rows stay expired, so the next sweep finds and retries this blob.
                    tally.BlobFailures.Add(ex);
                    return;
                }
            }

            foreach (var row in expired)
            {
                if (await store.DeleteExpiredMediaObjectAsync(projectId, row.MediaId, nowUnixMs, ct))
                    tally.ObjectsDeleted++;
            }
        }, ct);
    }

    /// <summary>
    /// Serializes blob writes, deletes, and the row inserts that depend on them, per (project, sha256). Stripes bound
    /// the lock table; two keys that share a stripe only wait on each other. No code path holds two stripes at once.
    /// </summary>
    private async Task<IDisposable> LockBlobAsync(string projectId, string sha256, CancellationToken ct)
    {
        var stripe = _blobLocks[(int)((uint)HashCode.Combine(projectId, sha256) % BlobLockStripes)];
        await stripe.WaitAsync(ct);
        return new StripeRelease(stripe);
    }

    private sealed class StripeRelease(SemaphoreSlim stripe) : IDisposable
    {
        public void Dispose() => stripe.Release();
    }

    private sealed class SweepTally
    {
        public int ObjectsDeleted { get; set; }
        public int BlobsDeleted { get; set; }
        public List<Exception> BlobFailures { get; } = [];
    }

    /// <summary>
    /// Metadata for one object, plus its blob when <paramref name="includeBody"/> is set. A HEAD request passes false,
    /// so the blob is checked for presence but never read. Private objects are visible only to secret-key callers.
    /// Every other miss (unknown, expired, private to a public caller, blob gone or of the wrong length) is the same
    /// 404. The caller disposes the blob it receives.
    /// </summary>
    public async Task<MediaResult<(MediaObjectRecord Record, MediaBlob? Blob)>> ReadObjectAsync(
        string projectId, string mediaId, bool callerIsSecret, bool includeBody, long nowUnixMs, CancellationToken ct)
    {
        var notFound = MediaResult<(MediaObjectRecord, MediaBlob?)>.Fail(404, "MEDIA_NOT_FOUND", "Media object not found.");
        var record = await _store.GetMediaObjectAsync(projectId, mediaId, ct);
        if (record is null || record.ExpiresAtUnixMs <= nowUnixMs) return notFound;
        if (record.Visibility != MediaPolicyParser.VisibilityPublic && !callerIsSecret) return notFound;
        if (!includeBody)
        {
            return await _blobs.ExistsAsync(projectId, record.Sha256, ct)
                ? MediaResult<(MediaObjectRecord, MediaBlob?)>.Success((record, null))
                : notFound;
        }
        var blob = await _blobs.OpenReadAsync(projectId, record.Sha256, ct);
        if (blob is null) return notFound;
        if (blob.Length != record.SizeBytes)
        {
            blob.Dispose();
            return notFound;
        }
        return MediaResult<(MediaObjectRecord, MediaBlob?)>.Success((record, blob));
    }

    private static async Task<MediaFailure?> CheckQuotaAsync(IMediaStore store, string projectId, MediaPurposePolicy purpose, long incomingBytes, long nowUnixMs, CancellationToken ct)
    {
        if (purpose.ProjectQuotaBytes <= 0) return null;
        var stored = await store.SumMediaObjectBytesAsync(projectId, purpose.Name, nowUnixMs, ct);
        return stored + incomingBytes > purpose.ProjectQuotaBytes
            ? new MediaFailure(413, "MEDIA_QUOTA_EXCEEDED", "Project media quota for this purpose is exhausted.")
            : null;
    }

    /// <summary>Lowercased media type without parameters, or null when the header is absent.</summary>
    private static string? NormalizeMediaType(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var semicolon = header.IndexOf(';');
        var type = (semicolon >= 0 ? header[..semicolon] : header).Trim().ToLowerInvariant();
        return type.Length == 0 ? null : type;
    }
}
