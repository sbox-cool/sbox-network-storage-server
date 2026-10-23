using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class ProjectDeletionTests<TFactory>(TFactory factory) : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_revokes_cached_keys_and_purges_blobs_with_persisted_cleanup_retry(bool failBlobDelete)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        var retained = await factory.CreateProjectAsync();
        var ct = CancellationToken.None;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<INetworkStorageStore>();
        var media = (IMediaStore)store;
        var blobs = services.GetRequiredService<IMediaBlobStore>();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        foreach (var id in new[] { project.ProjectId, retained.ProjectId })
        {
            await store.UpsertCollectionAsync(id, "notes", "Notes", "public", JsonSerializer.SerializeToElement(new { id = "notes", visibility = "public", fields = new { } }), 1, ct);
            await store.UpsertRecordAsync(id, "notes", "76561198000000123", JsonSerializer.SerializeToElement(new { text = "private player data" }), false, 1, ct);
            await blobs.WriteAsync(id, sha, bytes, ct);
            await media.InsertMediaObjectAsync(new MediaObjectRecord(id, "image", "avatar", sha, "image/png", bytes.Length, 1, 1, "76561198000000123", "private", 100, long.MaxValue), ct);
        }
        using var client = factory.CreateClient();
        using (var before = await Send(project.SecretKey, $"/v3/storage/{project.ProjectId}/notes/76561198000000123"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using (var before = await Send(project.PublicKey, $"/v3/storage/{project.ProjectId}/rate-limits"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        NetworkStorageProjectService CreateService(IMediaBlobStore blobStore) => new(
            services.GetRequiredService<IWorkspaceStore>(), services.GetRequiredService<IWorkspaceStorageEnumerator>(),
            services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IStorageKeyCdnWriter>(), store,
            NullLogger<NetworkStorageProjectService>.Instance, services.GetService<IApiKeyCacheInvalidator>(), mediaBlobs: blobStore);
        if (failBlobDelete)
        {
            await Assert.ThrowsAsync<IOException>(() => CreateService(new FailingDeleteBlobStore(blobs))
                .DeleteProjectAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, ct));
            Assert.Null(await store.ReadProjectAsync(project.ProjectId, ct));
            using var denied = await Send(project.SecretKey, $"/v3/storage/{project.ProjectId}/notes/76561198000000123");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(await blobs.ExistsAsync(project.ProjectId, sha, ct));
        }
        // A fresh service has no in-memory knowledge of the failed deletion or its removed media rows.
        await CreateService(blobs).DeleteProjectAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, ct);
        await CreateService(blobs).DeleteProjectAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, ct);
        Assert.False(await blobs.ExistsAsync(project.ProjectId, sha, ct));
        Assert.Null(await store.ReadRecordAsync(project.ProjectId, "notes", "76561198000000123", ct));
        Assert.Empty(await store.ListApiKeysAsync(project.ProjectId, ct));
        Assert.Empty(await store.ListWorkspaceObjectsAsync($"network-storage/keys/projects/{project.ProjectId}/", ct));
        Assert.Empty(await store.ListWorkspaceObjectsAsync($"network-storage-api/keys/projects/{project.ProjectId}/", ct));
        foreach (var key in new[] { project.SecretKey, project.PublicKey })
        {
            using var denied = await Send(key, $"/v3/storage/{project.ProjectId}/rate-limits");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.True(await blobs.ExistsAsync(retained.ProjectId, sha, ct));
        using var other = await Send(retained.SecretKey, $"/v3/storage/{retained.ProjectId}/notes/76561198000000123");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Contains("private player data", await other.Content.ReadAsStringAsync());

        async Task<HttpResponseMessage> Send(string key, string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("x-api-key", key);
            return await client.SendAsync(request);
        }
    }

    private sealed class FailingDeleteBlobStore(IMediaBlobStore inner) : IMediaBlobStore
    {
        public Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct) => inner.ExistsAsync(projectId, sha256, ct);
        public Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct) => inner.WriteAsync(projectId, sha256, bytes, ct);
        public Task<MediaBlob?> OpenReadAsync(string projectId, string sha256, CancellationToken ct) => inner.OpenReadAsync(projectId, sha256, ct);
        public Task DeleteAsync(string projectId, string sha256, CancellationToken ct) => throw new IOException("Injected blob storage outage");
    }
}

public sealed class SqliteProjectDeletionTests(SqliteHostFactory factory) : ProjectDeletionTests<SqliteHostFactory>(factory);
public sealed class PostgresProjectDeletionTests(PostgresHostFactory factory) : ProjectDeletionTests<PostgresHostFactory>(factory);
