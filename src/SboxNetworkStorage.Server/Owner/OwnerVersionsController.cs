using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerVersionResource(string Kind, string Id, string Name, int Count);
public sealed record OwnerDiffLine(char Op, string Text);
public sealed record OwnerVersionsModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerVersionResource> Resources,
    string? Kind, string? Id, IReadOnlyList<ResourceVersionEntry> Versions, ResourceVersionEntry? Selected,
    string? SelectedText, string? CurrentText, IReadOnlyList<OwnerDiffLine>? Diff, string? Error = null, bool Restored = false);

/// <summary>
/// Version history for endpoints and workflows. Every save (dashboard, Sync Tool PUT, PATCH, sync push, restore)
/// records a snapshot through <see cref="ManagementProjectObjects.RecordVersionAsync"/>; this page lists, compares
/// and restores them.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerVersionsController(INetworkStorageProjectService projects, INetworkStorageStore store,
    ManagementMutationHandler mutations, IAuditLogger audit) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/versions";
    private const int MaxDiffLines = 2000;

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? kind, [FromQuery] string? id, [FromQuery] string? v,
        [FromQuery] bool restored, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, v, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Versions.cshtml", model with { Restored = restored });
    }

    [HttpPost(Route + "/restore")]
    public async Task<IActionResult> Restore(string projectId, [FromForm] string? kind, [FromForm] string? id, [FromForm] string? v, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, v, ct);
        if (model?.Selected is null || model.Kind is null || model.Id is null) return NotFound();
        var definition = JsonNode.Parse(model.Selected.Definition.GetRawText())!.AsObject();
        definition["id"] = model.Id;
        var result = await mutations.SaveOwnerResourceAsync(OwnerProjectScope.Owner, projectId, model.Kind,
            JsonSerializer.SerializeToElement(definition), ct, versionSource: "restore");
        if (result.StatusCode >= 400)
        {
            Response.StatusCode = result.StatusCode;
            return View("~/Views/Owner/Versions.cshtml", model with { Error = JsonSerializer.Serialize(result.Body) });
        }
        await OwnerProjectScope.AuditAsync(audit, projectId, "resource.restore", new { kind = model.Kind, id = model.Id, version = model.Selected.Name }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/versions?kind={model.Kind}&id={Uri.EscapeDataString(model.Id)}&restored=true");
    }

    /// <summary>Source text when the definition was authored as YAML/JSON source, otherwise the definition rendered as YAML.</summary>
    public static string DisplayText(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty("sourceText", out var source)
           && source.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(source.GetString())
            ? source.GetString()!
            : OwnerYamlDefinitions.ToYaml(definition);

    private async Task<OwnerVersionsModel?> LoadAsync(string projectId, string? kind, string? id, string? versionName, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var endpointRows = await store.ListEndpointsAsync(projectId, ct);
        var workflowRows = await store.ListWorkflowsAsync(projectId, ct);
        var resources = new List<OwnerVersionResource>();
        foreach (var (rowKind, rows) in new[] { ("endpoint", endpointRows), ("workflow", workflowRows) })
        {
            foreach (var row in rows)
            {
                if (OwnerProjectScope.Text(row, rowKind + "_id") is not { } rowId) continue;
                var count = await ManagementProjectObjects.CountVersionsAsync(store, OwnerProjectScope.Owner, projectId, rowKind, rowId, ct);
                resources.Add(new OwnerVersionResource(rowKind, rowId,
                    OwnerProjectScope.Text(row, rowKind == "endpoint" ? "slug" : "name") ?? rowId, count));
            }
        }
        var empty = new OwnerVersionsModel(projectId, project.Name, resources, null, null, [], null, null, null, null);
        if (kind is not ("endpoint" or "workflow") || id is null) return empty;
        if (!resources.Any(resource => resource.Kind == kind && resource.Id == id)) return null;

        var history = await ManagementProjectObjects.ListVersionsAsync(store, OwnerProjectScope.Owner, projectId, kind, id, ct);
        var currentRow = (kind == "endpoint" ? endpointRows : workflowRows).First(row => OwnerProjectScope.Text(row, kind + "_id") == id);
        var current = OwnerProjectScope.JsonColumn(currentRow, "definition_json");
        var currentText = current is { } live ? DisplayText(live) : null;
        var selected = versionName is null ? history.FirstOrDefault() : history.FirstOrDefault(entry => entry.Name == versionName);
        if (versionName is not null && selected is null) return null;
        var selectedText = selected is null ? null : DisplayText(selected.Definition);
        return empty with
        {
            Kind = kind, Id = id, Versions = history, Selected = selected, SelectedText = selectedText, CurrentText = currentText,
            Diff = selectedText is null || currentText is null ? null : Diff(selectedText, currentText),
        };
    }

    /// <summary>Line diff (longest common subsequence) from the selected version to the live definition.</summary>
    private static IReadOnlyList<OwnerDiffLine>? Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var b = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines) return null;
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = a[i] == b[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var lines = new List<OwnerDiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { lines.Add(new(' ', a[x])); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) lines.Add(new('-', a[x++]));
            else lines.Add(new('+', b[y++]));
        }
        while (x < a.Length) lines.Add(new('-', a[x++]));
        while (y < b.Length) lines.Add(new('+', b[y++]));
        return lines;
    }
}
