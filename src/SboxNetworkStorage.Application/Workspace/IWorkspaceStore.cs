using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.Workspace;

public interface IWorkspaceStore
{
    Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken);

    Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken);

    Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken);

    Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken);
    Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken);
    Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken);

    Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken);
    Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken);
    Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken);
}
