using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Media;

public abstract class MediaQuotaConcurrencyTests<TFactory>(TFactory factory) : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parallel_uploads_cannot_exceed_quota_and_rejected_grant_can_be_retried(bool sharedBytes)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = (IMediaStore)services.GetRequiredService<INetworkStorageStore>();
        var blobs = services.GetRequiredService<IMediaBlobStore>();
        var tokens = services.GetRequiredService<MediaGrantTokens>();
        var simultaneousBlobs = new SimultaneousWrites(blobs);
        var first = new NetworkStorageMediaService(store, simultaneousBlobs, tokens);
        var second = new NetworkStorageMediaService(store, simultaneousBlobs, tokens);
        var bodies = new[] { Png(1), Png((byte)(sharedBytes ? 1 : 2)) };
        var policy = MediaPolicyParser.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            enabled = true,
            purposes = new { avatar = new { enabled = true, allowClientSelfGrant = true, visibility = "public",
                contentTypes = new[] { "image/png" }, maxBytes = 1024, maxWidth = 4096, maxHeight = 4096,
                perPlayerPerHour = 30, projectQuotaBytes = bodies[0].Length, retentionDays = 30 } },
        }), 1024);
        var purpose = policy.Purposes["avatar"];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var claims = new MediaGrantClaims[2];
        for (var i = 0; i < 2; i++)
        {
            var request = new MediaGrantRequest("avatar", "image/png", bodies[i].Length,
                Convert.ToHexStringLower(SHA256.HashData(bodies[i])), null);
            var issued = await first.IssueGrantAsync(project.ProjectId, purpose, request, "76561198000000123", now, default);
            Assert.True(issued.Ok, issued.Failure?.Message);
            claims[i] = first.VerifyGrant(project.ProjectId, issued.Value!.GrantToken, now).Value!;
        }

        // Neither initial blob write can finish before both uploads have passed their preflight quota check.
        var results = await Task.WhenAll(
            first.CompleteUploadAsync(claims[0], purpose, bodies[0], "image/png", now + 1, default),
            second.CompleteUploadAsync(claims[1], purpose, bodies[1], "image/png", now + 1, default));
        var winner = Array.FindIndex(results, result => result.Ok);
        Assert.InRange(winner, 0, 1);
        var rejected = 1 - winner;
        Assert.False(results[rejected].Ok);
        Assert.Equal(413, results[rejected].Failure!.Status);
        Assert.Equal("MEDIA_QUOTA_EXCEEDED", results[rejected].Failure!.Code);
        Assert.Equal(bodies[0].LongLength, await store.SumMediaObjectBytesAsync(project.ProjectId, "avatar", now + 2, default));
        Assert.NotNull(await store.GetMediaObjectAsync(project.ProjectId, claims[winner].MediaId, default));
        Assert.Null(await store.GetMediaObjectAsync(project.ProjectId, claims[rejected].MediaId, default));
        Assert.True(await blobs.ExistsAsync(project.ProjectId, claims[winner].Sha256, default));
        Assert.Equal(sharedBytes, await blobs.ExistsAsync(project.ProjectId, claims[rejected].Sha256, default));

        // Raising the purpose quota must let the same unconsumed grant complete, not return GRANT_USED.
        var retry = await new NetworkStorageMediaService(store, blobs, tokens).CompleteUploadAsync(claims[rejected],
            purpose with { ProjectQuotaBytes = bodies[0].LongLength * 2 }, bodies[rejected], "image/png", now + 3, default);
        Assert.True(retry.Ok, retry.Failure?.Message);
        Assert.Equal(bodies[0].LongLength * 2, await store.SumMediaObjectBytesAsync(project.ProjectId, "avatar", now + 4, default));
    }

    private static byte[] Png(byte width)
    {
        byte[] bytes = [0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10, 0, 0, 0, 13,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, width, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        return bytes;
    }

    private sealed class SimultaneousWrites(IMediaBlobStore inner) : IMediaBlobStore
    {
        private readonly TaskCompletionSource _bothWriting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct) => inner.ExistsAsync(projectId, sha256, ct);
        public async Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _writes) == 2) _bothWriting.TrySetResult();
            await _bothWriting.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await inner.WriteAsync(projectId, sha256, bytes, ct);
        }
        public Task DeleteAsync(string projectId, string sha256, CancellationToken ct) => inner.DeleteAsync(projectId, sha256, ct);
        public Task<MediaBlob?> OpenReadAsync(string projectId, string sha256, CancellationToken ct) => inner.OpenReadAsync(projectId, sha256, ct);
    }
}

public sealed class SqliteMediaQuotaConcurrencyTests(SqliteHostFactory factory) : MediaQuotaConcurrencyTests<SqliteHostFactory>(factory);
public sealed class PostgresMediaQuotaConcurrencyTests(PostgresHostFactory factory) : MediaQuotaConcurrencyTests<PostgresHostFactory>(factory);
