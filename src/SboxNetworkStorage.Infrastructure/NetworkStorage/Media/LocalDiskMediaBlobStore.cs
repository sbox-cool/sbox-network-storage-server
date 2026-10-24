using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Media;

/// <summary>
/// Content-addressed blobs on local disk at <c>{root}/{projectId}/{sha256}</c>. Both path segments are
/// validated against strict patterns before use, so a caller can never name a path outside <c>root</c>.
/// Writes go to a temp file that is moved into place, so a reader never sees a partial blob. Concurrent
/// writers for the same content keep the first completed file rather than replacing a file readers may hold.
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
            try
            {
                File.Move(temp, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another request installed the same content-addressed blob first.
            }
            catch (UnauthorizedAccessException) when (File.Exists(target))
            {
                // Windows can report a destination collision as access denied; the existing blob is immutable.
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task DeleteAsync(string projectId, string sha256, CancellationToken ct)
    {
        // A blob already gone is not an error. Windows throws when the project directory itself is missing, so check first.
        var path = PathFor(projectId, sha256);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<MediaBlob?> OpenReadAsync(string projectId, string sha256, CancellationToken ct)
    {
        var path = PathFor(projectId, sha256);
        if (!File.Exists(path)) return Task.FromResult<MediaBlob?>(null);
        // Streamed: the endpoint copies the bytes to the response as they are read, never buffering the whole file.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<MediaBlob?>(new MediaBlob(stream, stream.Length, owner: null));
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
