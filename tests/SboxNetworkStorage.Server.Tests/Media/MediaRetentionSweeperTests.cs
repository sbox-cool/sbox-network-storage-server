using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Media;

public sealed class MediaRetentionSweeperTests
{
    private const string ProjectId = "proj_example";
    private const string Owner = "76561198000000001";
    private const string Secret = "media-test-secret-0123456789abcdef";
    private const long Now = 1_700_000_000_000;
    private const long Live = Now + 10_000_000_000;
    private const long Expired = Now + 1_000;

    private static readonly string PublicPolicyJson = """
        {"enabled":true,"purposes":{"avatar":{"enabled":true,"allowClientSelfGrant":true,"visibility":"public",
        "contentTypes":["image/png","image/jpeg"],"maxBytes":1048576,"maxWidth":4096,"maxHeight":4096,
        "perPlayerPerHour":30,"projectQuotaBytes":0,"retentionDays":30}}}
        """;

    private static MediaProjectPolicy Policy(string json) =>
        MediaPolicyParser.Parse(JsonDocument.Parse(json).RootElement.Clone(), 10 * 1024 * 1024);

    private static MediaGrantRequest Request(byte[] body) =>
        new("avatar", "image/png", body.LongLength, Hex(SHA256.HashData(body)), null);

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

    private static (NetworkStorageMediaService Service, FakeMediaStore Store, FakeBlobStore Blobs) Build()
    {
        var tokens = new MediaGrantTokens(new FixedSecret(Secret));
        var store = new FakeMediaStore();
        var blobs = new FakeBlobStore();
        return (new NetworkStorageMediaService(store, blobs, tokens), store, blobs);
    }

    /// <summary>An object row whose bytes are <paramref name="bytes"/>, stored in the project with the given expiry.</summary>
    private static MediaObjectRecord Row(string mediaId, byte[] bytes, long expiresAt, string projectId = ProjectId) =>
        new(projectId, mediaId, "avatar", Hex(SHA256.HashData(bytes)), "image/png", bytes.LongLength, 64, 32,
            Owner, "public", Now, expiresAt);

    private static byte[] Sha(byte[] bytes) => SHA256.HashData(bytes);

