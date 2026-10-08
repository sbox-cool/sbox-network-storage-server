using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerDashboardModel(IReadOnlyList<BunnyProject> Projects, string? Error = null);
public sealed record OwnerProjectModel(BunnyProject Project, NetworkStorageProjectResources? Resources,
    IReadOnlyList<ApiKeyInfo> Keys, string? RawKey = null, string? Error = null);

/// <summary>Standalone adaptation of the Network Storage project/settings/key management surface.</summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDashboardController(INetworkStorageProjectService projects, IBunnyWorkspaceClient workspace,
    IAuditLogger audit) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Index() => Redirect("/dashboard");

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
        => View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(await workspace.GetUserProjectsAsync(Owner, ct)));

    [HttpPost("/dashboard/projects")]
    public async Task<IActionResult> CreateProject([FromForm] string? name, [FromForm] string? description,
        [FromForm] bool requireSboxAuth, CancellationToken ct)
    {
        name = name?.Trim();
        if (name is null || name.Length is < 1 or > 64 || description?.Length > 256)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(await workspace.GetUserProjectsAsync(Owner, ct),
                "Project name is required (max 64 characters); description must be at most 256 characters."));
        }
        var result = await projects.CreateProjectAsync(Owner, name, description, true, requireSboxAuth, "player", string.Empty, ct);
        await AuditAsync(result.ProjectId, "project.create", new { name, description, requireSboxAuth }, ct);
        return Redirect(ProjectUrl(result.ProjectId));
    }

    [HttpGet("/dashboard/projects/{projectId}")]
    public async Task<IActionResult> Project(string projectId, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Project.cshtml", model);
    }

    [HttpPost("/dashboard/projects/{projectId}/settings")]
    public async Task<IActionResult> Settings(string projectId, [FromForm] string? tab, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        if (tab is not ("project" or "security" or "player-keys")) return BadRequest("Unknown settings tab.");
        var form = await Request.ReadFormAsync(ct);
        var values = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        if (tab == "project" && (values.GetValueOrDefault("name")?.Trim().Length is not (>= 1 and <= 64)
            || values.GetValueOrDefault("description")?.Length > 256))
            return await ProjectErrorAsync(projectId, "Name must contain 1–64 characters; description may contain at most 256.", ct);
        if (tab == "player-keys" && values.GetValueOrDefault("playerKeyMode") is not ("player" or "playerSave"))
            return BadRequest("Invalid player key mode.");
        await projects.UpdateProjectSettingsAsync(Owner, projectId, tab, values, ct);
        await AuditAsync(projectId, "project.settings", new { tab }, ct);
        return Redirect(ProjectUrl(projectId));
    }

    [HttpPost("/dashboard/projects/{projectId}/keys")]
    public async Task<IActionResult> CreateKey(string projectId, [FromForm] string? label, [FromForm] string? keyType, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (keyType is not ("public" or "secret") || label is null || label.Trim().Length is < 1 or > 64 || model.Keys.Count >= 100)
            return await ProjectErrorAsync(projectId, "Choose public or secret, provide a label of 1–64 characters, and keep fewer than 100 keys.", ct);
        var (key, raw) = await projects.CreateProjectKeyAsync(Owner, projectId, label.Trim(), keyType, null, ct);
        await AuditAsync(projectId, "key.create", new { keyType, key.Label }, ct);
        model = await LoadProjectAsync(projectId, ct);
        return View("~/Views/Owner/Project.cshtml", model! with { RawKey = raw });
    }

    [HttpPost("/dashboard/projects/{projectId}/keys/toggle")]
    public Task<IActionResult> ToggleKey(string projectId, [FromForm] string? key, CancellationToken ct)
        => ChangeKeyAsync(projectId, key, false, ct);

    [HttpPost("/dashboard/projects/{projectId}/keys/revoke")]
    public Task<IActionResult> RevokeKey(string projectId, [FromForm] string? key, CancellationToken ct)
        => ChangeKeyAsync(projectId, key, true, ct);

    [HttpPost("/dashboard/projects/{projectId}/delete")]
    public async Task<IActionResult> DeleteProject(string projectId, [FromForm] string? confirmation, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        if (!string.Equals(confirmation, projectId, StringComparison.Ordinal))
            return await ProjectErrorAsync(projectId, "Type the exact project ID to confirm permanent deletion.", ct);
        await AuditAsync(projectId, "project.delete", new { projectId }, ct);
        await projects.DeleteProjectAsync(Owner, projectId, ct);
        return Redirect("/dashboard");
    }

    private async Task<IActionResult> ChangeKeyAsync(string projectId, string? key, bool revoke, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (key is null || !model.Keys.Any(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal))) return NotFound();
        if (revoke) await projects.RemoveProjectKeyAsync(Owner, projectId, key, ct);
        else await projects.ToggleProjectKeyAsync(Owner, projectId, key, ct);
        await AuditAsync(projectId, revoke ? "key.revoke" : "key.toggle", new { }, ct);
        return Redirect(ProjectUrl(projectId));
    }

    private async Task<OwnerProjectModel?> LoadProjectAsync(string projectId, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        return new OwnerProjectModel(access.Project, await projects.GetProjectResourcesForOwnerAsync(Owner, projectId, ct),
            await projects.GetProjectKeysAsync(Owner, projectId, ct));
    }

    private async Task<IActionResult> ProjectErrorAsync(string projectId, string error, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        var model = await LoadProjectAsync(projectId, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Project.cshtml", model with { Error = error });
    }

    private Task AuditAsync(string projectId, string action, object summary, CancellationToken ct)
        => audit.LogActionAsync(new AuditLogRequest(ProjectId: projectId, UserId: Owner.ToString(CultureInfo.InvariantCulture),
            Action: action, Actor: new { id = Owner, type = "owner-dashboard" }, Target: new { id = projectId, type = "project" },
            Summary: summary, Before: null, After: null), ct);

    private static string ProjectUrl(string projectId) => $"/dashboard/projects/{Uri.EscapeDataString(projectId)}";
}
