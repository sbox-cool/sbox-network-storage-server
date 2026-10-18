using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Media;

/// <summary>
/// Content-addressed blobs in an S3-compatible bucket under <c>media/{projectId}/{sha256}</c>.
/// Keys are built only from validated segments, so no caller-supplied text reaches the key path unchecked.
/// </summary>
public sealed class S3MediaBlobStore : IMediaBlobStore, IDisposable
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3MediaBlobStore(IAmazonS3 client, string bucket)
    {
        _client = client;
        _bucket = bucket;
    }

    /// <summary>Builds the client from the <c>media.s3.*</c> settings. Empty keys fall back to the SDK default credential chain.</summary>
    public static S3MediaBlobStore Create(string endpoint, string region, string bucket, bool forcePathStyle,
        string accessKeyId, string secretAccessKey)
    {
        var config = new AmazonS3Config
        {
            AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region,
            ForcePathStyle = forcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(endpoint)) config.ServiceURL = endpoint;

        IAmazonS3 client = string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
        return new S3MediaBlobStore(client, bucket);
    }

    public async Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, KeyFor(projectId, sha256), ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = KeyFor(projectId, sha256),
            InputStream = stream,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task<byte[]?> ReadAsync(string projectId, string sha256, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, KeyFor(projectId, sha256), ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();

    private static string KeyFor(string projectId, string sha256)
    {
        if (!StorageIdValidation.IsValidCollectionId(projectId))
            throw new ArgumentException("Invalid project id for media blob key.", nameof(projectId));
        if (!Sha256Pattern.IsMatch(sha256))
            throw new ArgumentException("Invalid sha256 for media blob key.", nameof(sha256));
        return $"media/{projectId}/{sha256}";
    }
}
