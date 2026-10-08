using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.Workspace;

public interface IWebsiteStorageClient
{
    Task<string> UploadFileAsync(string path, byte[] content, string contentType, CancellationToken cancellationToken);
    Task<string> GetCdnUrlAsync(string path, CancellationToken cancellationToken);
}
