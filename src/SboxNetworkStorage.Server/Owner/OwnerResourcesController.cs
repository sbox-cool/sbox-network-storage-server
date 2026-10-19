using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
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
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerResourceItem(string Id, string Name);

/// <summary>One tab panel in the resource integration guide: a copyable code block or agent prompt.</summary>
/// <param name="Id">Panel id, matched by its tab button's data-tab attribute.</param>
/// <param name="Label">Tab button text.</param>
/// <param name="Language">Highlight language (csharp, yaml, json) or null for a plain-text agent prompt.</param>
/// <param name="Code">Exact text shown and copied.</param>
public sealed record OwnerIntegrationPanel(string Id, string Label, string? Language, string Code);

/// <summary>
/// Tabbed integration guide for the open resource: the setup line, copyable game code and a prepared
/// agent prompt, all naming the live project and resource. Rendered by _ResourceIntegration.cshtml,
/// which follows the parent tabs contract (container data-tabs, buttons role=tab data-tab, panels
/// role=tabpanel) and the highlighter contract (pre data-language for code, plain pre for prompts).
/// Agent prompt blocks carry data-prompt-source="definition" so the workbench can refresh them from
/// the editor textarea. Never carries a secret key.
/// </summary>
public sealed record OwnerResourceIntegration(string ProjectId, string Kind, string? ResourceId, string Heading,
    string? Intro, string? Configure, string? SetupNote, IReadOnlyList<OwnerIntegrationPanel> Panels,
    string? TestUrl, string? VersionsUrl, string? DataUrl);

public sealed record OwnerResourcesModel(string ProjectId, string ProjectName, string Kind,
    IReadOnlyList<OwnerResourceItem> Items, string? Id, string Definition, string BuilderContext,
    string? Error = null, IReadOnlyList<DefinitionDiagnostic>? Diagnostics = null,
    OwnerResourceIntegration? Integration = null);
/// <summary>Per-endpoint usage counters for the current UTC month.</summary>
public sealed record OwnerEndpointUsage(long Calls, long Errors, long DurationMsSum);

/// <summary>One row of the resource list table with kind facts and stats precomputed.</summary>
public sealed record OwnerResourceRow(string Id, string Name,
    string? Method, bool? Enabled, int Steps, string? Access, string? Slug,
    long Calls, long Errors, long DurationMsSum,
    string? Storage, string? ClientAccess, int Fields, long? Records,
    string? Parameters, int WorkflowSteps,
    string? QueryType, string? Sources, string? LastRun, long? DurationMs, int? RecordsReturned);

/// <summary>Resource list page: one row per saved resource plus the stats month.</summary>
public sealed record OwnerResourceListModel(string ProjectId, string ProjectName, string Kind,
    IReadOnlyList<OwnerResourceRow> Rows, string Month);

/// <summary>Resource detail page: header, tab, editor state and overview facts.</summary>
public sealed record OwnerResourceDetailModel(string ProjectId, string ProjectName, string Kind,
    string? Id, string Name, string Definition, string BuilderContext,
    string Tab, bool IsDraft,
    OwnerResourceIntegration? Integration,
    string? Error = null, IReadOnlyList<DefinitionDiagnostic>? Diagnostics = null,
    OwnerResourceRow? Row = null, JsonElement? DefinitionJson = null);

