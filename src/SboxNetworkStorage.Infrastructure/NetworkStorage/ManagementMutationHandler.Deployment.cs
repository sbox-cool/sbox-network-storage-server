using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Entry points for the owner deployment plan (issue #232): a handler bound to one store transaction, the
/// module-ownership read the planner needs, and the explicit delete an approved plan applies.
/// </summary>
public sealed partial class ManagementMutationHandler
{
    /// <summary>
    /// Returns a handler whose reads and writes run through <paramref name="store"/> and <paramref name="workspace"/>.
    /// The endpoint executor is intentionally absent: deployment approval never runs endpoint code.
    /// </summary>
    public ManagementMutationHandler BindTo(INetworkStorageStore store, IWorkspaceStore workspace) =>
        new(_apiKeyResolver, workspace, store, _time, endpointExecutor: null, _valuesProvider, _scriptRuntimes, _keyRevocation, _syncJobs);

    /// <summary>Ids that installed modules own for a kind. Fails loudly: an unreadable module state throws, as on the bulk-write path.</summary>
    public Task<HashSet<string>> ModuleOwnedIdsAsync(long ownerUserId, string projectId, string kind, CancellationToken ct) =>
        kind == "game-values" ? ModuleOwnedValuesItemsAsync(ownerUserId, projectId, ct)
            : ModuleOwnedNamesAsync(ownerUserId, projectId, kind, ct);

    /// <summary>
    /// Deletes one live definition on behalf of an approved owner deployment plan. Membership and module ownership
    /// are checked exactly as for the dashboard delete routes. Staged revision entries are not changed.
    /// </summary>
    public async Task<NetworkStorageResult> DeleteOwnerResourceAsync(
        long ownerUserId,
        string projectId,
        string kind,
        string resourceId,
        CancellationToken ct)
    {
        var projects = await _workspaceClient.GetUserProjectsAsync(ownerUserId, ct);
        if (!projects.Any(project => string.Equals(project.Id, projectId, StringComparison.Ordinal)))
        {
            return ManagementAuthError();
        }

        if (ModuleDeleteRefusal(kind, resourceId, await ModuleOwnedNamesAsync(ownerUserId, projectId, kind, ct)) is { } refusal)
        {
            return refusal;
        }

        switch (kind)
        {
            case "collection":
                await _store.DeleteCollectionAsync(projectId, resourceId, ct);
                break;
            case "endpoint":
                await _store.DeleteEndpointAsync(projectId, resourceId, ct);
                break;
            case "workflow":
                await _store.DeleteWorkflowAsync(projectId, resourceId, ct);
                break;
            case "query":
                await _store.DeleteQueryAsync(projectId, resourceId, ct);
                break;
            default:
                return ValidationFailedResult(kind, $"Deployment plans cannot delete '{kind}' definitions.");
        }

        return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = kind, action = "delete", resourceId });
    }
}
