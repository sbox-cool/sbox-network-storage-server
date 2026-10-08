using System.Globalization;
using System.Text;
using System.Text.Json;
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
    IReadOnlyList<OwnerResourceItem> Items, string? Id, string Definition, string? Error = null, bool Saved = false);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerResourcesController(INetworkStorageProjectService projects, INetworkStorageStore store,
    ManagementMutationCandidateHandler mutations, IAuditLogger audit) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Route = "/dashboard/projects/{projectId}/resources/{kind}";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [HttpGet(Route)]
    public async Task<IActionResult> Editor(string projectId, string kind, [FromQuery] string? id, [FromQuery] bool saved, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Resources.cshtml", model with { Saved = saved });
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, ct);
        if (model is null) return NotFound();
        try
        {
            if (string.IsNullOrWhiteSpace(definition)) throw new ArgumentException("Enter a resource definition before saving.");
            if (Encoding.UTF8.GetByteCount(definition) > store.MaxPayloadBytes) throw new ArgumentException($"Definition exceeds the store limit of {store.MaxPayloadBytes:N0} bytes.");
            using var document = JsonDocument.Parse(definition);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Definition must be a JSON object.");
            var resource = document.RootElement;
            var resourceId = kind == "game-values" ? null : Text(resource, "id") ?? Text(resource, kind == "endpoint" ? "slug" : "name");
            if (kind != "game-values" && (resourceId is null || !StorageIdValidation.IsValidCollectionId(resourceId)))
                throw new ArgumentException("Provide an id containing only letters, numbers, underscores or hyphens (maximum 128 characters). Keep the same id when editing.");
            if (id is not null && kind != "game-values" && id != resourceId) throw new ArgumentException("An existing resource's id cannot be changed. Create a new resource instead.");
            var result = await mutations.SaveOwnerResourceAsync(Owner, projectId, kind, resource, ct);
            if (result.StatusCode >= 400)
            {
                Response.StatusCode = result.StatusCode;
                return View("~/Views/Owner/Resources.cshtml", model with { Definition = definition, Error = JsonSerializer.Serialize(result.Body, Pretty) });
            }
            await audit.LogActionAsync(new AuditLogRequest(projectId, Owner.ToString(CultureInfo.InvariantCulture), "resource.save",
                new { type = "owner-dashboard" }, new { kind, id = resourceId }, new { kind, id = resourceId }, null, null), ct);
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}?saved=true" + (resourceId is null ? "" : "&id=" + Uri.EscapeDataString(resourceId)));
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Resources.cshtml", model with { Definition = definition ?? "", Error = error.Message });
        }
    }

    private async Task<OwnerResourcesModel?> LoadAsync(string projectId, string kind, string? id, CancellationToken ct)
    {
        if (kind is not ("collection" or "endpoint" or "workflow" or "query" or "game-values")) return null;
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        IReadOnlyList<JsonElement> rows = kind switch
        {
            "collection" => await store.ListCollectionsAsync(projectId, ct),
            "endpoint" => await store.ListEndpointsAsync(projectId, ct),
            "workflow" => await store.ListWorkflowsAsync(projectId, ct),
            "query" => await store.ListQueriesAsync(projectId, ct),
            _ => []
        };
        var items = rows.Select(row => new OwnerResourceItem(Text(row, kind + "_id") ?? "", Text(row, "name") ?? Text(row, "slug") ?? Text(row, kind + "_id") ?? "")).OrderBy(item => item.Name).ToList();
        JsonElement? selected = kind == "game-values" ? await store.ReadGameValuesAsync(projectId, ct)
            : rows.FirstOrDefault(row => Text(row, kind + "_id") == id);
        if (id is not null && kind != "game-values" && !items.Any(item => item.Id == id)) return null;
        var value = selected is { ValueKind: JsonValueKind.Object } rowValue ? Column(rowValue, kind == "game-values" ? "payload_json" : "definition_json") : null;
        var initial = kind switch
        {
            "collection" => "{\n  \"id\": \"player_stats\",\n  \"name\": \"player_stats\",\n  \"collectionType\": \"player\",\n  \"schema\": {}\n}",
            "endpoint" => "{\n  \"id\": \"health-check\",\n  \"name\": \"Health check\",\n  \"slug\": \"health-check\",\n  \"method\": \"GET\",\n  \"enabled\": true,\n  \"steps\": [],\n  \"response\": { \"ok\": true }\n}",
            "workflow" => "{\n  \"id\": \"player_setup\",\n  \"name\": \"Player setup\",\n  \"steps\": []\n}",
            "query" => "{\n  \"id\": \"leaderboard\",\n  \"name\": \"Leaderboard\",\n  \"type\": \"list\",\n  \"sources\": [{ \"collectionId\": \"player_stats\" }],\n  \"config\": { \"limit\": 20 },\n  \"cache\": { \"ttlSeconds\": 60 }\n}",
            _ => "{}"
        };
        return new OwnerResourcesModel(projectId, access.Project.Name, kind, items, id, value is { } payload ? JsonSerializer.Serialize(payload, Pretty) : initial);
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static JsonElement? Column(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return value.Clone();
        using var document = JsonDocument.Parse(value.GetString() ?? "null");
        return document.RootElement.Clone();
    }
}
