using SboxNetworkStorage.Application.Common;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.Workspace;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Result of <see cref="OwnerDeployment.ApplyAsync"/>.</summary>
public enum DeploymentOutcome
{
    /// <summary>The plan was re-checked, matched its hash and committed in one transaction.</summary>
    Applied,
    /// <summary>The project changed since the plan was previewed; nothing was written.</summary>
    Stale,
    /// <summary>The re-checked plan has blocking issues; nothing was written.</summary>
    Blocked,
    /// <summary>A concurrent write conflicted with this transaction; the transaction rolled back.</summary>
    Conflict,
    /// <summary>A write was refused by the mutation handler; the transaction rolled back.</summary>
    Failed,
}

/// <summary>The outcome of an apply attempt, with a message for the owner.</summary>
public sealed record DeploymentApplyResult(DeploymentOutcome Outcome, string Message);

/// <summary>
/// Previews and applies deployment plans. Preview is read-only. Apply re-plans inside one serializable
/// transaction, requires the re-planned hash to equal the approved hash, and commits all upserts and deletes
/// together or none of them.
/// </summary>
internal static class OwnerDeployment
{
    private static readonly string[] OwnableKinds = ["collection", "endpoint", "workflow", "query", "game-values"];

    public static async Task<DeploymentPlan> PreviewAsync(INetworkStorageStore store, IWorkspaceStore workspace,
        ManagementMutationHandler mutations, string projectId, DeploymentProposal proposal,
        IReadOnlyList<IEndpointScriptRuntime> runtimes, CancellationToken ct)
    {
        var state = await LoadStateAsync(store, workspace, mutations, projectId, ct);
        return DeploymentPlanner.Plan(proposal, state, runtimes);
    }

    public static async Task<DeploymentApplyResult> ApplyAsync(INetworkStorageStore store, IWorkspaceStore workspace,
        ManagementMutationHandler mutations, IAuditLogger audit, string projectId, DeploymentProposal proposal,
        string expectedHash, IReadOnlyList<IEndpointScriptRuntime> runtimes, CancellationToken ct)
    {
        if (store is not ISerializableStoreTransactions serializable)
            throw new NotSupportedException("Deployment plans need a store with serializable transactions.");

        DeploymentPlan plan;
        await using (var transaction = await serializable.BeginSerializableTransactionAsync(ct))
        {
            try
            {
                var txWorkspace = new StoreMetadataWorkspaceClient(new StoreWorkspaceObjectClient(transaction.Store),
                    transaction.Store, Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreMetadataWorkspaceClient>.Instance);
                var txMutations = mutations.BindTo(transaction.Store, txWorkspace);

                plan = DeploymentPlanner.Plan(proposal,
                    await LoadStateAsync(transaction.Store, txWorkspace, txMutations, projectId, ct), runtimes);
                if (!string.Equals(plan.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return new DeploymentApplyResult(DeploymentOutcome.Stale,
                        "The project changed since this plan was previewed. Preview it again before approving.");
                if (plan.Blocked)
                    return new DeploymentApplyResult(DeploymentOutcome.Blocked, "The plan has blocking issues. Nothing was applied.");

                var definitions = proposal.Upserts.ToDictionary(upsert => (upsert.Kind, upsert.Id), upsert => upsert.Resource);
                foreach (var item in DeploymentPlanner.WriteOrder(plan))
                {
                    var result = await txMutations.SaveOwnerResourceAsync(OwnerProjectScope.Owner, projectId, item.Kind,
                        definitions[(item.Kind, item.Id)], ct, versionSource: "owner-deployment", targetsNext: proposal.TargetsNext);
                    if (result.StatusCode >= 400)
                        return Refused(result, $"{item.Kind} {item.Id}");
                }
                foreach (var item in DeploymentPlanner.DeleteOrder(plan))
                {
                    var result = await txMutations.DeleteOwnerResourceAsync(OwnerProjectScope.Owner, projectId, item.Kind, item.Id, ct);
                    if (result.StatusCode >= 400)
                        return Refused(result, $"delete {item.Kind} {item.Id}");
                }

                await transaction.CommitAsync(ct);
            }
            catch (Exception exception) when (serializable.IsSerializationConflict(exception))
            {
                return new DeploymentApplyResult(DeploymentOutcome.Conflict,
                    "Another change touched this project while the plan was applied. Nothing was applied; preview again.");
            }
        }

        await OwnerProjectScope.AuditAsync(audit, projectId, "deployment.apply", new
        {
            target = proposal.Target,
            hash = plan.Hash,
            upserts = plan.Items.Count(item => item.Action is "create" or "update"),
            deletes = plan.Items.Count(item => item.Action == "delete"),
        }, ct);
        return new DeploymentApplyResult(DeploymentOutcome.Applied, "Deployment plan applied.");
    }

    private static DeploymentApplyResult Refused(NetworkStorageResult result, string what) =>
        new(DeploymentOutcome.Failed,
            $"Could not apply {what} ({result.PublicErrorCode ?? $"status {result.StatusCode}"}). Nothing was applied.");

    private static async Task<DeploymentState> LoadStateAsync(INetworkStorageStore store, IWorkspaceStore workspace,
        ManagementMutationHandler mutations, string projectId, CancellationToken ct)
    {
        var live = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var staged = await RevisionOverrides.ReadAsync(workspace, OwnerProjectScope.Owner, projectId, ct);
        var hasRevisionData = await RevisionOverrides.HasRevisionDataAsync(workspace, OwnerProjectScope.Owner, projectId, ct);
        var project = (await workspace.GetUserProjectsAsync(OwnerProjectScope.Owner, ct))
            .Single(project => project.Id == projectId);
        var package = await workspace.GetProjectResourceAsync<Dictionary<string, JsonElement>>(
            OwnerProjectScope.Owner, projectId, "game-package.json", ct);
        var policy = JsonSerializer.SerializeToElement(new
        {
            currentRevisionId = package is not null && package.TryGetValue("currentRevisionId", out var revision)
                ? (JsonElement?)revision : null,
            project.RevisionEnforcementEnabled, project.RevisionEnforcementMode,
            project.RevisionGracePeriodMinutes, project.RevisionPostGraceAction, project.RevisionForceEndpointUpgrade,
            project.RevisionMinimumRevisionId, project.RevisionInGraceAction, project.RevisionServerEnforcementEnabled,
            project.RevisionRetentionCount,
        });
        var owned = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var kind in OwnableKinds)
            owned[kind] = await mutations.ModuleOwnedIdsAsync(OwnerProjectScope.Owner, projectId, kind, ct);
        return new DeploymentState(live, staged, hasRevisionData, owned, policy);
    }
}
