using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Owner-only download of the same archive <c>sbox-ns export</c> writes. Secrets are
/// excluded unless the owner opts in; every download is audit-logged per project.
/// Antiforgery is enforced by the global <c>AutoValidateAntiforgeryToken</c> filter.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerExportController(INetworkStorageStore store, INetworkStorageStoreAdmin admin, EffectiveConfig config,
    IBunnyWorkspaceClient workspace, IAuditLogger audit, ILogger<OwnerExportController> logger) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    /// <summary>One export at a time: each one stages a full dump on disk.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [HttpPost("/dashboard/export")]
    public async Task<IActionResult> Export([FromForm] bool includeSecrets, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return await DashboardErrorAsync(StatusCodes.Status409Conflict, "An export is already running. Try again when it has finished.", ct);
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
                return await DashboardErrorAsync(StatusCodes.Status500InternalServerError, ex.Message, ct);
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

    private async Task<IActionResult> DashboardErrorAsync(int status, string error, CancellationToken ct)
    {
        Response.StatusCode = status;
        return View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(await workspace.GetUserProjectsAsync(Owner, ct), error));
    }
}
