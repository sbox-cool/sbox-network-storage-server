using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Media;

public sealed class MediaServiceTests
{
    private const string ProjectId = "proj_example";
    private const string Owner = "76561198000000001";
    private const string Secret = "media-test-secret-0123456789abcdef";
    private const long Now = 1_700_000_000_000;

    private static readonly string PublicPolicyJson = """
        {"enabled":true,"purposes":{"avatar":{"enabled":true,"allowClientSelfGrant":true,"visibility":"public",
        "contentTypes":["image/png","image/jpeg"],"maxBytes":1048576,"maxWidth":4096,"maxHeight":4096,
        "perPlayerPerHour":30,"projectQuotaBytes":0,"retentionDays":30}}}
        """;

    private static readonly string NoSelfGrantJson = """
        {"enabled":true,"purposes":{"avatar":{"enabled":true,"allowClientSelfGrant":false,"visibility":"public",
        "contentTypes":["image/png"],"maxBytes":1048576,"maxWidth":4096,"maxHeight":4096,
        "perPlayerPerHour":30,"projectQuotaBytes":0,"retentionDays":30}}}
        """;

    private static MediaProjectPolicy Policy(string json) =>
        MediaPolicyParser.Parse(JsonDocument.Parse(json).RootElement.Clone(), 10 * 1024 * 1024);

