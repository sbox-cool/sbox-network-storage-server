using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerPage(string Slug, string Title, string Type, string Content, string? CreatedAt, string? UpdatedAt);

/// <param name="BaseUrl">server.public_url, or the origin the dashboard was opened with (<see cref="ServerBaseUrl.ForRequest"/>).</param>
public sealed record OwnerPagesModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerPage> Pages, OwnerPage? Editing,
    string BaseUrl, string? Error = null)
{
    /// <summary>Absolute public URL of a page.</summary>
    public string PublicUrl(string slug) => $"{BaseUrl}/pages/{Uri.EscapeDataString(ProjectId)}/{Uri.EscapeDataString(slug)}";
}

/// <summary>
/// Published pages served publicly at <c>/pages/{project}/{slug}</c> (and <c>/api/pages/...</c>) by
/// <c>PagesHandler</c>: markdown pages or key/value data pages, e.g. patch notes or a message of the day.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed partial class OwnerPagesController(INetworkStorageProjectService projects, INetworkStorageStore store,
    IWorkspaceStore workspace, IAuditLogger audit, EffectiveConfig config) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/pages";
    private const int MaxContentLength = 100_000;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.None, 100)]
    private static partial Regex SlugPattern();

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? slug, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        return View("~/Views/Owner/Pages.cshtml", model with { Editing = model.Pages.FirstOrDefault(page => page.Slug == slug) });
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, [FromForm] string? originalSlug, [FromForm] string? slug, [FromForm] string? title,
        [FromForm] string? type, [FromForm] string? content, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var page = new OwnerPage((slug ?? "").Trim(), (title ?? "").Trim(), type == "keyvalue" ? "keyvalue" : "markdown", content ?? "", null, null);
        model = model with { Editing = page };
        if (!SlugPattern().IsMatch(page.Slug)) return Invalid(model, "Slug may contain lowercase letters, numbers, hyphens and underscores (maximum 64).");
        if (page.Title.Length is < 1 or > 120) return Invalid(model, "Give the page a title of 1 to 120 characters.");
        if (page.Content.Length > MaxContentLength) return Invalid(model, $"Page content is limited to {MaxContentLength:N0} characters.");
        JsonElement? data = null;
        if (page.Type == "keyvalue")
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(page.Content) ? "{}" : page.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid(model, "Key/value pages need a JSON object, for example {\"motd\": \"Welcome\"}.");
                data = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                return Invalid(model, $"Page data is not valid JSON: {error.Message}");
            }
        }
        var previous = model.Pages.FirstOrDefault(existing => existing.Slug == originalSlug);
        if (page.Slug != originalSlug && model.Pages.Any(existing => existing.Slug == page.Slug))
            return Invalid(model, $"A page with slug {page.Slug} already exists.");

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var pageDocument = new Dictionary<string, object?>
        {
            ["id"] = page.Slug, ["slug"] = page.Slug, ["title"] = page.Title, ["pageType"] = page.Type,
            ["createdAt"] = previous?.CreatedAt ?? now, ["updatedAt"] = now,
        };
        if (data is { } values) pageDocument["kvData"] = values;
        else pageDocument["contentMd"] = page.Content;

        // The public route resolves the owning user through the page index before reading page content.
        await workspace.PutRawAsync($"network-storage/page-index/{Uri.EscapeDataString(projectId)}.json",
            new Dictionary<string, object> { ["userId"] = OwnerProjectScope.Owner, ["projectId"] = projectId }, ct);
        await workspace.PutProjectResourceAsync(OwnerProjectScope.Owner, projectId, $"pages/{page.Slug}.json", pageDocument, ct);
        if (previous is not null && previous.Slug != page.Slug) await DeletePageAsync(projectId, previous.Slug, ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "page.save", new { slug = page.Slug }, ct);
        OwnerFlash.Success(this, "Page saved.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/pages?slug={Uri.EscapeDataString(page.Slug)}");
    }

    [HttpPost(Route + "/delete")]
    public async Task<IActionResult> Delete(string projectId, [FromForm] string? slug, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        if (!model.Pages.Any(page => page.Slug == slug)) return NotFound();
        if (!OwnerConfirm.IsConfirmed(Request)) return OwnerConfirm.Page(this);
        await DeletePageAsync(projectId, slug!, ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "page.delete", new { slug }, ct);
        OwnerFlash.Success(this, "Page deleted.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/pages");
    }

    private Task DeletePageAsync(string projectId, string slug, CancellationToken ct)
        => workspace.DeleteRawAsync($"network-storage/users/{OwnerProjectScope.Owner.ToString(CultureInfo.InvariantCulture)}/{projectId}/pages/{slug}.json", ct);

    private IActionResult Invalid(OwnerPagesModel model, string error)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("~/Views/Owner/Pages.cshtml", model with { Error = error });
    }

    private async Task<OwnerPagesModel?> LoadAsync(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var pages = new List<OwnerPage>();
        foreach (var row in await store.ListPagesAsync(projectId, ct))
        {
            if (OwnerProjectScope.Text(row, "page_slug") is not { } slug) continue;
            var content = OwnerProjectScope.JsonColumn(row, "content_json") ?? default;
            var type = OwnerProjectScope.Text(content, "pageType") == "keyvalue" ? "keyvalue" : "markdown";
            var text = type == "keyvalue"
                ? content.TryGetProperty("kvData", out var values) ? JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }) : "{}"
                : OwnerProjectScope.Text(content, "contentMd") ?? "";
            pages.Add(new OwnerPage(slug, OwnerProjectScope.Text(content, "title") ?? OwnerProjectScope.Text(row, "title") ?? slug, type, text,
                OwnerProjectScope.Text(content, "createdAt"), OwnerProjectScope.Text(content, "updatedAt")));
        }
        return new OwnerPagesModel(projectId, project.Name, pages, null, ServerBaseUrl.ForRequest(config, Request));
    }
}