/// <summary>Quick preview modal body: header, stats, outline, snippet and links.</summary>
public sealed record OwnerResourcePreviewModel(string ProjectId, string Kind, string Id, string Name,
    OwnerResourceRow Row, OwnerResourceIntegration? Integration, string Definition, JsonElement? DefinitionJson = null);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerResourcesController(INetworkStorageProjectService projects, INetworkStorageStore store,
    ManagementMutationHandler mutations, IAuditLogger audit, EffectiveConfig config) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Root = "/dashboard/projects/{projectId}/resources";
    private const string Route = Root + "/{kind}";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [HttpGet(Route)]
    public async Task<IActionResult> List(string projectId, string kind, [FromQuery] string? id, [FromQuery] string? draft, [FromQuery] string? tab, CancellationToken ct)
    {
        if (!OwnerResourceSource.IsKind(kind)) return NotFound();
        if (kind == "game-values")
        {
            var gameDetail = await LoadDetailAsync(projectId, kind, null, tab, null, ct);
            return gameDetail is null ? NotFound() : View("~/Views/Owner/Resource.cshtml", gameDetail);
        }
        if (!string.IsNullOrEmpty(id))
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}/{Uri.EscapeDataString(id)}");
        if (draft is not null)
        {
            if (draft.Length is < 1 or > 128 || draft.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
                return BadRequest("Draft ids need 1-128 letters, numbers, underscores or hyphens.");
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}/new?draft={Uri.EscapeDataString(draft)}");
        }
        var model = await LoadListAsync(projectId, kind, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Resources.cshtml", model);
    }

    [HttpGet(Root + "/{kind}/new")]
    public async Task<IActionResult> New(string projectId, string kind, [FromQuery] string? draft, [FromQuery] string? tab, CancellationToken ct)
    {
        if (!OwnerResourceSource.IsKind(kind) || kind == "game-values") return NotFound();
        if (string.IsNullOrEmpty(draft))
        {
            var existing = await LoadDetailAsync(projectId, kind, "new", tab, null, ct);
            if (existing is not null) return View("~/Views/Owner/Resource.cshtml", existing);
            return BadRequest("Add ?draft=<id> with 1-128 letters, numbers, underscores or hyphens.");
        }
        if (draft.Length is < 1 or > 128 || draft.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            return BadRequest("Draft ids need 1-128 letters, numbers, underscores or hyphens.");
        var detail = await LoadDetailAsync(projectId, kind, null, tab, draft, ct);
        return detail is null ? NotFound() : View("~/Views/Owner/Resource.cshtml", detail);
    }

    [HttpGet(Root + "/{kind}/{resourceId}")]
    public async Task<IActionResult> Detail(string projectId, string kind, string resourceId, [FromQuery] string? tab, CancellationToken ct)
    {
        if (!OwnerResourceSource.IsKind(kind)) return NotFound();
        if (kind == "game-values") return NotFound();
        var detail = await LoadDetailAsync(projectId, kind, resourceId, tab, null, ct);
        return detail is null ? NotFound() : View("~/Views/Owner/Resource.cshtml", detail);
    }

    [HttpGet(Root + "/{kind}/{resourceId}/preview")]
    public async Task<IActionResult> Preview(string projectId, string kind, string resourceId, CancellationToken ct)
    {
        if (!OwnerResourceSource.IsKind(kind) || kind == "game-values") return NotFound();
        if (string.Equals(resourceId, "new", StringComparison.OrdinalIgnoreCase)) return NotFound();
        var preview = await LoadPreviewAsync(projectId, kind, resourceId, ct);
        return preview is null ? NotFound() : PartialView("~/Views/Owner/_ResourcePreview.cshtml", preview);
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        if (loaded is null) return NotFound();
        var (_, resources) = loaded.Value;
        try
        {
            var resource = OwnerResourceSource.Parse(kind, definition, id, store.MaxPayloadBytes, out var resourceId);
            var (diagnostics, result) = await OwnerResourceSource.SaveAsync(mutations, audit, "owner-dashboard", projectId, kind, resource, resourceId, resources, HttpContext.RequestServices.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>(), false, ct);
            if (diagnostics.Any(item => item.IsError))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                var failed = await LoadDetailAsync(projectId, kind, resourceId ?? id, "edit", null, ct, definition, null, diagnostics);
                if (failed is not null) return View("~/Views/Owner/Resource.cshtml", failed);
                return BadRequest("Could not save this definition.\n" + definition);
            }
            if (result!.StatusCode >= 400)
            {
                Response.StatusCode = result.StatusCode;
                var failed = await LoadDetailAsync(projectId, kind, resourceId ?? id, "edit", null, ct, definition, JsonSerializer.Serialize(result.Body, Pretty), null);
                if (failed is not null) return View("~/Views/Owner/Resource.cshtml", failed);
                return BadRequest("Could not save this definition.\n" + definition);
            }
            OwnerFlash.Success(this, "Resource saved.");
            if (kind == "game-values" || resourceId is null)
                return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}?tab=edit");
            return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/resources/{kind}/{Uri.EscapeDataString(resourceId)}?tab=edit");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            var failed = await LoadDetailAsync(projectId, kind, id, "edit", null, ct, definition ?? "", error.Message, null);
            if (failed is not null) return View("~/Views/Owner/Resource.cshtml", failed);
            return BadRequest("Could not save this definition: " + error.Message + "\n" + (definition ?? ""));
        }
    }

    /// <summary>Live diagnostics for the editor: parses the text and runs the save-time validator without saving.</summary>
    [HttpPost(Route + "/check")]
    public async Task<IActionResult> Check(string projectId, string kind, [FromForm] string? definition, [FromForm] string? id, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, kind, id, ct);
        if (loaded is null) return NotFound();
        var diagnostics = OwnerResourceSource.Check(kind, definition, id, loaded.Value.Resources, store.MaxPayloadBytes, HttpContext.RequestServices.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>(), out var source, out _);
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

    /// <summary>TypeScript declarations for this project's endpoint scripts: the shipped ns.d.ts plus the project's own names.</summary>
    [HttpGet(Root + "/endpoint-typings")]
    public async Task<IActionResult> EndpointTypingsDocument(string projectId, CancellationToken ct)
    {
        var loaded = await LoadAsync(projectId, "endpoint", null, ct);
        if (loaded is null) return NotFound();
        var scriptRuntimes = HttpContext.RequestServices.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>();
        return Content(EndpointTypings.ForProject(loaded.Value.Resources, scriptRuntimes), "text/plain; charset=utf-8");
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
                resource, companion.ResourceId, resources, HttpContext.RequestServices.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>(), false, ct);
            var errors = diagnostics.Where(item => item.IsError).Select(item => item.Message).ToList();
            if (errors.Count > 0 || result is null || result.StatusCode >= 400)
                failed.Add(new { id = companion.Id, message = errors.Count > 0 ? string.Join(" ", errors) : JsonSerializer.Serialize(result?.Body) });
            else created.Add(companion.Id);
        }
        return Json(new { created, skipped, failed }, Web);
    }

    private static string CurrentMonth() => DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string NormalizeTab(string? tab, bool draft)
        => tab is "overview" or "edit" or "code" ? tab : draft ? "edit" : "overview";

    private static string ApplyDraftId(string skeleton, string draftId)
    {
        var draft = JsonNode.Parse(OwnerYamlDefinitions.ParseDefinition(skeleton, out _).GetRawText())!.AsObject();
        draft["id"] = draftId;
        if (draft.ContainsKey("slug")) draft["slug"] = draftId;
        if (draft.ContainsKey("name")) draft["name"] = draftId;
        return OwnerYamlDefinitions.ToYaml(JsonSerializer.SerializeToElement(draft));
    }

    private static long RowLong(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static string? RowString(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<OwnerResourceListModel?> LoadListAsync(string projectId, string kind, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var month = CurrentMonth();
        var usage = kind == "endpoint" ? await EndpointUsageMapAsync(projectId, month, ct) : new Dictionary<string, OwnerEndpointUsage>(StringComparer.Ordinal);
        var queryRuns = kind == "query" ? await QueryRunMapAsync(projectId, ct) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var counts = kind == "collection" ? await CollectionCountMapAsync(projectId, resources, ct) : new Dictionary<string, long>(StringComparer.Ordinal);
        var rows = new List<OwnerResourceRow>();
        foreach (var row in resources.Rows(kind).OrderBy(candidate => Text(candidate, "name") ?? Text(candidate, "slug") ?? Text(candidate, kind + "_id") ?? ""))
            rows.Add(BuildRow(kind, row, OwnerProjectResources.Column(row, "definition_json"), usage, queryRuns, counts));
        return new OwnerResourceListModel(projectId, access.Project.Name, kind, rows, month);
    }

    private async Task<Dictionary<string, OwnerEndpointUsage>> EndpointUsageMapAsync(string projectId, string month, CancellationToken ct)
    {
        var map = new Dictionary<string, OwnerEndpointUsage>(StringComparer.Ordinal);
        foreach (var row in await store.ReadProjectUsageEndpointsAsync(projectId, month, 200, ct))
        {
            if (RowString(row, "endpoint_slug") is not { } slug) continue;
            map[slug] = new OwnerEndpointUsage(RowLong(row, "calls"), RowLong(row, "errors"), RowLong(row, "duration_ms_sum"));
        }
        return map;
    }

    private async Task<Dictionary<string, JsonElement>> QueryRunMapAsync(string projectId, CancellationToken ct)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in await store.ListQueryLastRunsAsync(projectId, ct))
            if (RowString(row, "query_id") is { } id) map[id] = row;
        return map;
    }

    private async Task<Dictionary<string, long>> CollectionCountMapAsync(string projectId, OwnerProjectResources resources, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in resources.Rows("collection"))
        {
            if (OwnerDataRecords.Describe(row) is not { } collection) continue;
            map[collection.Id] = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct);
        }
        return map;
    }

    private async Task<OwnerResourceDetailModel?> LoadDetailAsync(string projectId, string kind, string? resourceId,
        string? tab, string? draftId, CancellationToken ct, string? overrideDefinition = null,
        string? overrideError = null, IReadOnlyList<DefinitionDiagnostic>? overrideDiagnostics = null)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicKey = keys.Where(key => key.Enabled && key.KeyType == "public").OrderBy(key => key.CreatedAt).FirstOrDefault()?.Key;
        var baseUrl = ServerBaseUrl.ForRequest(config, Request);
        var month = CurrentMonth();
        if (kind == "game-values")
        {
            var stored = resources.GameValues;
            var display = overrideDefinition
                ?? (stored is { ValueKind: JsonValueKind.Object } payload ? OwnerResourceSource.DisplayText(payload) : OwnerResourceExamples.Skeletons[kind]);
            var parsed = ParseDraft(display);
            var integration = Integration(projectId, kind, null, default, stored, display, resources, publicKey, baseUrl);
            return new OwnerResourceDetailModel(projectId, access.Project.Name, kind, null, "Game values",
                display, resources.BuilderJson(), NormalizeTab(tab, false), false, integration,
                overrideError, overrideDiagnostics, null, parsed);
        }
        if (draftId is not null)
        {
            var display = overrideDefinition ?? ApplyDraftId(OwnerResourceExamples.Skeletons[kind], draftId);
            var parsed = ParseDraft(display);
            var name = (parsed is { ValueKind: JsonValueKind.Object } draft && OwnerProjectScope.Text(draft, "name") is { Length: > 0 } draftName)
                ? draftName : draftId;
            var integration = Integration(projectId, kind, null, default, parsed, display, resources, publicKey, baseUrl);
            var row = BuildDraftRow(kind, draftId, name, parsed);
            return new OwnerResourceDetailModel(projectId, access.Project.Name, kind, draftId, name,
                display, resources.BuilderJson(), NormalizeTab(tab, true), true, integration,
                overrideError, overrideDiagnostics, row, parsed);
        }
        if (resourceId is null) return null;
        var match = resources.Rows(kind).FirstOrDefault(candidate => Text(candidate, kind + "_id") == resourceId);
        if (match.ValueKind != JsonValueKind.Object)
        {
            if (overrideDefinition is null) return null;
            var parsedNew = ParseDraft(overrideDefinition);
            var integrationNew = Integration(projectId, kind, null, default, parsedNew, overrideDefinition, resources, publicKey, baseUrl);
            var rowNew = BuildDraftRow(kind, resourceId, resourceId, parsedNew);
            return new OwnerResourceDetailModel(projectId, access.Project.Name, kind, resourceId, resourceId,
                overrideDefinition, resources.BuilderJson(), "edit", true, integrationNew,
                overrideError, overrideDiagnostics, rowNew, parsedNew);
        }
        var storedDefinition = OwnerProjectResources.Column(match, "definition_json");
        var text = overrideDefinition
            ?? (storedDefinition is { ValueKind: JsonValueKind.Object } live ? OwnerResourceSource.DisplayText(live) : OwnerResourceExamples.Skeletons[kind]);
        var json = overrideDefinition is null ? storedDefinition : ParseDraft(text);
        var guide = Integration(projectId, kind, resourceId, match, storedDefinition, text, resources, publicKey, baseUrl);
        var nameSaved = Text(match, "name") ?? Text(match, "slug") ?? resourceId;
        var usage = kind == "endpoint" ? await EndpointUsageMapAsync(projectId, month, ct) : new Dictionary<string, OwnerEndpointUsage>(StringComparer.Ordinal);
        var queryRuns = kind == "query" ? await QueryRunMapAsync(projectId, ct) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (kind == "collection" && OwnerDataRecords.Describe(match) is { } collection)
            counts[collection.Id] = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct);
        var detailRow = BuildRow(kind, match, storedDefinition, usage, queryRuns, counts);
        return new OwnerResourceDetailModel(projectId, access.Project.Name, kind, resourceId, nameSaved,
            text, resources.BuilderJson(), NormalizeTab(tab, false), false, guide,
            overrideError, overrideDiagnostics, detailRow, json);
    }

    private async Task<OwnerResourcePreviewModel?> LoadPreviewAsync(string projectId, string kind, string resourceId, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var resources = await OwnerProjectResources.LoadAsync(store, projectId, ct);
        var match = resources.Rows(kind).FirstOrDefault(candidate => Text(candidate, kind + "_id") == resourceId);
        if (match.ValueKind != JsonValueKind.Object) return null;
        var definition = OwnerProjectResources.Column(match, "definition_json");
        var display = definition is { ValueKind: JsonValueKind.Object } live ? OwnerResourceSource.DisplayText(live) : OwnerResourceExamples.Skeletons[kind];
        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicKey = keys.Where(key => key.Enabled && key.KeyType == "public").OrderBy(key => key.CreatedAt).FirstOrDefault()?.Key;
        var baseUrl = ServerBaseUrl.ForRequest(config, Request);
        var guide = Integration(projectId, kind, resourceId, match, definition, display, resources, publicKey, baseUrl);
        var usage = kind == "endpoint" ? await EndpointUsageMapAsync(projectId, CurrentMonth(), ct) : new Dictionary<string, OwnerEndpointUsage>(StringComparer.Ordinal);
        var queryRuns = kind == "query" ? await QueryRunMapAsync(projectId, ct) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (kind == "collection" && OwnerDataRecords.Describe(match) is { } collection)
            counts[collection.Id] = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct);
        var row = BuildRow(kind, match, definition, usage, queryRuns, counts);
        return new OwnerResourcePreviewModel(projectId, kind, resourceId, row.Name, row, guide, display, definition);
    }

    private static OwnerResourceRow BuildRow(string kind, JsonElement row, JsonElement? definition,
        Dictionary<string, OwnerEndpointUsage> usage, Dictionary<string, JsonElement> queryRuns, Dictionary<string, long> counts)
    {
        var id = Text(row, kind + "_id") ?? "";
        var name = Text(row, "name") ?? Text(row, "slug") ?? id;
        var steps = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength() : 0;
        var slug = kind == "endpoint" ? Text(row, "slug") ?? Text(row, "endpoint_id") ?? id : null;
        var method = kind == "endpoint" ? Text(row, "method") ?? (definition is { ValueKind: JsonValueKind.Object } e ? OwnerProjectScope.Text(e, "method") : null) ?? "POST" : null;
        var enabled = kind == "endpoint" ? EndpointEnabled(row, definition) : (bool?)null;
        var access = kind == "endpoint"
            ? OwnerGameSnippets.IsInternalEndpoint(definition) ? "Internal"
                : OwnerGameSnippets.RequiresSecretKey(definition) || OwnerGameSnippets.RowRequiresSecretKey(row) ? "Secret key" : "Public"
            : null;
        var stats = slug is not null && usage.TryGetValue(slug, out var hit) ? hit : new OwnerEndpointUsage(0, 0, 0);
        if (slug is not null && !usage.ContainsKey(slug) && usage.TryGetValue(id, out var byId)) stats = byId;
        var storage = kind == "collection"
            ? OwnerDataRecords.Describe(row)?.Global == true || IsGlobalDefinition(definition) ? "Global" : "Per player" : null;
        var clientAccess = kind == "collection" ? OwnerGameSnippets.IsPublicCollection(definition) ? "Public" : "Endpoints only" : null;
        var fields = kind == "collection" ? CollectionFields(definition) : 0;
        var records = kind == "collection" && counts.TryGetValue(id, out var count) ? count : (long?)null;
        var parameters = kind == "workflow" ? WorkflowParams(definition) : null;
        var queryType = kind == "query" ? (definition is { ValueKind: JsonValueKind.Object } q ? OwnerProjectScope.Text(q, "type") : null) ?? "Query" : null;
        var sources = kind == "query" ? QuerySources(definition) : null;
        queryRuns.TryGetValue(id, out var lastRun);
        return new OwnerResourceRow(id, name, method, enabled, steps, access, slug,
            stats.Calls, stats.Errors, stats.DurationMsSum,
            storage, clientAccess, fields, records,
            parameters, steps,
            queryType, sources,
            lastRun.ValueKind == JsonValueKind.Object ? RowString(lastRun, "run_at") : null,
            lastRun.ValueKind == JsonValueKind.Object && lastRun.TryGetProperty("duration_ms", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var durationMs) ? durationMs : null,
            lastRun.ValueKind == JsonValueKind.Object && lastRun.TryGetProperty("records_returned", out var returned) && returned.ValueKind == JsonValueKind.Number && returned.TryGetInt32(out var returnedCount) ? returnedCount : null);
    }

    private static OwnerResourceRow BuildDraftRow(string kind, string id, string name, JsonElement? definition)
    {
        var slug = kind == "endpoint" ? id : null;
        var method = kind == "endpoint" ? (definition is { ValueKind: JsonValueKind.Object } e ? OwnerProjectScope.Text(e, "method") : null) ?? "POST" : null;
        var enabled = kind == "endpoint" ? EndpointEnabled(default, definition) : (bool?)null;
        var access = kind == "endpoint"
            ? OwnerGameSnippets.IsInternalEndpoint(definition) ? "Internal"
                : OwnerGameSnippets.RequiresSecretKey(definition) ? "Secret key" : "Public"
            : null;
        var steps = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength() : 0;
        return new OwnerResourceRow(id, name, method, enabled, steps, access, slug, 0, 0, 0,
            kind == "collection" ? (IsGlobalDefinition(definition) ? "Global" : "Per player") : null,
            kind == "collection" ? (OwnerGameSnippets.IsPublicCollection(definition) ? "Public" : "Endpoints only") : null,
            kind == "collection" ? CollectionFields(definition) : 0, null,
            kind == "workflow" ? WorkflowParams(definition) : null, steps,
            kind == "query" ? ((definition is { ValueKind: JsonValueKind.Object } q ? OwnerProjectScope.Text(q, "type") : null) ?? "Query") : null,
            kind == "query" ? QuerySources(definition) : null, null, null, null);
    }

    private static bool EndpointEnabled(JsonElement row, JsonElement? definition)
    {
        if (definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("enabled", out var flag))
        {
            if (flag.ValueKind == JsonValueKind.True) return true;
            if (flag.ValueKind == JsonValueKind.False) return false;
        }
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("enabled", out var column))
        {
            if (column.ValueKind == JsonValueKind.True) return true;
            if (column.ValueKind == JsonValueKind.False) return false;
            if (column.ValueKind == JsonValueKind.Number && column.TryGetInt64(out var number)) return number != 0;
        }
        return true;
    }

    private static int CollectionFields(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } d) return 0;
        if (!d.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object) return 0;
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            return properties.EnumerateObject().Count();
        return 0;
    }

    private static string WorkflowParams(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("params", out var parameters)) return "None";
        var names = parameters.ValueKind switch
        {
            JsonValueKind.Object => parameters.EnumerateObject().Select(p => p.Name).ToList(),
            JsonValueKind.Array => parameters.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null)
                .OfType<string>().ToList(),
            _ => new List<string>(),
        };
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }

    private static string QuerySources(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return "None";
        var names = new List<string>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind == JsonValueKind.String && source.GetString() is { } text) names.Add(text);
            else if (source.ValueKind == JsonValueKind.Object
                && (OwnerProjectScope.Text(source, "collectionId") ?? OwnerProjectScope.Text(source, "collection") ?? OwnerProjectScope.Text(source, "queryId")) is { } named)
                names.Add(named);
        }
        if (names.Count == 0) return sources.GetArrayLength() == 0 ? "None" : sources.GetArrayLength() + (sources.GetArrayLength() == 1 ? " source" : " sources");
        return names.Count <= 2 ? string.Join(", ", names) : names[0] + ", " + names[1] + " (+" + (names.Count - 2) + " more)";
    }

    private async Task<(OwnerResourcesModel Model, OwnerProjectResources Resources)?> LoadAsync(string projectId, string kind, string? id, CancellationToken ct, string? draftId = null)
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
        if (id is null && kind != "game-values" && draftId is not null)
        {
            var draft = JsonNode.Parse(OwnerYamlDefinitions.ParseDefinition(display, out _).GetRawText())!.AsObject();
            draft["id"] = draftId;
            if (draft.ContainsKey("slug")) draft["slug"] = draftId;
            if (draft.ContainsKey("name")) draft["name"] = draftId;
            display = OwnerYamlDefinitions.ToYaml(JsonSerializer.SerializeToElement(draft));
        }
        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicKey = keys.Where(key => key.Enabled && key.KeyType == "public").OrderBy(key => key.CreatedAt).FirstOrDefault()?.Key;
        var baseUrl = ServerBaseUrl.ForRequest(config, Request);
        var integration = Integration(projectId, kind, id, row, value, display, resources, publicKey, baseUrl);
        var model = new OwnerResourcesModel(projectId, access.Project.Name, kind, items, id, display, resources.BuilderJson(), Integration: integration);
        return (model, resources);
    }


    /// <summary>
    /// Tabbed integration guide for the open resource, built for saved resources and for new drafts alike.
    /// For drafts the editor text is parsed for ids and schemas; the guide then names the skeleton values
    /// and says to save first. Never includes a secret key.
    /// </summary>
    private static OwnerResourceIntegration? Integration(string projectId, string kind, string? id, JsonElement row,
        JsonElement? definition, string display, OwnerProjectResources resources, string? publicKey, string baseUrl)
    {
        var saved = row.ValueKind == JsonValueKind.Object && id is not null;
        var draft = !saved ? ParseDraft(display) : null;
        var effective = definition ?? draft;
        var projectUrl = OwnerProjectScope.ProjectUrl(projectId);
        var configure = publicKey is null ? null : $"NetworkStorage.Configure( {JsonSerializer.Serialize(projectId)}, {JsonSerializer.Serialize(publicKey)}, {JsonSerializer.Serialize(baseUrl)} );";
        var setupNote = publicKey is null
            ? "This project has no enabled public key. Create one under API keys: game code needs it."
            : ServerBaseUrl.IsLoopback(baseUrl)
                ? "This base URL only works on this machine. Set server.public_url so players on other computers can reach it."
                : baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "This base URL uses plain HTTP, which s&box game clients may block. Prefer HTTPS."
                    : null;
        return kind switch
        {
            "endpoint" => EndpointIntegration(projectId, projectUrl, id, row, effective, saved, configure, setupNote),
            "collection" => CollectionIntegration(projectId, projectUrl, id, row, effective, saved, resources, configure, setupNote),
            "query" => QueryIntegration(projectId, id, row, effective, saved, configure, setupNote),
            "workflow" => WorkflowIntegration(projectId, projectUrl, id, effective, saved, resources, configure, setupNote),
            "game-values" => GameValuesIntegration(projectId, resources, configure, setupNote),
            _ => null,
        };
    }

    private static OwnerResourceIntegration EndpointIntegration(string projectId, string projectUrl, string? id,
        JsonElement row, JsonElement? definition, bool saved, string? configure, string? setupNote)
    {
        var slug = saved ? Text(row, "slug") ?? id! : DraftSlug(definition, "my-endpoint");
        var resourceId = saved ? id : null;
        if (OwnerGameSnippets.IsInternalEndpoint(definition))
        {
            var panels = new[]
            {
                new OwnerIntegrationPanel("integration-yaml", "Endpoint step", "yaml", OwnerGameSnippets.EndpointRunRoute(slug)),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null,
                    $"My sbox-ns project ID is {projectId}. The endpoint \"{slug}\" is internal, so it never runs over HTTP and game code cannot call it. " +
                    "Read its definition with definition_get, find the endpoints or workflows that should call it, dry-run one caller with endpoint_test, " +
                    "and show me the calling step. Do not save anything without my confirm string."),
            };
            return new OwnerResourceIntegration(projectId, "endpoint", resourceId, "Use from another endpoint",
                "Internal endpoints never run over HTTP or from game code. Call them from a condition route of another endpoint or workflow, then call that endpoint from the game." +
                (saved ? "" : " This draft is not saved yet: save first, then this guide names your id."),
                configure, setupNote, panels, null,
                saved && id is not null ? $"{projectUrl}/versions?kind=endpoint&id={Uri.EscapeDataString(id)}" : null, null);
        }
        var secret = OwnerGameSnippets.RequiresSecretKey(definition);
        var intro = secret
            ? "This endpoint requires a secret key: call it from a dedicated server, never from the game client. The call below is the same; the dedicated server sends the secret key with it."
            : "s&box hides 4xx response bodies from game code, so a rejected call returns no value and the error code can read HTTP_ERROR. The Logs tab shows the status.";
        var prompt = secret
            ? $"My sbox-ns project ID is {projectId}. I am working on the endpoint \"{slug}\", which requires a secret key, so game clients must never call it. " +
                "Read its definition with definition_get, dry-run it with endpoint_test using a secret key for a success case and a rejected case (target next when staged), " +
                "check logs_requests and errors_recent for failures, and show me the dedicated-server C# that calls it with NetworkStorage.CallEndpoint. Do not save anything without my confirm string."
            : $"My sbox-ns project ID is {projectId}. I am working on the endpoint \"{slug}\". " +
                "Read its definition with definition_get, then dry-run it with endpoint_test for a success case and a rejected case (target next when staged). " +
                "Check logs_requests and errors_recent for failures, and show me the C# that calls it with NetworkStorage.CallEndpoint. Do not save anything without my confirm string.";
        var callPanels = new[]
        {
            new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp", OwnerGameSnippets.EndpointCall(slug, definition)),
            new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null, prompt),
        };
        return new OwnerResourceIntegration(projectId, "endpoint", resourceId, "Use from the game",
            intro + (saved ? "" : " This draft is not saved yet: save first, then this guide names your id."),
            configure, setupNote, callPanels,
            saved ? $"{projectUrl}/tests?endpoint={Uri.EscapeDataString(slug)}#try-it" : null,
            saved && id is not null ? $"{projectUrl}/versions?kind=endpoint&id={Uri.EscapeDataString(id)}" : null, null);
    }

    private static OwnerResourceIntegration CollectionIntegration(string projectId, string projectUrl, string? id,
        JsonElement row, JsonElement? definition, bool saved, OwnerProjectResources resources, string? configure, string? setupNote)
    {
        var collectionId = saved ? id! : DraftSlug(definition, "my-collection");
        var resourceId = saved ? id : null;
        var dataUrl = saved ? OwnerDataController.CollectionUrl(projectId, id!) : null;
        var global = saved ? OwnerDataRecords.Describe(row)?.Global == true : IsGlobalDefinition(definition);
        if (saved && OwnerGameSnippets.IsPublicCollection(definition))
        {
            var intro = global
                ? "Game clients may read and write this global collection directly."
                : "With s&box authentication on, players may only write their own document: the key is their Steam ID, or starts with {steamId}_ for save slots.";
            if (!OwnerGameSnippets.AllowsClientDelete(definition))
                intro += " Deletes from game clients are off: delete through an endpoint, or turn on allowRecordDelete.";
            var panels = new[]
            {
                new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp",
                    OwnerGameSnippets.CollectionDocuments(collectionId, global, definition)),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null,
                    $"My sbox-ns project ID is {projectId}. I am working on the collection \"{collectionId}\" ({(global ? "global" : "per-player")}, accessMode public). " +
                    "Read its definition with definition_get and sample records with data_records, then show me the GetDocument and SaveDocument C# for it " +
                    "and say whether writes belong in game code or in an endpoint. Do not save anything without my confirm string."),
            };
            return new OwnerResourceIntegration(projectId, "collection", resourceId, "Use from the game", intro,
                configure, setupNote, panels, null, null, dataUrl);
        }
        IReadOnlyList<string> callers = saved ? EndpointsUsingCollection(resources, collectionId) : [];
        var caller = callers.FirstOrDefault();
        var callerDefinition = caller is null ? null : EndpointDefinition(resources, caller);
        var endpointNote = caller is null
            ? "No game-callable endpoint references this collection yet."
            : $"The endpoint \"{caller}\" reads or writes it: call that endpoint from the game.";
        var code = caller is null
            ? $"// Collection \"{collectionId}\" is endpoint-only: direct reads and writes answer 403 ENDPOINT_ONLY.\n// Save an endpoint that reads or writes it, then call that endpoint:\nvar result = await NetworkStorage.CallEndpoint( \"my-endpoint\", new {{ }} );\nif ( !result.HasValue )\n{{\n    return;\n}}"
            : OwnerGameSnippets.EndpointCall(caller, callerDefinition);
        var collectionPrompt = caller is null
            ? $"My sbox-ns project ID is {projectId}. I am working on the collection \"{collectionId}\" (endpoint-only: game clients get 403 ENDPOINT_ONLY on direct calls). " +
                "No game-callable endpoint references it yet. Draft the endpoint YAML that should read or write it, validate with definition_check, " +
                "and show me the C# that would call it with NetworkStorage.CallEndpoint once saved. Do not save anything without my confirm string."
            : $"My sbox-ns project ID is {projectId}. I am working on the collection \"{collectionId}\" (endpoint-only: game clients get 403 ENDPOINT_ONLY on direct calls). " +
                $"The endpoint \"{caller}\" reads or writes it. Dry-run that endpoint with endpoint_test (target next when staged), " +
                "then show me the C# that calls it with NetworkStorage.CallEndpoint. Do not save anything without my confirm string.";
        return new OwnerResourceIntegration(projectId, "collection", resourceId, "Use from the game",
            "Game clients cannot read or write this collection directly (accessMode is not public). Call an endpoint that reads or writes it; direct calls answer 403 ENDPOINT_ONLY. "
            + endpointNote + (saved ? "" : " This draft is not saved yet: save first, then this guide names your id."),
            configure, setupNote,
            [
                new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp", code),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null, collectionPrompt),
            ], null, null, dataUrl);
    }

    private static OwnerResourceIntegration QueryIntegration(string projectId, string? id, JsonElement row,
        JsonElement? definition, bool saved, string? configure, string? setupNote)
    {
        var queryId = saved ? id! : DraftSlug(definition, "my-query");
        var secret = OwnerGameSnippets.RequiresSecretKey(definition)
            || (saved && OwnerGameSnippets.RowRequiresSecretKey(row));
        var ttl = definition is { ValueKind: JsonValueKind.Object } query && query.TryGetProperty("cache", out var cache)
            && cache.ValueKind == JsonValueKind.Object && cache.TryGetProperty("ttlSeconds", out var seconds)
            && seconds.ValueKind == JsonValueKind.Number ? seconds.GetRawText() : null;
        var intro = secret
            ? "This query requires a secret key: run it from a dedicated server, never from the game client."
            : $"Query results are shared between callers{(ttl is null ? "" : $" for {ttl} seconds")}. A forced fresh run needs a secret key.";
        var prompt = secret
            ? $"My sbox-ns project ID is {projectId}. I am working on the query \"{queryId}\", which requires a secret key, so game clients must never run it. " +
                "Read its definition with definition_get (sources, config, cache), check logs_requests for failures, " +
                "then show me the dedicated-server C# that runs it with NetworkStorage.RunQuery. Do not save anything without my confirm string."
            : $"My sbox-ns project ID is {projectId}. I am working on the query \"{queryId}\". " +
                "Read its definition with definition_get (sources, config, cache), check logs_requests for failures, " +
                "then show me the C# that runs it with NetworkStorage.RunQuery and how the shared cached result behaves. Do not save anything without my confirm string.";
        return new OwnerResourceIntegration(projectId, "query", saved ? id : null, "Use from the game",
            intro + (saved ? "" : " This draft is not saved yet: save first, then this guide names your id."),
            configure, setupNote,
            [
                new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp", OwnerGameSnippets.QueryRun(queryId)),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null, prompt),
            ], null, null, null);
    }

    private static OwnerResourceIntegration WorkflowIntegration(string projectId, string projectUrl, string? id,
        JsonElement? definition, bool saved, OwnerProjectResources resources, string? configure, string? setupNote)
    {
        var workflowId = saved ? id! : DraftSlug(definition, "my-workflow");
        IReadOnlyList<string> callers = saved ? EndpointsRunningWorkflow(resources, workflowId) : [];
        var caller = callers.FirstOrDefault();
        var callerDefinition = caller is null ? null : EndpointDefinition(resources, caller);
        var code = caller is null
            ? $"// Workflow \"{workflowId}\" never runs from game code: it runs inside an endpoint step.\n// Save an endpoint with the step from the Endpoint step tab, then call that endpoint:\nvar result = await NetworkStorage.CallEndpoint( \"my-endpoint\", new {{ }} );\nif ( !result.HasValue )\n{{\n    return;\n}}"
            : OwnerGameSnippets.EndpointCall(caller, callerDefinition);
        var parameters = OwnerGameSnippets.ParamSkeleton(definition);
        var paramList = parameters.Count == 0 ? "none" : string.Join(", ", parameters.Keys);
        var prompt = caller is null
            ? $"My sbox-ns project ID is {projectId}. I am working on the workflow \"{workflowId}\" (params: {paramList}). " +
                "Workflows never run from game code: they run inside an endpoint step. No game-callable endpoint runs it yet. " +
                "Draft the endpoint step YAML that runs it with its params, validate with definition_check, " +
                "and show me the C# that calls that endpoint with NetworkStorage.CallEndpoint once saved. Do not save anything without my confirm string."
            : $"My sbox-ns project ID is {projectId}. I am working on the workflow \"{workflowId}\" (params: {paramList}). " +
                "Workflows never run from game code: they run inside an endpoint step. " +
                $"The endpoint \"{caller}\" runs it. Dry-run that endpoint with endpoint_test (target next when staged), " +
                "then show me the endpoint step YAML and the C# that calls the endpoint with NetworkStorage.CallEndpoint. Do not save anything without my confirm string.";
        return new OwnerResourceIntegration(projectId, "workflow", saved ? id : null, "Run from an endpoint",
            "Workflows never run from game code. They run inside an endpoint step; the game calls that endpoint." +
            (caller is null ? " No game-callable endpoint runs this workflow yet." : $" The endpoint \"{caller}\" runs it.") +
            (saved ? "" : " This draft is not saved yet: save first, then this guide names your id."),
            configure, setupNote,
            [
                new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp", code),
                new OwnerIntegrationPanel("integration-yaml", "Endpoint step", "yaml",
                    OwnerGameSnippets.WorkflowStep(workflowId, definition)),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null, prompt),
            ], null, saved && id is not null ? $"{projectUrl}/versions?kind=workflow&id={Uri.EscapeDataString(id)}" : null, null);
    }

    private static OwnerResourceIntegration GameValuesIntegration(string projectId, OwnerProjectResources resources,
        string? configure, string? setupNote)
    {
        var items = resources.ValueItems();
        var groups = items.Where(item => !item.Table).Select(item => item.Id).Take(6).ToList();
        var tables = items.Where(item => item.Table).Select(item => item.Id).Take(6).ToList();
        var summary = items.Count == 0
            ? "No groups or tables are saved yet."
            : "Groups: " + (groups.Count == 0 ? "none" : string.Join(", ", groups))
                + ". Tables: " + (tables.Count == 0 ? "none" : string.Join(", ", tables)) + ".";
        return new OwnerResourceIntegration(projectId, "game-values", null, "Use from the game",
            "The game reads all values in one call. Values are read-only from game code; edits happen here or through editor sync. " + summary,
            configure, setupNote,
            [
                new OwnerIntegrationPanel("integration-csharp", "C# from the game", "csharp", OwnerGameSnippets.GameValuesRead()),
                new OwnerIntegrationPanel("integration-prompt", "Agent prompt", null,
                    $"My sbox-ns project ID is {projectId}. I am editing game values ({summary}) " +
                    "Read them with definition_get kind game-values, then show me the C# that reads them with NetworkStorage.GetGameValues " +
                    "and which group or table the game needs. " +
                    $"Saving needs confirm \"{projectId}/game-values/game-values\". Do not save without my go-ahead."),
            ], null, null, null);
    }

    private static JsonElement? ParseDraft(string display)
    {
        try
        {
            var parsed = OwnerYamlDefinitions.ParseDefinition(display, out _);
            return parsed.ValueKind == JsonValueKind.Object ? parsed : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string DraftSlug(JsonElement? definition, string fallback)
        => definition is { ValueKind: JsonValueKind.Object } draft
            && (OwnerProjectScope.Text(draft, "slug") ?? OwnerProjectScope.Text(draft, "id")) is { Length: > 0 } slug
            ? slug : fallback;

    private static bool IsGlobalDefinition(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("collectionType", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "global", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> EndpointsUsingCollection(OwnerProjectResources resources, string collectionId)
    {
        var slugs = new List<string>();
        foreach (var row in resources.Rows("endpoint"))
        {
            if (OwnerProjectResources.Column(row, "definition_json") is not { } definition) continue;
            if (OwnerGameSnippets.RequiresSecretKey(definition) || OwnerGameSnippets.IsInternalEndpoint(definition)
                || OwnerGameSnippets.RowRequiresSecretKey(row)
                || definition.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
            if (!DefinitionReferences(definition, "collection", collectionId)) continue;
            if ((Text(row, "slug") ?? Text(row, "endpoint_id")) is { } slug) slugs.Add(slug);
        }
        slugs.Sort(StringComparer.Ordinal);
        return slugs;
    }

    private static IReadOnlyList<string> EndpointsRunningWorkflow(OwnerProjectResources resources, string workflowId)
    {
        var slugs = new List<string>();
        foreach (var row in resources.Rows("endpoint"))
        {
            if (OwnerProjectResources.Column(row, "definition_json") is not { } definition) continue;
            if (OwnerGameSnippets.RequiresSecretKey(definition) || OwnerGameSnippets.IsInternalEndpoint(definition)
                || OwnerGameSnippets.RowRequiresSecretKey(row)
                || definition.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
            if (!DefinitionReferences(definition, "workflow", workflowId)
                && !DefinitionReferences(definition, "flow", workflowId)) continue;
            if ((Text(row, "slug") ?? Text(row, "endpoint_id")) is { } slug) slugs.Add(slug);
        }
        slugs.Sort(StringComparer.Ordinal);
        return slugs;
    }

    private static JsonElement? EndpointDefinition(OwnerProjectResources resources, string slug)
    {
        foreach (var row in resources.Rows("endpoint"))
            if ((Text(row, "slug") ?? Text(row, "endpoint_id")) == slug)
                return OwnerProjectResources.Column(row, "definition_json");
        return null;
    }

    private static bool DefinitionReferences(JsonElement definition, string property, string target)
    {
        if (definition.ValueKind != JsonValueKind.Object) return false;
        if (definition.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray())
                if (StepReferences(step, property, target)) return true;
        return false;
    }

    private static bool StepReferences(JsonElement step, string property, string target)
    {
        if (step.ValueKind != JsonValueKind.Object) return false;
        if (step.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), target, StringComparison.Ordinal)) return true;
        foreach (var route in new[] { "onTrue", "onFalse" })
            if (step.TryGetProperty(route, out var branch) && branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("flow", out var flow) && flow.ValueKind == JsonValueKind.String
                && string.Equals(flow.GetString(), target, StringComparison.Ordinal)) return true;
        return false;
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
