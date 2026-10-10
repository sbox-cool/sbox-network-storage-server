using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

/// <param name="SshHost">Host shown in the coding-agent SSH command: this server's public host, or a placeholder when it is only reachable locally.</param>
public sealed record OwnerDashboardModel(IReadOnlyList<WorkspaceProject> Projects, string SshHost, string? Error = null)
{
    public static string SshHostFor(EffectiveConfig config, HttpRequest request)
    {
        var baseUrl = ServerBaseUrl.ForRequest(config, request);
        return !ServerBaseUrl.IsLoopback(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "my-vps";
    }
}

/// <param name="BaseUrl">server.public_url, or the origin the dashboard was opened with.</param>
/// <param name="FromPublicUrl">True when <paramref name="BaseUrl"/> comes from server.public_url.</param>
/// <param name="PublicKey">The first enabled public key, or null when the project has none.</param>
public sealed record OwnerConnectInfo(string BaseUrl, bool FromPublicUrl, string? PublicKey)
{
    public bool IsLoopback => ServerBaseUrl.IsLoopback(BaseUrl);
    public bool IsPlainHttp => BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}

public sealed record OwnerRequestSummary(DateTimeOffset At, string Method, string Path, int Status);

public sealed record OwnerProjectModel(WorkspaceProject Project, NetworkStorageProjectResources? Resources,
    IReadOnlyList<ApiKeyInfo> Keys, OwnerConnectInfo Connect, OwnerRequestSummary? LastRequest,
    IReadOnlyList<OwnerRequestSummary> RecentRejected, string? RawKey = null, string? Error = null);

/// <summary>Standalone adaptation of the Network Storage project/settings/key management surface.</summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDashboardController(INetworkStorageProjectService projects, IWorkspaceStore workspace,
    IAuditLogger audit, INetworkStorageStore store, EffectiveConfig config, IMemoryCache cache, TimeProvider time) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const int RecentRequests = 200;
    private static readonly TimeSpan RawKeyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Permission scopes enforced by ApiKeyPermissionPolicy for secret keys.</summary>
    public static readonly string[] KeyScopes = ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"];

    /// <summary>Access levels: r reads definitions, w writes them, x executes (tests, direct collection data API).</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyLevels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["none"] = "No access", ["r"] = "Read", ["rw"] = "Read and write", ["x"] = "Execute", ["rx"] = "Read and execute", ["rwx"] = "Full",
    };

    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Index() => Redirect("/dashboard");

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
        => View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(await workspace.GetUserProjectsAsync(Owner, ct), OwnerDashboardModel.SshHostFor(config, Request)));

    [HttpPost("/dashboard/projects")]
    public async Task<IActionResult> CreateProject([FromForm] string? name, [FromForm] string? description,
        [FromForm] bool requireSboxAuth, [FromForm] bool createPublicKey, CancellationToken ct)
    {
        name = name?.Trim();
        if (name is null || name.Length is < 1 or > 64 || description?.Length > 256)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Dashboard.cshtml", new OwnerDashboardModel(await workspace.GetUserProjectsAsync(Owner, ct), OwnerDashboardModel.SshHostFor(config, Request),
                "Project name is required (max 64 characters); description must be at most 256 characters."));
        }
        var result = await projects.CreateProjectAsync(Owner, name, description, true, requireSboxAuth, "player", string.Empty, ct);
        await AuditAsync(result.ProjectId, "project.create", new { name, description, requireSboxAuth }, ct);
        if (createPublicKey)
        {
            // Same label as `sbox-ns quickstart`; public keys stay visible in the key table, so no one-time notice.
            var (key, _) = await projects.CreateProjectKeyAsync(Owner, result.ProjectId, "Game client", "public", null, ct);
            await AuditAsync(result.ProjectId, "key.create", new { keyType = "public", key.Label }, ct);
        }
        return Redirect(ProjectUrl(result.ProjectId));
    }

    [HttpGet("/dashboard/projects/{projectId}")]
    public async Task<IActionResult> Project(string projectId, [FromQuery] string? created, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        // The raw key from the POST that redirected here; shown once, then forgotten.
        if (created is not null && cache.Get<(string ProjectId, string Raw)>(RawKeyCacheKey(created)) is { Raw: not null } stored
            && stored.ProjectId == projectId)
        {
            cache.Remove(RawKeyCacheKey(created));
            model = model with { RawKey = stored.Raw };
        }
        return View("~/Views/Owner/Project.cshtml", model);
    }

    [HttpPost("/dashboard/projects/{projectId}/settings")]
    public async Task<IActionResult> Settings(string projectId, [FromForm] string? tab, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        if (tab is not ("project" or "security" or "player-keys" or "legacy-projections" or "revisions")) return BadRequest("Unknown settings tab.");
        var form = await Request.ReadFormAsync(ct);
        var values = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        if (tab == "project" && (values.GetValueOrDefault("name")?.Trim().Length is not (>= 1 and <= 64)
            || values.GetValueOrDefault("description")?.Length > 256))
            return await ProjectErrorAsync(projectId, "Name must contain 1-64 characters; description may contain at most 256.", ct);
        if (tab == "player-keys" && values.GetValueOrDefault("playerKeyMode") is not ("player" or "playerSave"))
            return BadRequest("Invalid player key mode.");
        if (tab == "revisions" && (values.GetValueOrDefault("revisionEnforcementMode") is not ("force_upgrade" or "allow_continue")
            || values.GetValueOrDefault("revisionPostGraceAction") is not ("block_writes" or "block_all")
            || values.GetValueOrDefault("revisionNotifyMessage")?.Length > 500))
            return await ProjectErrorAsync(projectId, "Choose an enforcement mode and post-grace action; the notice may contain at most 500 characters.", ct);
        await projects.UpdateProjectSettingsAsync(Owner, projectId, tab, values, ct);
        await AuditAsync(projectId, "project.settings", new { tab }, ct);
        return Redirect(ProjectUrl(projectId));
    }

    [HttpPost("/dashboard/projects/{projectId}/keys")]
    public async Task<IActionResult> CreateKey(string projectId, [FromForm] string? label, [FromForm] string? keyType, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (keyType is not ("public" or "secret") || label is null || label.Trim().Length is < 1 or > 64 || model.Keys.Count >= 100)
            return await ProjectErrorAsync(projectId, "Choose public or secret, provide a label of 1-64 characters, and keep fewer than 100 keys.", ct);
        var (key, raw) = await projects.CreateProjectKeyAsync(Owner, projectId, label.Trim(), keyType, null, ct);
        await AuditAsync(projectId, "key.create", new { keyType, key.Label }, ct);
        // Post/redirect/get: a refresh must not create a second key. The raw key waits server-side for one view.
        var notice = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(RawKeyCacheKey(notice), (projectId, raw), RawKeyLifetime);
        return Redirect(ProjectUrl(projectId) + "?created=" + notice + "#api-keys");
    }

    [HttpPost("/dashboard/projects/{projectId}/keys/toggle")]
    public Task<IActionResult> ToggleKey(string projectId, [FromForm] string? key, CancellationToken ct)
        => ChangeKeyAsync(projectId, key, false, ct);

    [HttpPost("/dashboard/projects/{projectId}/keys/revoke")]
    public Task<IActionResult> RevokeKey(string projectId, [FromForm] string? key, CancellationToken ct)
        => ChangeKeyAsync(projectId, key, true, ct);

    [HttpPost("/dashboard/projects/{projectId}/keys/permissions")]
    public async Task<IActionResult> KeyPermissions(string projectId, [FromForm] string? keyIdentifier, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (keyIdentifier is null || !model.Keys.Any(key => key.KeyType == "secret" && key.KeyIdentifier == keyIdentifier)) return NotFound();
        var form = await Request.ReadFormAsync(ct);
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scope in KeyScopes)
        {
            var level = form["scope_" + scope].ToString();
            if (!KeyLevels.ContainsKey(level)) return await ProjectErrorAsync(projectId, $"Choose an access level for {scope}.", ct);
            permissions[scope] = level;
        }
        await projects.UpdateProjectKeyPermissionsAsync(Owner, projectId, keyIdentifier, permissions, ct);
        await AuditAsync(projectId, "key.permissions", new { keyIdentifier, permissions }, ct);
        return Redirect(ProjectUrl(projectId) + "#api-keys");
    }

    [HttpPost("/dashboard/projects/{projectId}/delete")]
    public async Task<IActionResult> DeleteProject(string projectId, [FromForm] string? confirmation, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        if (!string.Equals(confirmation, projectId, StringComparison.Ordinal))
            return await ProjectErrorAsync(projectId, "Type the exact project ID to confirm permanent deletion.", ct);
        await AuditAsync(projectId, "project.delete", new { projectId }, ct);
        await projects.DeleteProjectAsync(Owner, projectId, ct);
        return Redirect("/dashboard");
    }

    private async Task<IActionResult> ChangeKeyAsync(string projectId, string? key, bool revoke, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (key is null || !model.Keys.Any(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal))) return NotFound();
        if (revoke) await projects.RemoveProjectKeyAsync(Owner, projectId, key, ct);
        else await projects.ToggleProjectKeyAsync(Owner, projectId, key, ct);
        await AuditAsync(projectId, revoke ? "key.revoke" : "key.toggle", new { }, ct);
        return Redirect(ProjectUrl(projectId));
    }

    private async Task<OwnerProjectModel?> LoadProjectAsync(string projectId, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicUrl = ServerBaseUrl.PublicUrl(config);
        var connect = new OwnerConnectInfo(ServerBaseUrl.ForRequest(config, Request), publicUrl is not null,
            keys.Where(key => key.Enabled && key.KeyType == "public").OrderBy(key => key.CreatedAt).FirstOrDefault()?.Key);
        var requests = (await store.ListStorageRequestLogAsync(projectId, RecentRequests, ct)).Select(Summary).ToList();
        var since = time.GetUtcNow().AddDays(-1);
        var rejected = requests.Where(request => request.Status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
            && request.At >= since).ToList();
        return new OwnerProjectModel(access.Project, await projects.GetProjectResourcesForOwnerAsync(Owner, projectId, ct),
            keys, connect, requests.FirstOrDefault(), rejected);
    }

    private static OwnerRequestSummary Summary(JsonElement row) => new(
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("created_at_unix_ms").GetInt64()),
        row.GetProperty("method").GetString() ?? "", row.GetProperty("path").GetString() ?? "", row.GetProperty("status_code").GetInt32());

    private async Task<IActionResult> ProjectErrorAsync(string projectId, string error, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        var model = await LoadProjectAsync(projectId, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Project.cshtml", model with { Error = error });
    }

    private Task AuditAsync(string projectId, string action, object summary, CancellationToken ct)
        => audit.LogActionAsync(new AuditLogRequest(ProjectId: projectId, UserId: Owner.ToString(CultureInfo.InvariantCulture),
            Action: action, Actor: new { id = Owner, type = "owner-dashboard" }, Target: new { id = projectId, type = "project" },
            Summary: summary, Before: null, After: null), ct);

    private static string RawKeyCacheKey(string notice) => "owner-dashboard:raw-key:" + notice;

    private static string ProjectUrl(string projectId) => $"/dashboard/projects/{Uri.EscapeDataString(projectId)}";
}
