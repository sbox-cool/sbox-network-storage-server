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
public sealed record OwnerResourcesModel(string ProjectId, string ProjectName, string Kind,
    IReadOnlyList<OwnerResourceItem> Items, string? Id, string Definition, string BuilderContext,
    string? Error = null, bool Saved = false, IReadOnlyList<DefinitionDiagnostic>? Diagnostics = null);

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
    public async Task<IActionResult> Editor(string projectId, string kind, [FromQuery] string? id, [FromQuery] bool saved, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        return loaded is null ? NotFound() : View("~/Views/Owner/Resources.cshtml", loaded.Value.Model with { Saved = saved });
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        if (loaded is null) return NotFound();
        var (model, resources) = loaded.Value;
        try
        {
            var resource = ParseForSave(kind, definition, id, out var resourceId);
            var (diagnostics, result) = await SaveAsync(projectId, kind, resource, resourceId, resources, ct);
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
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}?saved=true" + (resourceId is null ? "" : "&id=" + Uri.EscapeDataString(resourceId)));
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
        JsonElement? source = null;
        IReadOnlyList<DefinitionDiagnostic> diagnostics;
        try
        {
            if (!string.IsNullOrWhiteSpace(definition)) source = OwnerYamlDefinitions.ParseDefinition(definition, out _);
            var resource = ParseForSave(kind, definition, id, out var resourceId);
            diagnostics = NetworkStorageDefinitionValidator.ValidateResource(resource, kind, loaded.Value.Resources.ValidationContext(resourceId), out _);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            diagnostics = [new DefinitionDiagnostic("error", "INVALID_DEFINITION", error.Message, "/")];
        }
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
                : WrapDashboardSource(companion.ResourceId, companion.Source, companion.Kind);
            var (diagnostics, result) = await SaveAsync(projectId, companion.Kind, resource, companion.ResourceId, resources, ct);
            var errors = diagnostics.Where(item => item.IsError).Select(item => item.Message).ToList();
            if (errors.Count > 0 || result is null || result.StatusCode >= 400)
                failed.Add(new { id = companion.Id, message = errors.Count > 0 ? string.Join(" ", errors) : JsonSerializer.Serialize(result?.Body) });
            else created.Add(companion.Id);
        }
        return Json(new { created, skipped, failed }, Web);
    }

    private async Task<(IReadOnlyList<DefinitionDiagnostic> Diagnostics, NetworkStorageResult? Result)> SaveAsync(
        string projectId, string kind, JsonElement resource, string? resourceId, OwnerProjectResources resources, CancellationToken ct)
    {
        var diagnostics = NetworkStorageDefinitionValidator.ValidateResource(resource, kind, resources.ValidationContext(resourceId), out _);
        if (diagnostics.Any(item => item.IsError)) return (diagnostics, null);
        var result = await mutations.SaveOwnerResourceAsync(Owner, projectId, kind, resource, ct);
        if (result.StatusCode < 400)
            await audit.LogActionAsync(new AuditLogRequest(projectId, Owner.ToString(CultureInfo.InvariantCulture), "resource.save",
                new { type = "owner-dashboard" }, new { kind, id = resourceId }, new { kind, id = resourceId }, null, null), ct);
        return (diagnostics, result);
    }

    private JsonElement ParseForSave(string kind, string? definition, string? id, out string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(definition)) throw new ArgumentException("Enter a resource definition before saving.");
        if (Encoding.UTF8.GetByteCount(definition) > store.MaxPayloadBytes) throw new ArgumentException($"Definition exceeds the store limit of {store.MaxPayloadBytes:N0} bytes.");
        var parsed = OwnerYamlDefinitions.ParseDefinition(definition, out var wasJson);
        resourceId = null;
        // Game values are one document per project; the parsed object is stored as-is.
        if (kind == "game-values") return parsed;
        resourceId = Text(parsed, "id") ?? Text(parsed, kind == "endpoint" ? "slug" : "name");
        if (resourceId is null || !StorageIdValidation.IsValidCollectionId(resourceId))
            throw new ArgumentException("Provide an id containing only letters, numbers, underscores or hyphens (maximum 128 characters). Keep the same id when editing.");
        if (id is not null && id != resourceId) throw new ArgumentException("An existing resource's id cannot be changed. Create a new resource instead.");
        return wasJson ? parsed : WrapDashboardSource(resourceId, definition, kind);
    }

    private async Task<(OwnerResourcesModel Model, OwnerProjectResources Resources)?> LoadAsync(string projectId, string kind, string? id, CancellationToken ct)
    {
        if (kind is not ("collection" or "endpoint" or "workflow" or "query" or "game-values")) return null;
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var rows = resources.Rows(kind);
        var items = rows.Select(row => new OwnerResourceItem(Text(row, kind + "_id") ?? "", Text(row, "name") ?? Text(row, "slug") ?? Text(row, kind + "_id") ?? "")).OrderBy(item => item.Name).ToList();
        if (id is not null && kind != "game-values" && !items.Any(item => item.Id == id)) return null;
        var value = kind == "game-values" ? resources.GameValues
            : rows.Where(row => Text(row, kind + "_id") == id).Select(row => OwnerProjectResources.Column(row, "definition_json")).FirstOrDefault();
        string display;
        if (value is { ValueKind: JsonValueKind.Object } payload)
        {
            display = payload.TryGetProperty("sourceText", out var source) && source.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(source.GetString())
                ? source.GetString()!
                : OwnerYamlDefinitions.ToYaml(payload);
        }
        else display = OwnerResourceExamples.Skeletons[kind];
        var model = new OwnerResourcesModel(projectId, access.Project.Name, kind, items, id, display, resources.BuilderJson());
        return (model, resources);
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

    private static JsonElement WrapDashboardSource(string id, string sourceText, string kind) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sourceText"] = sourceText,
            ["sourceFormat"] = "yaml",
            ["sourcePath"] = $"{kind}/{id}.yml",
            ["authoringMode"] = "dashboard",
            ["sourceVersion"] = 1,
        });
}
