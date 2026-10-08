using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

public interface IQueryManagementService
{
    Task ClearQueryCacheAsync(long userId, string projectId, string queryId, CancellationToken cancellationToken);
    Task RepublishQueryAsync(long userId, string projectId, string queryId, CancellationToken cancellationToken);
}

public interface IWorkflowManagementService
{
    Task<string> CreateWorkflowAsync(long userId, string projectId, IReadOnlyDictionary<string, string> workflowData, CancellationToken cancellationToken);
    Task UpdateWorkflowAsync(long userId, string projectId, string workflowId, IReadOnlyDictionary<string, string> workflowData, CancellationToken cancellationToken);
    Task DeleteWorkflowAsync(long userId, string projectId, string workflowId, CancellationToken cancellationToken);
}

public interface IPageManagementService
{
    Task RepublishPageAsync(long userId, string projectId, string pageSlug, CancellationToken cancellationToken);
}
