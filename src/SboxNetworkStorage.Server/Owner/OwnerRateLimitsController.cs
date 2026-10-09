using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerRateLimitRule(string Id, string Name, string Collection, string Field, string Scope, string Action,
    IReadOnlyDictionary<string, long> Windows, bool Webhook, bool Enabled);

public sealed record OwnerRateLimitsModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerRateLimitRule> Rules,
    IReadOnlyList<string> Collections, IReadOnlyList<string> Fields, OwnerRateLimitRule? Editing, string? Error = null, bool Saved = false);

/// <summary>
/// Field rate-limit rules editor. Rules persist in the same store row the management API
/// (<c>PUT/GET /v3/manage/{project}/rate-limit-rules</c>) and the editor Sync Tool use.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed partial class OwnerRateLimitsController(INetworkStorageProjectService projects, INetworkStorageStore store,
    IAuditLogger audit, TimeProvider time) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/rate-limits";
    public static readonly string[] WindowKeys = ["perMinute", "perHour", "perDay", "perWeek", "perMonth", "perYear"];

    /// <summary>Hosted presets (storage-rate-limit-rules.js addPreset).</summary>
    public static readonly IReadOnlyDictionary<string, OwnerRateLimitRule> Presets = new Dictionary<string, OwnerRateLimitRule>(StringComparer.Ordinal)
    {
        ["saves"] = new("rl_save_throttle", "Save throttle", "*", "*", "per_player", "reject", new Dictionary<string, long> { ["perHour"] = 360 }, false, true),
        ["xp"] = new("rl_xp_daily", "XP daily cap", "*", "xp", "per_player", "clamp", new Dictionary<string, long> { ["perDay"] = 10000 }, false, true),
        ["gold"] = new("rl_gold_hourly", "Gold hourly cap", "*", "gold", "per_player", "clamp", new Dictionary<string, long> { ["perHour"] = 50000 }, false, true),
        ["currency"] = new("rl_currency_guard", "Currency anti-exploit", "*", "currency", "per_player", "clamp",
            new Dictionary<string, long> { ["perMinute"] = 1000, ["perHour"] = 10000, ["perDay"] = 50000 }, false, true),
    };

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex RuleIdPattern();
    [GeneratedRegex("^(\\*|[A-Za-z0-9_][A-Za-z0-9_.]{0,127})$")]
    private static partial Regex FieldPattern();

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? edit, [FromQuery] bool saved, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        return View("~/Views/Owner/RateLimits.cshtml", model with { Editing = model.Rules.FirstOrDefault(rule => rule.Id == edit), Saved = saved });
    }

    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var form = await Request.ReadFormAsync(ct);
        string Field(string name) => form[name].ToString().Trim();
        var windows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in WindowKeys)
        {
            var raw = Field(key);
            if (raw.Length == 0) continue;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 1_000_000_000)
                return Invalid(model, $"{key} must be a whole number between 1 and 1000000000, or empty.");
            windows[key] = limit;
        }
        var collection = Field("collection") is { Length: > 0 } c ? c : "*";
        var field = Field("field") is { Length: > 0 } f ? f : "*";
        var id = Field("id");
        if (id.Length == 0) id = AutoId(collection, field);
        var rule = new OwnerRateLimitRule(id, Field("name"), collection, field,
            Field("scope") == "global" ? "global" : "per_player",
            Field("action") is "clamp" or "flag" ? Field("action") : "reject",
            windows, form["webhook"] == "true", form["enabled"] == "true");
        if (!RuleIdPattern().IsMatch(rule.Id)) return Invalid(model, "Rule id may contain letters, numbers and underscores (maximum 64).");
        if (rule.Collection != "*" && !model.Collections.Contains(rule.Collection, StringComparer.Ordinal))
            return Invalid(model, $"Collection '{rule.Collection}' does not exist in this project. Use * for every collection.");
        if (!FieldPattern().IsMatch(rule.Field)) return Invalid(model, "Field must be * or a field path such as gold or stats.xp.");
        if (windows.Count == 0) return Invalid(model, "Set at least one time window.");
        var original = Field("originalId");
        await SaveRulesAsync(projectId, model.Rules.Where(existing => existing.Id != rule.Id && existing.Id != original).Append(rule), ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "rate-limit-rule.save", new { rule.Id }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/rate-limits?saved=true");
    }

    [HttpPost(Route + "/preset")]
    public async Task<IActionResult> AddPreset(string projectId, [FromForm] string? preset, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        if (preset is null || !Presets.TryGetValue(preset, out var rule)) return Invalid(model, "Unknown preset.");
        if (model.Rules.Any(existing => existing.Id == rule.Id)) return Invalid(model, $"The {preset} preset is already added as {rule.Id}.");
        await SaveRulesAsync(projectId, model.Rules.Append(rule), ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "rate-limit-rule.save", new { rule.Id, preset }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/rate-limits?saved=true&edit={Uri.EscapeDataString(rule.Id)}");
    }

    [HttpPost(Route + "/delete")]
    public async Task<IActionResult> Delete(string projectId, [FromForm] string? id, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        if (!model.Rules.Any(rule => rule.Id == id)) return NotFound();
        await SaveRulesAsync(projectId, model.Rules.Where(rule => rule.Id != id), ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "rate-limit-rule.delete", new { id }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/rate-limits?saved=true");
    }

    private IActionResult Invalid(OwnerRateLimitsModel model, string error)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("~/Views/Owner/RateLimits.cshtml", model with { Error = error });
    }

    private Task SaveRulesAsync(string projectId, IEnumerable<OwnerRateLimitRule> rules, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var rule in rules)
        {
            var windows = new JsonObject();
            foreach (var key in WindowKeys)
                if (rule.Windows.TryGetValue(key, out var limit)) windows[key] = limit;
            array.Add(new JsonObject
            {
                ["id"] = rule.Id, ["name"] = rule.Name, ["collection"] = rule.Collection, ["field"] = rule.Field,
                ["scope"] = rule.Scope, ["windows"] = windows, ["action"] = rule.Action, ["webhook"] = rule.Webhook, ["enabled"] = rule.Enabled,
            });
        }
        return store.UpsertRateLimitRulesAsync(projectId, JsonSerializer.SerializeToElement(array), time.GetUtcNow().ToUnixTimeMilliseconds(), ct);
    }

    private async Task<OwnerRateLimitsModel?> LoadAsync(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var rules = new List<OwnerRateLimitRule>();
        if (await store.ReadRateLimitRulesAsync(projectId, ct) is { } row && OwnerProjectScope.JsonColumn(row, "rules_json") is { } document)
        {
            var list = document.ValueKind == JsonValueKind.Object && document.TryGetProperty("rules", out var nested) ? nested : document;
            if (list.ValueKind == JsonValueKind.Array)
                rules.AddRange(list.EnumerateArray().Where(rule => rule.ValueKind == JsonValueKind.Object).Select(ReadRule));
        }
        var collections = new List<string>();
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var collection in await store.ListCollectionsAsync(projectId, ct))
        {
            if (OwnerProjectScope.Text(collection, "collection_id") is { } id) collections.Add(id);
            if (OwnerProjectScope.JsonColumn(collection, "definition_json") is { } definition) CollectFields(definition, fields);
        }
        return new OwnerRateLimitsModel(projectId, project.Name, rules, collections, fields.ToList(), null);
    }

    private static string AutoId(string collection, string field)
    {
        var raw = Regex.Replace($"rl_{(collection == "*" ? "all" : collection)}_{(field == "*" ? "all" : field)}", "[^A-Za-z0-9_]", "_");
        return raw.Length > 64 ? raw[..64] : raw;
    }

    private static OwnerRateLimitRule ReadRule(JsonElement rule)
    {
        var windows = new Dictionary<string, long>(StringComparer.Ordinal);
        var hasWindows = rule.TryGetProperty("windows", out var windowElement) && windowElement.ValueKind == JsonValueKind.Object;
        foreach (var key in WindowKeys)
        {
            // Legacy flat rules use maxPerMinute/maxPerHour/maxPerDay.
            var flat = "max" + char.ToUpperInvariant(key[0]) + key[1..];
            if (hasWindows && windowElement.TryGetProperty(key, out var value) && value.TryGetInt64(out var limit) && limit > 0) windows[key] = limit;
            else if (rule.TryGetProperty(flat, out var legacy) && legacy.TryGetInt64(out var legacyLimit) && legacyLimit > 0) windows[key] = legacyLimit;
        }
        return new OwnerRateLimitRule(
            OwnerProjectScope.Text(rule, "id") ?? "", OwnerProjectScope.Text(rule, "name") ?? "",
            OwnerProjectScope.Text(rule, "collection") ?? "*", OwnerProjectScope.Text(rule, "field") ?? "*",
            OwnerProjectScope.Text(rule, "scope") == "global" ? "global" : "per_player",
            OwnerProjectScope.Text(rule, "action") is "clamp" or "flag" ? OwnerProjectScope.Text(rule, "action")! : "reject",
            windows,
            rule.TryGetProperty("webhook", out var webhook) && webhook.ValueKind == JsonValueKind.True,
            !rule.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False);
    }

    /// <summary>Top-level field names from flat, JSON-schema style or array-of-fields collection schemas.</summary>
    private static void CollectFields(JsonElement definition, ISet<string> fields)
    {
        if (!definition.TryGetProperty("schema", out var schema)) return;
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            schema = properties;
        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in schema.EnumerateObject())
                if (!property.Name.StartsWith('_') && property.Name is not ("type" or "required")) fields.Add(property.Name);
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                if (OwnerProjectScope.Text(item, "name") is { } name) fields.Add(name);
        }
    }
}
