using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerResourceItem(string Id, string Name);

/// <summary>How game code calls the open resource, and links to the pages that work with it.</summary>
/// <param name="Snippet">C# for game code, or null when game clients cannot call the resource directly.</param>
/// <param name="Note">A sentence shown with (or instead of) the snippet.</param>
public sealed record OwnerResourceUsage(string? Snippet, string? Note, string? TestUrl, string? VersionsUrl, string? DataUrl);

public sealed record OwnerResourcesModel(string ProjectId, string ProjectName, string Kind,
    IReadOnlyList<OwnerResourceItem> Items, string? Id, string Definition, string BuilderContext,
    string? Error = null, IReadOnlyList<DefinitionDiagnostic>? Diagnostics = null, OwnerResourceUsage? Usage = null);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerResourcesController(INetworkStorageProjectService projects, INetworkStorageStore store,
    ManagementMutationHandler mutations, IAuditLogger audit) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Root = "/dashboard/projects/{projectId}/resources";
    private const string Route = Root + "/{kind}";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [HttpGet(Route)]
    public async Task<IActionResult> Editor(string projectId, string kind, [FromQuery] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        return loaded is null ? NotFound() : View("~/Views/Owner/Resources.cshtml", loaded.Value.Model);
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        if (loaded is null) return NotFound();
        var (model, resources) = loaded.Value;
        try
        {
            var resource = OwnerResourceSource.Parse(kind, definition, id, store.MaxPayloadBytes, out var resourceId);
            var (diagnostics, result) = await OwnerResourceSource.SaveAsync(mutations, audit, "owner-dashboard", projectId, kind, resource, resourceId, resources, false, ct);
            if (diagnostics.Any(item => item.IsError))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return View("~/Views/Owner/Resources.cshtml", model with { Definition = definition!, Diagnostics = diagnostics });
            }
            if (result!.StatusCode >= 400)
            {
                Response.StatusCode = result.StatusCode;
                return View("~/Views/Owner/Resources.cshtml", model with { Definition = definition!, Error = JsonSerializer.Serialize(result.Body, Pretty) });
            }
            OwnerFlash.Success(this, "Resource saved.");
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}" + (resourceId is null ? "" : "?id=" + Uri.EscapeDataString(resourceId)));
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Resources.cshtml", model with { Definition = definition ?? "", Error = error.Message });
        }
    }

    /// <summary>Live diagnostics for the editor: parses the text and runs the save-time validator without saving.</summary>
    [HttpPost(Route + "/check")]
    public async Task<IActionResult> Check(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        if (loaded is null) return NotFound();
        var diagnostics = OwnerResourceSource.Check(kind, definition, id, loaded.Value.Resources, store.MaxPayloadBytes, out var source, out _);
        return Json(new { ok = !diagnostics.Any(item => item.IsError), diagnostics, source }, Web);
    }

    /// <summary>The examples gallery and step palette defaults, with which example resources already exist.</summary>
    [HttpGet(Root + "/catalog")]
    public async Task<IActionResult> Catalog(string projectId, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, "endpoint", null, ct);
        if (loaded is null) return NotFound();
        var resources = loaded.Value.Resources;
        var existing = new[] { "collection", "endpoint", "workflow", "query", "game-values" }
            .ToDictionary(kind => kind, resources.ExistingIds);
        return Json(new
        {
            examples = OwnerResourceExamples.Catalog.Select(example => new
            {
                example.Id, example.Kind, example.Category, example.Title, example.Summary, example.Requires, example.Source,
                calls = example.Calls.Select(call => new { call.Method, call.Input, call.Secret, call.Status }),
                exists = ExampleExists(example, existing),
            }),
            stepDefaults = OwnerResourceExamples.StepDefaults,
            stepYaml = OwnerResourceExamples.StepYaml,
        }, Web);
    }

    /// <summary>Creates the companion definitions an example needs (never overwrites existing resources).</summary>
    [HttpPost(Root + "/catalog/{exampleId}/companions")]
    public async Task<IActionResult> CreateCompanions(string projectId, string exampleId, CancellationToken ct)
    {
        var example = OwnerResourceExamples.Find(exampleId);
        var loaded = await LoadAsync(projectId, "endpoint", null, ct);
        if (loaded is null || example is null) return NotFound();
        var created = new List<string>();
        var skipped = new List<string>();
        var failed = new List<object>();
        foreach (var companion in OwnerResourceExamples.WithCompanions(example).Where(item => item.Id != example.Id))
        {
            var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
            if (ExampleExists(companion, null, resources))
            {
                skipped.Add(companion.Id);
                continue;
            }
            var resource = companion.Kind == "game-values"
                ? MergeGameValues(resources.GameValues, OwnerYamlDefinitions.ParseDefinition(companion.Source, out _))
                : OwnerResourceSource.Wrap(companion.ResourceId, companion.Source, companion.Kind);
            var (diagnostics, result) = await OwnerResourceSource.SaveAsync(mutations, audit, "owner-dashboard", projectId, companion.Kind,
                resource, companion.ResourceId, resources, false, ct);
            var errors = diagnostics.Where(item => item.IsError).Select(item => item.Message).ToList();
            if (errors.Count > 0 || result is null || result.StatusCode >= 400)
                failed.Add(new { id = companion.Id, message = errors.Count > 0 ? string.Join(" ", errors) : JsonSerializer.Serialize(result?.Body) });
            else created.Add(companion.Id);
        }
        return Json(new { created, skipped, failed }, Web);
    }

    private async Task<(OwnerResourcesModel Model, OwnerProjectResources Resources)?> LoadAsync(string projectId, string kind, string? id, CancellationToken ct)
    {
        if (!OwnerResourceSource.IsKind(kind)) return null;
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var rows = resources.Rows(kind);
        var items = rows.Select(row => new OwnerResourceItem(Text(row, kind + "_id") ?? "", Text(row, "name") ?? Text(row, "slug") ?? Text(row, kind + "_id") ?? "")).OrderBy(item => item.Name).ToList();
        if (id is not null && kind != "game-values" && !items.Any(item => item.Id == id)) return null;
        var row = rows.FirstOrDefault(candidate => Text(candidate, kind + "_id") == id);
        var value = kind == "game-values" ? resources.GameValues
            : row.ValueKind == JsonValueKind.Object ? OwnerProjectResources.Column(row, "definition_json") : null;
        var display = value is { ValueKind: JsonValueKind.Object } payload ? OwnerResourceSource.DisplayText(payload) : OwnerResourceExamples.Skeletons[kind];
        var usage = id is null || row.ValueKind != JsonValueKind.Object ? null : Usage(projectId, kind, id, row, value);
        var model = new OwnerResourcesModel(projectId, access.Project.Name, kind, items, id, display, resources.BuilderJson(), Usage: usage);
        return (model, resources);
    }

    private static OwnerResourceUsage? Usage(string projectId, string kind, string id, JsonElement row, JsonElement? definition)
    {
        var projectUrl = OwnerProjectScope.ProjectUrl(projectId);
        switch (kind)
        {
            case "endpoint":
            {
                var slug = Text(row, "slug") ?? id;
                var note = OwnerGameSnippets.RequiresSecretKey(definition)
                    ? "This endpoint requires a secret key: call it from a dedicated server, never from the game client."
                    : "s&box hides 4xx response bodies from game code, so a rejected call returns no value and the error code can read HTTP_ERROR. The Logs tab shows the status.";
                return new OwnerResourceUsage(OwnerGameSnippets.EndpointCall(slug, definition), note,
                    $"{projectUrl}/tests?endpoint={Uri.EscapeDataString(slug)}#try-it",
                    $"{projectUrl}/versions?kind=endpoint&id={Uri.EscapeDataString(id)}", null);
            }
            case "workflow":
                return new OwnerResourceUsage(null, null, null, $"{projectUrl}/versions?kind=workflow&id={Uri.EscapeDataString(id)}", null);
            case "collection":
            {
                var global = OwnerDataRecords.Describe(row)?.Global == true;
                var dataUrl = OwnerDataController.CollectionUrl(projectId, id);
                return OwnerGameSnippets.IsPublicCollection(definition)
                    ? new OwnerResourceUsage(OwnerGameSnippets.CollectionDocuments(id, global, definition),
                        global ? null : "With s&box authentication on, players may only write their own document: the key is their Steam ID, or starts with {steamId}_ for save slots.",
                        null, null, dataUrl)
                    : new OwnerResourceUsage(null,
                        "Game clients cannot read or write this collection directly (accessMode is not public). Call an endpoint that reads or writes it; direct calls answer 403 ENDPOINT_ONLY.",
                        null, null, dataUrl);
            }
            default:
                return null;
        }
    }

    private static bool ExampleExists(OwnerResourceExample example, Dictionary<string, HashSet<string>>? existing, OwnerProjectResources? resources = null)
    {
        if (example.Kind != "game-values")
            return (existing?[example.Kind] ?? resources!.ExistingIds(example.Kind)).Contains(example.ResourceId);
        var ids = existing?["game-values"] ?? resources!.ExistingIds("game-values");
        var source = OwnerYamlDefinitions.ParseDefinition(example.Source, out _);
        return source.GetProperty("items").EnumerateArray().All(item => ids.Contains(item.GetProperty("id").GetString()!));
    }

    /// <summary>Adds the example's game value items that the stored document lacks; legacy groups/tables become items.</summary>
    private static JsonElement MergeGameValues(JsonElement? stored, JsonElement additions)
    {
        var document = stored is { ValueKind: JsonValueKind.Object } current
            ? (JsonObject)JsonNode.Parse(current.GetRawText())!
            : new JsonObject();
        // Game values are stored parsed; drop source metadata left by older dashboard saves.
        foreach (var field in new[] { "sourceText", "sourceFormat", "sourcePath", "authoringMode", "sourceVersion" })
            document.Remove(field);
        if (document["items"] is not JsonArray items)
        {
            items = new JsonArray();
            foreach (var (name, type) in new[] { ("groups", "group"), ("tables", "table") })
            {
                if (document[name] is not JsonArray legacy) continue;
                foreach (var node in legacy)
                    if (node is JsonObject item)
                    {
                        var copy = (JsonObject)item.DeepClone();
                        copy["type"] = type;
                        items.Add(copy);
                    }
                document.Remove(name);
            }
            document["items"] = items;
        }
        var ids = items.OfType<JsonObject>()
            .Select(item => item["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in additions.GetProperty("items").EnumerateArray())
            if (ids.Add(item.GetProperty("id").GetString()))
                items.Add(JsonNode.Parse(item.GetRawText()));
        return JsonSerializer.SerializeToElement(document);
    }

    private static string? Text(JsonElement row, string name) => OwnerProjectResources.Text(row, name);
}
