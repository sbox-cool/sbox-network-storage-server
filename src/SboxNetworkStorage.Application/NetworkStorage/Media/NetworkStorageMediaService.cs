using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.Media;

public sealed record MediaGrantRequest(string? Purpose, string? ContentType, long SizeBytes, string? Sha256, string? ForSteamId);

public sealed record IssuedMediaGrant(string GrantToken, string MediaId, string ContentType, long MaxBytes, long ExpiresAtUnixMs);

/// <summary>
/// Media grant and upload rules, free of HTTP. The endpoint layer resolves identity and maps
/// <see cref="MediaFailure"/> to status codes; every check that matters lives here.
/// </summary>
public sealed class NetworkStorageMediaService
{
    private const long MillisPerHour = 3_600_000;
    private const long MillisPerDay = 86_400_000;
    private const string OctetStream = "application/octet-stream";

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SteamIdPattern = new("^[0-9]{17}$", RegexOptions.CultureInvariant);

    private readonly IMediaStore _store;
    private readonly IMediaBlobStore _blobs;
    private readonly MediaGrantTokens _tokens;

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

        var quotaFailure = await CheckQuotaAsync(projectId, purpose, request.SizeBytes, nowUnixMs, ct);
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

        var quotaFailure = await CheckQuotaAsync(claims.ProjectId, purpose, body.LongLength, nowUnixMs, ct);
        if (quotaFailure is not null) return MediaResult<MediaObjectRecord>.Fail(quotaFailure.Status, quotaFailure.Code, quotaFailure.Message);

        if (!await _store.TryConsumeGrantAsync(claims.ProjectId, claims.GrantId, nowUnixMs, ct))
            return MediaResult<MediaObjectRecord>.Fail(409, "GRANT_USED", "Upload grant has already been used.");

        if (!await _blobs.ExistsAsync(claims.ProjectId, digest, ct))
            await _blobs.WriteAsync(claims.ProjectId, digest, body, ct);

        var record = new MediaObjectRecord(
            ProjectId: claims.ProjectId, MediaId: claims.MediaId, Purpose: claims.Purpose, Sha256: digest,
            ContentType: claims.ContentType, SizeBytes: body.LongLength, Width: detected.Width, Height: detected.Height,
            OwnerSteamId: claims.OwnerSteamId, Visibility: purpose.Visibility, CreatedAtUnixMs: nowUnixMs,
            ExpiresAtUnixMs: nowUnixMs + purpose.RetentionDays * MillisPerDay);
        await _store.InsertMediaObjectAsync(record, ct);
        return MediaResult<MediaObjectRecord>.Success(record);
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

    private async Task<MediaFailure?> CheckQuotaAsync(string projectId, MediaPurposePolicy purpose, long incomingBytes, long nowUnixMs, CancellationToken ct)
    {
        if (purpose.ProjectQuotaBytes <= 0) return null;
        var stored = await _store.SumMediaObjectBytesAsync(projectId, purpose.Name, nowUnixMs, ct);
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
