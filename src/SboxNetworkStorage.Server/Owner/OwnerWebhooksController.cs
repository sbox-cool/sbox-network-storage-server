using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerWebhookProfile(string Id, string Name, string Url, string Color, bool Enabled);

public sealed record OwnerWebhooksModel(string ProjectId, string ProjectName, string DefaultUrl, IReadOnlyList<OwnerWebhookProfile> Profiles,
    OwnerWebhookProfile? Editing, string? Error = null, bool Saved = false);

/// <summary>
/// Discord webhook profiles stored on the project (<c>webhooks.defaultUrl</c>, <c>webhooks.profiles</c>, the hosted
/// layout). Webhook steps reference a profile by its URL; this page renders the step YAML for each profile and
/// <c>profiles.json</c> feeds the resource builder's webhook step picker.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed partial class OwnerWebhooksController(INetworkStorageProjectService projects, IWorkspaceStore workspace,
    IAuditLogger audit) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/webhooks";

    [GeneratedRegex("^[a-z0-9_]{1,40}$", RegexOptions.None, 100)]
    private static partial Regex ProfileIdPattern();
    [GeneratedRegex("^#?[0-9a-fA-F]{6}$", RegexOptions.None, 100)]
    private static partial Regex ColorPattern();

    /// <summary>The executor only sends to Discord webhook URLs (EndpointStepExecutor webhook step).</summary>
    public static bool IsDiscordWebhookUrl(string url)
        => (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)
            || url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal))
           && Uri.TryCreate(url, UriKind.Absolute, out _);

    /// <summary>A webhook step that sends through <paramref name="profile"/>; the same shape the executor runs.</summary>
    public static string StepYaml(OwnerWebhookProfile profile)
        => $"- id: notify_{profile.Id}\n  type: webhook\n  url: {Quote(profile.Url)}\n  title: {Quote(profile.Name)}\n  description: \"Player {{{{steamId}}}} called this endpoint\"\n  color: \"{profile.Color.TrimStart('#')}\"\n  fields:\n    - name: Player\n      value: \"{{{{steamId}}}}\"\n      inline: true\n";

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? edit, [FromQuery] bool saved, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        return View("~/Views/Owner/Webhooks.cshtml", model with { Editing = model.Profiles.FirstOrDefault(profile => profile.Id == edit), Saved = saved });
    }

    [HttpGet(Route + "/profiles.json")]
    public async Task<IActionResult> Profiles(string projectId, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        return Json(new
        {
            defaultUrl = model.DefaultUrl,
            profiles = model.Profiles.Where(profile => profile.Enabled)
                .Select(profile => new { id = profile.Id, name = profile.Name, url = profile.Url, color = profile.Color.TrimStart('#') }),
        });
    }

    [HttpPost(Route + "/default")]
    public async Task<IActionResult> SaveDefault(string projectId, [FromForm] string? defaultUrl, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var url = defaultUrl?.Trim() ?? "";
        if (url.Length > 0 && !IsDiscordWebhookUrl(url))
            return Invalid(model, "The default URL must be a Discord webhook URL (https://discord.com/api/webhooks/...).");
        await SaveAsync(projectId, url, model.Profiles, ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "webhooks.default", new { configured = url.Length > 0 }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/webhooks?saved=true");
    }

    [HttpPost(Route + "/profiles")]
    public async Task<IActionResult> SaveProfile(string projectId, [FromForm] string? id, [FromForm] string? originalId, [FromForm] string? name,
        [FromForm] string? url, [FromForm] string? color, [FromForm] bool enabled, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var profile = new OwnerWebhookProfile((id ?? "").Trim(), (name ?? "").Trim(), (url ?? "").Trim(),
            string.IsNullOrWhiteSpace(color) ? "#5865f2" : "#" + color.Trim().TrimStart('#').ToLowerInvariant(), enabled);
        if (!ProfileIdPattern().IsMatch(profile.Id)) return Invalid(model, "Profile id may contain lowercase letters, numbers and underscores (maximum 40).");
        if (profile.Name.Length is < 1 or > 64) return Invalid(model, "Give the profile a name of 1 to 64 characters.");
        if (!IsDiscordWebhookUrl(profile.Url)) return Invalid(model, "The profile URL must be a Discord webhook URL (https://discord.com/api/webhooks/...).");
        if (!ColorPattern().IsMatch(profile.Color)) return Invalid(model, "Color must be a six digit hex value such as 5865f2.");
        if (originalId != profile.Id && model.Profiles.Any(existing => existing.Id == profile.Id))
            return Invalid(model, $"A profile with id {profile.Id} already exists.");
        var profiles = model.Profiles.Where(existing => existing.Id != profile.Id && existing.Id != originalId).Append(profile).ToList();
        await SaveAsync(projectId, model.DefaultUrl, profiles, ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "webhooks.profile.save", new { profile.Id }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/webhooks?saved=true#profile-{profile.Id}");
    }

    [HttpPost(Route + "/profiles/delete")]
    public async Task<IActionResult> DeleteProfile(string projectId, [FromForm] string? id, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        if (!model.Profiles.Any(profile => profile.Id == id)) return NotFound();
        await SaveAsync(projectId, model.DefaultUrl, model.Profiles.Where(profile => profile.Id != id).ToList(), ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "webhooks.profile.delete", new { id }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/webhooks?saved=true");
    }

    private IActionResult Invalid(OwnerWebhooksModel model, string error)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("~/Views/Owner/Webhooks.cshtml", model with { Error = error });
    }

    /// <summary>Rewrites defaultUrl and profiles; other webhook settings on the project are preserved.</summary>
    private async Task SaveAsync(string projectId, string defaultUrl, IReadOnlyList<OwnerWebhookProfile> profiles, CancellationToken ct)
    {
        var list = (await workspace.GetUserProjectsAsync(OwnerProjectScope.Owner, ct)).ToList();
        var index = list.FindIndex(project => string.Equals(project.Id, projectId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"Project '{projectId}' disappeared while saving webhooks.");
        var webhooks = new Dictionary<string, object>(list[index].Webhooks ?? new Dictionary<string, object>(), StringComparer.Ordinal)
        {
            ["defaultUrl"] = defaultUrl,
            ["profiles"] = profiles.Select(profile => new Dictionary<string, object>
            {
                ["id"] = profile.Id, ["name"] = profile.Name, ["url"] = profile.Url, ["color"] = profile.Color, ["enabled"] = profile.Enabled,
            }).ToList(),
        };
        list[index] = list[index] with { Webhooks = webhooks, DiscordWebhook = defaultUrl, UpdatedAt = DateTimeOffset.UtcNow };
        await workspace.SaveUserProjectsAsync(OwnerProjectScope.Owner, list, ct);
    }

    private async Task<OwnerWebhooksModel?> LoadAsync(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var webhooks = JsonSerializer.SerializeToElement(project.Webhooks ?? new Dictionary<string, object>());
        var defaultUrl = OwnerProjectScope.Text(webhooks, "defaultUrl") ?? project.DiscordWebhook ?? "";
        var profiles = new List<OwnerWebhookProfile>();
        if (webhooks.TryGetProperty("profiles", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (OwnerProjectScope.Text(item, "id") is not { Length: > 0 } id) continue;
                profiles.Add(new OwnerWebhookProfile(id, OwnerProjectScope.Text(item, "name") ?? id, OwnerProjectScope.Text(item, "url") ?? "",
                    OwnerProjectScope.Text(item, "color") ?? "#5865f2",
                    !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False));
            }
        }
        return new OwnerWebhooksModel(projectId, project.Name, defaultUrl, profiles, null);
    }
}
