using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Owner-only download of the same archive <c>sbox-ns export</c> writes. Secrets are
/// excluded unless the owner opts in and confirms the password (and authenticator code when
/// enabled), so a stolen session cookie alone cannot take the server's keys. Every download is
/// audit-logged per project. Antiforgery is enforced by the global <c>AutoValidateAntiforgeryToken</c> filter.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerExportController(INetworkStorageStore store, INetworkStorageStoreAdmin admin, EffectiveConfig config,
    IWorkspaceStore workspace, IAuditLogger audit, OwnerAccountService accounts, ILogger<OwnerExportController> logger) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    /// <summary>One export at a time: each one stages a full dump on disk.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [HttpPost("/dashboard/export")]
    [EnableRateLimiting(OwnerLoginLimits.Policy)]
    public async Task<IActionResult> Export([FromForm] bool includeSecrets, [FromForm] string? password,
        [FromForm] string? secondFactor, CancellationToken ct)
    {
        if (includeSecrets && await ConfirmOwnerAsync(password, secondFactor, ct) is { } refused) return refused;
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return await DashboardErrorAsync(StatusCodes.Status409Conflict, "An export is already running. Try again when it has finished.", "export", ct);
        }

        try
        {
            StagedExport staged;
            try
            {
                staged = await ServerArchive.PrepareExportAsync(store, admin, config, includeSecrets, ct);
            }
            catch (ExportArchiveException ex)
            {
                return await DashboardErrorAsync(StatusCodes.Status500InternalServerError, ex.Message, "export", ct);
            }

            await using (staged)
            {
                var manifest = staged.Manifest;
                foreach (var project in manifest.Projects)
                {
                    await audit.LogActionAsync(new AuditLogRequest(ProjectId: project.Id,
                        UserId: Owner.ToString(CultureInfo.InvariantCulture), Action: "server.export",
                        Actor: new { id = Owner, type = "owner-dashboard", name = User.Identity?.Name },
                        Target: new { id = project.Id, type = "project" },
                        Summary: new { message = "Downloaded a whole-server export", includeSecrets, rows = project.Counts.Values.Sum() },
                        Before: null, After: null), ct);
                }

                logger.LogWarning("Owner {Owner} downloaded a server export ({Projects} projects, {Rows} rows, secrets {Secrets}) from {RemoteAddress}",
                    User.Identity?.Name, manifest.Projects.Count, manifest.TotalRows, includeSecrets ? "included" : "excluded",
                    HttpContext.Connection.RemoteIpAddress);

                Response.ContentType = "application/gzip";
                Response.Headers.ContentDisposition = $"attachment; filename=\"{ExportFormat.DefaultFileName(manifest.CreatedAt)}\"";
                await staged.WriteToAsync(Response.Body, ct);
            }

            return new EmptyResult();
        }
        finally
        {
            Gate.Release();
        }
    }

    [HttpPost("/dashboard/projects/{projectId}/export")]
    public async Task<IActionResult> ExportProject(string projectId, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct))
            return await DashboardErrorAsync(409, "An export or import is already running.", "export", ct);
        try
        {
            if (!(await workspace.GetUserProjectsAsync(Owner, ct)).Any(project => project.Id == projectId))
                return NotFound();
            await using var staged = await ServerArchive.PrepareProjectExportAsync(store, admin, config, projectId, ct);
            Response.ContentType = "application/gzip";
            Response.Headers.ContentDisposition = $"attachment; filename=\"sbox-ns-project-{projectId}-{staged.Manifest.CreatedAt:yyyyMMdd-HHmmss}.tar.gz\"";
            await audit.LogActionAsync(new AuditLogRequest(projectId, Owner.ToString(CultureInfo.InvariantCulture),
                "project.export", new { id = Owner, type = "owner-dashboard" }, new { id = projectId, type = "project" },
                new { message = "Downloaded a portable project export", rows = staged.Manifest.TotalRows }, null, null), ct);
            await staged.WriteToAsync(Response.Body, ct);
            return new EmptyResult();
        }
        catch (ExportArchiveException ex)
        {
            return await DashboardErrorAsync(400, ex.Message, "export", ct);
        }
        finally { Gate.Release(); }
    }

    [HttpPost("/dashboard/import")]
    [RequestSizeLimit(ProjectArchive.MaxUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ProjectArchive.MaxUploadBytes)]
    public async Task<IActionResult> ImportProject(IFormFile? archive, CancellationToken ct)
    {
        if (archive is null || archive.Length == 0 || archive.Length > ProjectArchive.MaxUploadBytes)
            return await DashboardErrorAsync(400, "Choose a project .tar.gz export no larger than 64 MiB.", "import", ct);
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct))
            return await DashboardErrorAsync(409, "An export or import is already running.", "import", ct);
        try
        {
            await using var input = archive.OpenReadStream();
            var result = await ProjectArchive.ImportAsync(input, store, config, ct);
            var projectId = result.Manifest.Projects[0].Id;
            await audit.LogActionAsync(new AuditLogRequest(projectId, Owner.ToString(CultureInfo.InvariantCulture),
                "project.import", new { id = Owner, type = "owner-dashboard" }, new { id = projectId, type = "project" },
                new { message = "Imported a portable project export", rows = result.RowsApplied }, null, null), ct);
            OwnerFlash.Success(this, "Project imported.");
            return Redirect("/dashboard/projects/" + Uri.EscapeDataString(projectId));
        }
        catch (ExportArchiveException ex)
        {
            return await DashboardErrorAsync(400, ex.Message, "import", ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Password plus authenticator or recovery code when enabled, as for enrolling an authenticator. Null when confirmed.</summary>
    private async Task<IActionResult?> ConfirmOwnerAsync(string? password, string? secondFactor, CancellationToken ct)
    {
        const string Required = "To include secrets, enter your password, and your authenticator or recovery code if you enabled one.";
        var owner = password is { Length: >= 1 and <= 1024 }
            ? await accounts.AuthenticateAsync(User.Identity!.Name!, password, ct) : null;
        if (owner is null || owner.SecurityStamp != User.FindFirstValue(OwnerHostingExtensions.StampClaim))
            return await DashboardErrorAsync(StatusCodes.Status401Unauthorized, Required, "export", ct);
        if (owner.TotpSecret is null) return null;
        try
        {
            if (await accounts.VerifySecondFactorAsync(owner.SecurityStamp, secondFactor, ct)) return null;
        }
        catch (OwnerAuthenticatorUnavailableException)
        {
            return await DashboardErrorAsync(StatusCodes.Status401Unauthorized,
                "This server cannot read your authenticator. Use a recovery code, or run sbox-ns admin reset-2fa on the server.", "export", ct);
        }
        return await DashboardErrorAsync(StatusCodes.Status401Unauthorized, Required, "export", ct);
    }

    private async Task<IActionResult> DashboardErrorAsync(int status, string error, string? openDialog, CancellationToken ct)
    {
        Response.StatusCode = status;
        if (openDialog is not null) ViewData["OpenDialog"] = openDialog;
        var cards = await OwnerProjectCards.LoadAsync(workspace, store, Owner, ct);
        return View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(cards,
            OwnerDashboardModel.SshHostFor(config, Request), error, null, openDialog, null, null, cards.Count));
    }
}
