using SboxNetworkStorage.Infrastructure.NetworkStorage.Media;

namespace SboxNetworkStorage.Server.Tests.Media;

public sealed class LocalDiskMediaBlobStoreTests : IDisposable
{
    private const string ProjectId = "proj_example";
    private static readonly string Sha = new('a', 64);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ns-blobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Delete_when_the_project_directory_is_missing_is_a_no_op()
    {
        var store = new LocalDiskMediaBlobStore(_root);

        await store.DeleteAsync(ProjectId, Sha, default);

        Assert.False(await store.ExistsAsync(ProjectId, Sha, default));
    }

    [Fact]
    public async Task Delete_removes_the_blob_and_a_repeated_delete_is_a_no_op()
    {
        var store = new LocalDiskMediaBlobStore(_root);
        await store.WriteAsync(ProjectId, Sha, [1, 2, 3], default);

        await store.DeleteAsync(ProjectId, Sha, default);
        await store.DeleteAsync(ProjectId, Sha, default);

        Assert.False(await store.ExistsAsync(ProjectId, Sha, default));
    }
}