    [Fact]
    public async Task Sweep_deletes_an_expired_row_and_the_blob_only_it_referenced()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        blobs.Items[(ProjectId, Hex(Sha(bytes)))] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));

        var result = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(1, result.ObjectsDeleted);
        Assert.Equal(1, result.BlobsDeleted);
        Assert.Empty(result.BlobFailures);
        Assert.Empty(store.Objects);
        Assert.Empty(blobs.Items);
    }

    [Fact]
    public async Task Sweep_keeps_a_blob_that_an_unexpired_row_still_references()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        var sha = Hex(Sha(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));
        store.Objects.Add(Row("med_live", bytes, Live));

        var result = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(1, result.ObjectsDeleted);
        Assert.Equal(0, result.BlobsDeleted);
        Assert.Equal(new[] { "med_live" }, store.Objects.Select(o => o.MediaId));
        Assert.True(blobs.Items.ContainsKey((ProjectId, sha)));
        Assert.True((await service.ReadObjectAsync(ProjectId, "med_live", false, true, Now + 2_000, default)).Ok);
    }

    [Fact]
    public async Task Sweep_leaves_unexpired_objects_and_their_blobs_untouched()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        blobs.Items[(ProjectId, Hex(Sha(bytes)))] = bytes;
        store.Objects.Add(Row("med_live", bytes, Live));
        store.Objects.Add(Row("med_edge", Png(8, 8), Now + 2_001));

        var result = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(0, result.ObjectsDeleted);
        Assert.Equal(0, result.BlobsDeleted);
        Assert.Equal(0, blobs.Deletes);
        Assert.Equal(2, store.Objects.Count);
        Assert.True(blobs.Items.ContainsKey((ProjectId, Hex(Sha(bytes)))));
    }

    [Fact]
    public async Task Blobs_are_per_project_so_another_projects_row_does_not_keep_one_alive()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        var sha = Hex(Sha(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        blobs.Items[("proj_other", sha)] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));
        store.Objects.Add(Row("med_other", bytes, Live, "proj_other"));

        var result = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(1, result.BlobsDeleted);
        Assert.False(blobs.Items.ContainsKey((ProjectId, sha)));
        Assert.True(blobs.Items.ContainsKey(("proj_other", sha)));
    }

    [Fact]
    public async Task Sweep_pages_through_rows_and_removes_the_blob_only_after_its_last_row()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        var sha = Hex(Sha(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(Row("med_a", bytes, Expired));
        store.Objects.Add(Row("med_b", bytes, Expired));

        var result = await service.SweepExpiredAsync(Now + 2_000, 1, default);

        Assert.Equal(2, result.ObjectsDeleted);
        Assert.Equal(1, result.BlobsDeleted);
        Assert.Equal(1, blobs.Deletes);
        Assert.Empty(store.Objects);
        Assert.Empty(blobs.Items);
    }

    [Fact]
    public async Task Failed_blob_delete_keeps_the_expired_rows_and_the_next_sweep_retries_them()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        var sha = Hex(Sha(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));
        blobs.FailingDeletes.Add((ProjectId, sha));

        var first = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(0, first.ObjectsDeleted);
        Assert.Equal(0, first.BlobsDeleted);
        Assert.Single(first.BlobFailures);
        Assert.Single(store.Objects);
        Assert.True(blobs.Items.ContainsKey((ProjectId, sha)));

        blobs.FailingDeletes.Clear();
        var second = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(1, second.ObjectsDeleted);
        Assert.Equal(1, second.BlobsDeleted);
        Assert.Empty(second.BlobFailures);
        Assert.Empty(store.Objects);
        Assert.Empty(blobs.Items);
    }

    [Fact]
    public async Task Repeated_sweep_has_nothing_left_to_delete()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        blobs.Items[(ProjectId, Hex(Sha(bytes)))] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));

        await service.SweepExpiredAsync(Now + 2_000, 200, default);
        var again = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(0, again.ObjectsDeleted);
        Assert.Equal(0, again.BlobsDeleted);
        Assert.Empty(again.BlobFailures);
        Assert.Equal(1, blobs.Deletes);
    }

    [Fact]
    public async Task Upload_of_bytes_under_sweep_waits_for_the_sweep_and_its_blob_survives()
    {
        var (service, store, blobs) = Build();
        var body = Png(64, 32);
        var sha = Hex(Sha(body));
        blobs.Items[(ProjectId, sha)] = body;
        store.Objects.Add(Row("med_old", body, Expired));

        var deleteEntered = new TaskCompletionSource();
        var releaseDelete = new TaskCompletionSource();
        blobs.DeleteGate = _ =>
        {
            deleteEntered.TrySetResult();
            return releaseDelete.Task;
        };

        var sweep = service.SweepExpiredAsync(Now + 2_000, 200, default);
        await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var policy = Policy(PublicPolicyJson);
        var purpose = service.AuthorizeGrant(policy, Request(body), callerIsSecret: false).Value!;
        var issued = (await service.IssueGrantAsync(ProjectId, purpose, Request(body), Owner, Now + 2_001, default)).Value!;
        var claims = service.VerifyGrant(ProjectId, issued.GrantToken, Now + 2_002).Value!;
        var upload = service.CompleteUploadAsync(claims, purpose, body, "image/png", Now + 2_003, default);

        await Task.Delay(100);
        Assert.False(upload.IsCompleted);

        releaseDelete.SetResult();
        var swept = await sweep;
        var uploaded = await upload;

        Assert.True(uploaded.Ok);
        Assert.Equal(1, swept.ObjectsDeleted);
        Assert.Equal(1, swept.BlobsDeleted);
        Assert.True(blobs.Items.ContainsKey((ProjectId, sha)));
        Assert.Equal(new[] { uploaded.Value!.MediaId }, store.Objects.Select(o => o.MediaId));
        Assert.True((await service.ReadObjectAsync(ProjectId, uploaded.Value.MediaId, false, true, Now + 2_004, default)).Ok);
    }

    [Fact]
    public async Task Blob_survives_when_a_row_is_renewed_after_the_sweep_read_its_batch()
    {
        var (service, store, blobs) = Build();
        var bytes = Png(64, 32);
        var sha = Hex(Sha(bytes));
        blobs.Items[(ProjectId, sha)] = bytes;
        store.Objects.Add(Row("med_old", bytes, Expired));
        // Another writer re-points the row at live bytes after the sweep read the batch and before it decides.
        store.BeforeNextBlobLock = () =>
        {
            store.Objects.RemoveAll(o => o.MediaId == "med_old");
            store.Objects.Add(Row("med_old", bytes, Live));
            return Task.CompletedTask;
        };

        var result = await service.SweepExpiredAsync(Now + 2_000, 200, default);

        Assert.Equal(0, result.ObjectsDeleted);
        Assert.Equal(0, result.BlobsDeleted);
        Assert.Empty(result.BlobFailures);
        Assert.Equal(0, blobs.Deletes);
        Assert.True(blobs.Items.ContainsKey((ProjectId, sha)));
        Assert.Equal(Live, Assert.Single(store.Objects).ExpiresAtUnixMs);
        Assert.True((await service.ReadObjectAsync(ProjectId, "med_old", false, true, Now + 2_000, default)).Ok);
    }

    [Fact]
    public async Task Sweeper_first_sweep_runs_one_minute_after_start_then_sweeps_once_a_day()
    {
        var (service, store, blobs) = Build();
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(Now));
        var sweeper = new MediaRetentionSweeperService(service, clock, NullLoggerFactory.Instance);
        var bytes = Png(64, 32);
        blobs.Items[(ProjectId, Hex(Sha(bytes)))] = bytes;
        store.Objects.Add(Row("med_first", bytes, Expired));

        clock.Now = clock.Now.AddSeconds(59);
        await sweeper.SweepIfDueAsync(default);
        Assert.Single(store.Objects);

        clock.Now = clock.Now.AddSeconds(1);
        await sweeper.SweepIfDueAsync(default);
        Assert.Empty(store.Objects);

        store.Objects.Add(Row("med_second", bytes, Expired));
        clock.Now = clock.Now.AddHours(23);
        await sweeper.SweepIfDueAsync(default);
        Assert.Single(store.Objects);

        clock.Now = clock.Now.AddHours(1);
        await sweeper.SweepIfDueAsync(default);
        Assert.Empty(store.Objects);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedSecret(string secret) : IAuthSessionSecretProvider
    {
        public string GetSecret() => secret;
    }
}
