using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Media;

/// <summary>
/// Content-addressed blobs on local disk at <c>{root}/{projectId}/{sha256}</c>. Both path segments are
/// validated against strict patterns before use, so a caller can never name a path outside <c>root</c>.
/// Writes go to a temp file that is moved into place, so a reader never sees a partial blob.
/// </summary>
public sealed class LocalDiskMediaBlobStore : IMediaBlobStore
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly string _root;

    public LocalDiskMediaBlobStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct)
        => Task.FromResult(File.Exists(PathFor(projectId, sha256)));

    public async Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct)
    {
        var target = PathFor(projectId, sha256);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<byte[]?> ReadAsync(string projectId, string sha256, CancellationToken ct)
    {
        var path = PathFor(projectId, sha256);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    private string PathFor(string projectId, string sha256)
    {
        if (!StorageIdValidation.IsValidCollectionId(projectId))
            throw new ArgumentException("Invalid project id for media blob path.", nameof(projectId));
        if (!Sha256Pattern.IsMatch(sha256))
            throw new ArgumentException("Invalid sha256 for media blob path.", nameof(sha256));
        return Path.Combine(_root, projectId, sha256);
    }
}
