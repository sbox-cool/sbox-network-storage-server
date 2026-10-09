using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.Workspace;

/// <summary>
/// Loads workspace project activity inputs, matching Bun's loadProjectActivity().
/// </summary>
public static class ProjectActivityLoader
{
    public static async Task<ProjectActivitySupport.Snapshot> LoadAsync(
        IWorkspaceStore client,
        long storageOwnerUserId,
        WorkspaceProject project,
        string monthKey,
        CancellationToken cancellationToken)
    {
        var collectionsTask = client.GetProjectResourceTextAsync(
            storageOwnerUserId,
            project.Id,
            "collections.json",
            cancellationToken);
        var endpointsTask = client.GetProjectResourceTextAsync(
            storageOwnerUserId,
            project.Id,
            "endpoints.json",
            cancellationToken);
        var workflowsTask = client.GetProjectResourceTextAsync(
            storageOwnerUserId,
            project.Id,
            "workflows.json",
            cancellationToken);
        var usageTask = client.GetProjectResourceTextAsync(
            storageOwnerUserId,
            project.Id,
            $"usage/{monthKey}.json",
            cancellationToken);

        await Task.WhenAll(collectionsTask, endpointsTask, workflowsTask, usageTask);

        return ProjectActivitySupport.Compute(
            project,
            await collectionsTask,
            await endpointsTask,
            await workflowsTask,
            await usageTask);
    }
}
