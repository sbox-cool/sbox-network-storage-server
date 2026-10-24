using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Deployment console. Preview is read-only and shows the dependency-aware plan. Approve re-checks the same
/// proposal and applies it only when the re-checked plan hash equals the hash the owner approved.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDeploymentController(INetworkStorageProjectService projects, INetworkStorageStore store,
    IWorkspaceStore workspace, ManagementMutationHandler mutations, IAuditLogger audit) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/deployment";

    private const string SampleProposal = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: players
              name: players
              collectionType: player
              schema:
                coins:
                  type: number
        """;

    private IReadOnlyList<IEndpointScriptRuntime> Runtimes =>
        HttpContext.RequestServices.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>();

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        return View("~/Views/Owner/Deployment.cshtml", new DeploymentPageModel(projectId, project.Name, SampleProposal, null));
    }

    [HttpPost(Route + "/preview")]
    public async Task<IActionResult> Preview(string projectId, [FromForm] string? proposal, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        return await RenderAsync(project, projectId, proposal ?? "", null, StatusCodes.Status200OK, ct);
    }

    [HttpPost(Route + "/apply")]
    public async Task<IActionResult> Apply(string projectId, [FromForm] string? proposal, [FromForm] string? planHash, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var text = proposal ?? "";
        if (string.IsNullOrWhiteSpace(planHash))
            return await RenderAsync(project, projectId, text, "Preview the plan before approving.", StatusCodes.Status400BadRequest, ct);

        DeploymentProposal parsed;
        try
        {
            parsed = DeploymentProposalReader.Parse(text, store.MaxPayloadBytes);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return await RenderAsync(project, projectId, text, ex.Message, StatusCodes.Status400BadRequest, ct);
        }

        var result = await OwnerDeployment.ApplyAsync(store, workspace, mutations, audit, projectId, parsed, planHash, Runtimes, ct);
        switch (result.Outcome)
        {
            case DeploymentOutcome.Applied:
                OwnerFlash.Success(this, result.Message);
                return Redirect(OwnerProjectScope.ProjectUrl(projectId) + "/deployment");
            case DeploymentOutcome.Stale:
            case DeploymentOutcome.Conflict:
                return await RenderAsync(project, projectId, text, result.Message, StatusCodes.Status409Conflict, ct);
            case DeploymentOutcome.Blocked:
                return await RenderAsync(project, projectId, text, result.Message, StatusCodes.Status400BadRequest, ct);
            default:
                return await RenderAsync(project, projectId, text, result.Message, StatusCodes.Status500InternalServerError, ct);
        }
    }

    /// <summary>Renders the console with a fresh preview of the proposal. A proposal that does not parse is shown with its error.</summary>
    private async Task<IActionResult> RenderAsync(WorkspaceProject project, string projectId, string proposalText,
        string? error, int status, CancellationToken ct)
    {
        DeploymentPlan? plan = null;
        try
        {
            var parsed = DeploymentProposalReader.Parse(proposalText, store.MaxPayloadBytes);
            plan = await OwnerDeployment.PreviewAsync(store, workspace, mutations, projectId, parsed, Runtimes, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Deployment.cshtml",
                new DeploymentPageModel(projectId, project.Name, proposalText, null, error ?? ex.Message));
        }
        Response.StatusCode = status;
        return View("~/Views/Owner/Deployment.cshtml",
            new DeploymentPageModel(projectId, project.Name, proposalText, plan, error));
    }
}