    private static MediaGrantRequest Request(byte[] body, string contentType = "image/png", string? sha = null) =>
        new("avatar", contentType, body.LongLength, sha ?? Hex(SHA256.HashData(body)), null);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Minimal PNG: signature plus IHDR. The sniffer reads only the header, not CRCs.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13;
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(width)).CopyTo(bytes, 16);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(height)).CopyTo(bytes, 20);
        return bytes;
    }

    /// <summary>Minimal JPEG: SOI then a baseline SOF0 header carrying the dimensions.</summary>
    private static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
    ];

    private static (NetworkStorageMediaService Service, FakeMediaStore Store, FakeBlobStore Blobs, MediaGrantTokens Tokens)
        Build(string? secret = Secret)
    {
        var tokens = new MediaGrantTokens(new FixedSecret(secret ?? Secret));
        var store = new FakeMediaStore();
        var blobs = new FakeBlobStore();
        return (new NetworkStorageMediaService(store, blobs, tokens), store, blobs, tokens);
    }

    [Fact]
    public void Default_policy_is_disabled_and_answers_not_found()
    {
        var (service, _, _, _) = Build();
        var result = service.AuthorizeGrant(MediaPolicyParser.Parse(null, 1024), Request(Png(4, 4)), callerIsSecret: true);
        Assert.False(result.Ok);
        Assert.Equal(404, result.Failure!.Status);
    }

    [Fact]
    public void Public_key_caller_needs_the_per_purpose_self_grant_opt_in()
    {
        var (service, _, _, _) = Build();
        var denied = service.AuthorizeGrant(Policy(NoSelfGrantJson), Request(Png(4, 4)), callerIsSecret: false);
        Assert.Equal("MEDIA_GRANT_FORBIDDEN", denied.Failure!.Code);
        Assert.Equal(403, denied.Failure.Status);

        var secretAllowed = service.AuthorizeGrant(Policy(NoSelfGrantJson), Request(Png(4, 4)), callerIsSecret: true);
        Assert.True(secretAllowed.Ok);
    }

    [Fact]
    public async Task Grant_upload_then_second_put_is_409_GRANT_USED()
    {
        var (service, store, blobs, _) = Build();
        var body = Png(64, 32);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;

        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now + 1).Value!;
        var uploaded = await service.CompleteUploadAsync(claims, purpose, body, "image/png", Now + 2, default);
        Assert.True(uploaded.Ok);
        Assert.Equal(64, uploaded.Value!.Width);
        Assert.Equal(32, uploaded.Value.Height);
        Assert.Equal(Owner, uploaded.Value.OwnerSteamId);
        Assert.Single(store.Objects);
        Assert.Single(blobs.Items);

        var replay = await service.CompleteUploadAsync(claims, purpose, body, "image/png", Now + 3, default);
        Assert.Equal("GRANT_USED", replay.Failure!.Code);
        Assert.Equal(409, replay.Failure.Status);
    }

    [Fact]
    public async Task Rejected_upload_does_not_burn_the_grant()
    {
        var (service, store, _, _) = Build();
        var body = Png(8, 8);
        var purpose = service.AuthorizeGrant(Policy(PublicPolicyJson), Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var wrongType = await service.CompleteUploadAsync(claims, purpose, body, "image/jpeg", Now, default);
        Assert.Equal("MEDIA_TYPE_MISMATCH", wrongType.Failure!.Code);

        var uploaded = await service.CompleteUploadAsync(claims, purpose, body, "image/png", Now, default);
        Assert.True(uploaded.Ok);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task Jpeg_upload_with_matching_grant_is_accepted()
    {
        var (service, _, _, _) = Build();
        var body = Jpeg(120, 80);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body, "image/jpeg"), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body, "image/jpeg"), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var uploaded = await service.CompleteUploadAsync(claims, purpose, body, "image/jpeg", Now, default);
        Assert.True(uploaded.Ok);
        Assert.Equal("image/jpeg", uploaded.Value!.ContentType);
        Assert.Equal(120, uploaded.Value.Width);
    }

    [Fact]
    public async Task Body_is_checked_by_magic_bytes_not_only_the_declared_type()
    {
        var (service, _, _, _) = Build();
        var png = Png(8, 8);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(png, "image/jpeg", Hex(SHA256.HashData(png))), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(png, "image/jpeg", Hex(SHA256.HashData(png))), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var result = await service.CompleteUploadAsync(claims, purpose, png, "image/jpeg", Now, default);
        Assert.Equal("MEDIA_TYPE_MISMATCH", result.Failure!.Code);
        Assert.Equal(415, result.Failure.Status);
    }

    [Fact]
    public async Task Wrong_sha256_is_a_typed_hash_mismatch()
    {
        var (service, store, _, _) = Build();
        var body = Png(16, 16);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var wrongSha = new string('0', 64);
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body, sha: wrongSha), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var result = await service.CompleteUploadAsync(claims, purpose, body, "image/png", Now, default);
        Assert.Equal("MEDIA_HASH_MISMATCH", result.Failure!.Code);
        Assert.Equal(422, result.Failure.Status);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Body_larger_than_the_granted_size_is_413()
    {
        var (service, _, _, _) = Build();
        var small = Png(4, 4);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(small), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(small), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var oversized = Png(4, 4).Concat(new byte[100]).ToArray();
        var result = await service.CompleteUploadAsync(claims, purpose, oversized, "image/png", Now, default);
        Assert.Equal("MEDIA_TOO_LARGE", result.Failure!.Code);
        Assert.Equal(413, result.Failure.Status);
    }

    [Fact]
    public async Task Dimensions_over_the_purpose_limit_are_422()
    {
        var (service, _, _, _) = Build();
        var body = Png(5000, 10);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now).Value!;

        var result = await service.CompleteUploadAsync(claims, purpose, body, "image/png", Now, default);
        Assert.Equal("MEDIA_DIMENSIONS_EXCEEDED", result.Failure!.Code);
        Assert.Equal(422, result.Failure.Status);
    }

    [Fact]
    public async Task Expired_grant_is_410()
    {
        var (service, _, _, _) = Build();
        var body = Png(4, 4);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;

        var atExpiry = service.VerifyGrant(ProjectId, issued.GrantToken, issued.ExpiresAtUnixMs);
        Assert.Equal("GRANT_EXPIRED", atExpiry.Failure!.Code);
        Assert.Equal(410, atExpiry.Failure.Status);
    }

    [Fact]
    public async Task Tampered_token_or_wrong_project_is_401()
    {
        var (service, _, _, _) = Build();
        var body = Png(4, 4);
        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;

        var token = issued.GrantToken;
        var i = token.IndexOf('.') + 5;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];
        Assert.Equal("GRANT_INVALID", service.VerifyGrant(ProjectId, tampered, Now).Failure!.Code);
        Assert.Equal("GRANT_INVALID", service.VerifyGrant("proj_other", token, Now).Failure!.Code);
    }

    [Fact]
    public async Task Token_signed_with_another_secret_is_401()
    {
        var (issuer, _, _, _) = Build(Secret);
        var (verifier, _, _, _) = Build("a-different-secret-0123456789abcdef");
        var body = Png(4, 4);
        var purpose = issuer.AuthorizeGrant(Policy(PublicPolicyJson), Request(body), callerIsSecret: false).Value!;
        var issued = (await issuer.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default)).Value!;
        Assert.Equal("GRANT_INVALID", verifier.VerifyGrant(ProjectId, issued.GrantToken, Now).Failure!.Code);
    }

    [Fact]
    public async Task Project_quota_rejects_the_grant_before_it_is_issued()
    {
        var (service, store, _, _) = Build();
        var policyJson = PublicPolicyJson.Replace("\"projectQuotaBytes\":0", "\"projectQuotaBytes\":100");
        store.Objects.Add(new MediaObjectRecord(ProjectId, "med_old", "avatar", new string('b', 64), "image/png", 90, 1, 1,
            Owner, "public", Now - 1000, Now + 1000));
        var body = Png(4, 4);
        var purpose = service.AuthorizeGrant(Policy(policyJson), Request(body), callerIsSecret: false).Value!;

        var result = await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default);
        Assert.Equal("MEDIA_QUOTA_EXCEEDED", result.Failure!.Code);
        Assert.Equal(413, result.Failure.Status);
    }

    [Fact]
    public async Task Per_player_hourly_limit_is_429()
    {
        var (service, store, _, _) = Build();
        var policyJson = PublicPolicyJson.Replace("\"perPlayerPerHour\":30", "\"perPlayerPerHour\":1");
        store.Grants.Add(new MediaGrantRecord(ProjectId, "grant_recent", "avatar", Owner, Now - 1000, Now + 1000));
        var body = Png(4, 4);
        var purpose = service.AuthorizeGrant(Policy(policyJson), Request(body), callerIsSecret: false).Value!;

        var result = await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now, default);
        Assert.Equal("MEDIA_RATE_LIMITED", result.Failure!.Code);
        Assert.Equal(429, result.Failure.Status);
    }

    [Fact]
    public async Task Private_objects_are_hidden_from_public_callers_and_expired_objects_are_gone()
    {
        var (service, store, blobs, _) = Build();
        var bytes = Png(4, 4);
        var sha = Hex(SHA256.HashData(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(new MediaObjectRecord(ProjectId, "med_private", "avatar", sha, "image/png", bytes.Length, 4, 4,
            Owner, "private", Now, Now + 1000));
        store.Objects.Add(new MediaObjectRecord(ProjectId, "med_public", "avatar", sha, "image/png", bytes.Length, 4, 4,
            Owner, "public", Now, Now + 1000));

        Assert.Equal(404, (await service.ReadObjectAsync(ProjectId, "med_private", false, true, Now, default)).Failure!.Status);
        Assert.True((await service.ReadObjectAsync(ProjectId, "med_private", true, true, Now, default)).Ok);
        Assert.True((await service.ReadObjectAsync(ProjectId, "med_public", false, true, Now, default)).Ok);
        Assert.Equal(404, (await service.ReadObjectAsync(ProjectId, "med_public", false, true, Now + 1000, default)).Failure!.Status);
    }

    [Fact]
    public async Task Head_reads_metadata_without_opening_the_blob()
    {
        var (service, store, blobs, _) = Build();
        var bytes = Png(4, 4);
        var sha = Hex(SHA256.HashData(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(new MediaObjectRecord(ProjectId, "med_public", "avatar", sha, "image/png", bytes.Length, 4, 4,
            Owner, "public", Now, Now + 1000));

        var head = await service.ReadObjectAsync(ProjectId, "med_public", false, false, Now, default);
        Assert.True(head.Ok);
        Assert.Null(head.Value!.Blob);
        Assert.Equal(bytes.Length, head.Value.Record.SizeBytes);
        Assert.Equal(0, blobs.OpenReads);
    }

    [Fact]
    public async Task Read_refuses_a_blob_whose_length_disagrees_with_the_record()
    {
        var (service, store, blobs, _) = Build();
        var bytes = Png(4, 4);
        var sha = Hex(SHA256.HashData(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(new MediaObjectRecord(ProjectId, "med_public", "avatar", sha, "image/png", bytes.Length + 1, 4, 4,
            Owner, "public", Now, Now + 1000));

        Assert.Equal(404, (await service.ReadObjectAsync(ProjectId, "med_public", false, true, Now, default)).Failure!.Status);
    }

    [Fact]
    public void Self_grant_purpose_requires_a_project_quota()
    {
        var refused = MediaPolicyParser.ValidateForSave("avatar", allowClientSelfGrant: true, projectQuotaBytes: 0);
        Assert.Equal("MEDIA_QUOTA_REQUIRED", refused!.Code);
        Assert.Equal(422, refused.Status);
        Assert.Null(MediaPolicyParser.ValidateForSave("avatar", allowClientSelfGrant: true, projectQuotaBytes: 1_000_000));
        Assert.Null(MediaPolicyParser.ValidateForSave("avatar", allowClientSelfGrant: false, projectQuotaBytes: 0));
    }

    [Fact]
    public void Sniffer_reads_dimensions_and_rejects_unknown_bytes()
    {
        var png = MediaSniffer.Sniff(Png(7, 9));
        Assert.Equal("image/png", png!.Value.ContentType);
        Assert.Equal((7, 9), (png.Value.Width, png.Value.Height));

        var jpeg = MediaSniffer.Sniff(Jpeg(120, 80));
        Assert.Equal("image/jpeg", jpeg!.Value.ContentType);
        Assert.Equal((120, 80), (jpeg.Value.Width, jpeg.Value.Height));

        Assert.Null(MediaSniffer.Sniff("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
    }

    private sealed class FixedSecret(string secret) : IAuthSessionSecretProvider
    {
        public string GetSecret() => secret;
    }
}
