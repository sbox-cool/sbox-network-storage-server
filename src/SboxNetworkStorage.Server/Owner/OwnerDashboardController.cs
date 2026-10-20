using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.GameLogs;
using SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Authority;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Updates.ReleaseStatus;

namespace SboxNetworkStorage.Server.Owner;

/// <param name="LiveRevision">The synced game package revision, or null when the project never synced.</param>
/// <param name="LastRequestAt">When the game last called this server, or null when it never did.</param>
public sealed record OwnerProjectCard(WorkspaceProject Project, long? LiveRevision, DateTimeOffset? LastRequestAt);

/// <param name="Cards">One card per project, ordered by name.</param>
/// <param name="SshHost">Host shown in the coding-agent SSH command: this server's public host, or a placeholder when it is only reachable locally.</param>
/// <param name="Query">The current <c>?q=</c> filter text, if any.</param>
/// <param name="OpenDialog">Dialog id (<c>create-project</c>, <c>import</c>, <c>export</c>) to render open: the no-JS target or the re-opened dialog after a validation error.</param>
/// <param name="CreateName">The create-project name to preserve when re-rendering after a validation error.</param>
/// <param name="CreateDescription">The create-project description to preserve when re-rendering after a validation error.</param>
/// <param name="ReleaseStatus">Library and server release status, or null when the status card is off.</param>
public sealed record OwnerDashboardModel(IReadOnlyList<OwnerProjectCard> Cards, string SshHost, string? Error = null,
    string? Query = null, string? OpenDialog = null, string? CreateName = null, string? CreateDescription = null, int TotalProjects = 0,
    OwnerReleaseStatusCard? ReleaseStatus = null)
{
    public IReadOnlyList<WorkspaceProject> Projects => Cards.Select(card => card.Project).ToList();

    public static string SshHostFor(EffectiveConfig config, HttpRequest request)
    {
        var baseUrl = ServerBaseUrl.ForRequest(config, request);
        return !ServerBaseUrl.IsLoopback(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "my-vps";
    }

    /// <summary>Server-side <c>?q=</c> matching, mirrored by the client enhancement: name or id, case-insensitive substring.</summary>
    public static bool Matches(OwnerProjectCard card, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        return card.Project.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || card.Project.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
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

public sealed record OwnerFetchBlockedHost(string Host, string Reason, string Endpoint, DateTimeOffset LastSeen);

public sealed record OwnerProjectModel(WorkspaceProject Project, NetworkStorageProjectResources? Resources,
    IReadOnlyList<ApiKeyInfo> Keys, OwnerConnectInfo Connect, OwnerRequestSummary? LastRequest,
    IReadOnlyList<OwnerRequestSummary> RecentRejected, AuthorityCheckService.AuthorityCheckResult? Authority = null, string? RawKey = null, string? Error = null,
    RequestEncryptionKeyInfo? EncryptionKey = null, string? SigningKeyFingerprint = null);

/// <summary>Standalone adaptation of the Network Storage project/settings/key management surface.</summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDashboardController(INetworkStorageProjectService projects, IWorkspaceStore workspace,
    IAuditLogger audit, INetworkStorageStore store, EffectiveConfig config, IMemoryCache cache, TimeProvider time, GameLogQueue gameLogs,
    ReleaseStatusService releaseStatus, RequestEncryptionKeyStore encryptionKeys, OwnerAccountService accounts, IErrorArchive errorArchive) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const int RecentRequests = 200;
    private static readonly TimeSpan RawKeyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Permission scopes enforced by ApiKeyPermissionPolicy for secret keys.</summary>
    public static readonly string[] KeyScopes = ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings", "logs"];

    /// <summary>Access levels: r reads definitions, w writes them, x executes (tests, direct collection data API).</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyLevels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["none"] = "No access", ["r"] = "Read", ["rw"] = "Read and write", ["x"] = "Execute", ["rx"] = "Read and execute", ["rwx"] = "Full",
    };

    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Index() => Redirect("/dashboard");

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] string? q, [FromQuery] string? dialog, CancellationToken ct)
    {
        var open = DialogId(dialog);
        if (open is not null) ViewData["OpenDialog"] = open;
        return View("~/Views/Owner/Dashboard.cshtml", await DashboardModelAsync(q, null, open, null, null, ct));
    }

    [HttpPost("/dashboard/projects")]
    public async Task<IActionResult> CreateProject([FromForm] string? name, [FromForm] string? description,
        [FromForm] bool requireSboxAuth, [FromForm] bool createPublicKey, [FromForm] string? hostingProfile, CancellationToken ct)
    {
        name = name?.Trim();
        if (name is null || name.Length is < 1 or > 64 || description?.Length > 256)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            ViewData["OpenDialog"] = "create-project";
            return View("~/Views/Owner/Dashboard.cshtml", await DashboardModelAsync(null,
                "Project name is required (max 64 characters); description must be at most 256 characters.",
                "create-project", name, description, ct));
        }
        var profile = hostingProfile is "dedicated" or "hybrid" or "unset" ? hostingProfile : "player-hosted";
        var result = await projects.CreateProjectAsync(Owner, name, description, true, requireSboxAuth, "player", string.Empty, ct, profile);
        await AuditAsync(result.ProjectId, "project.create", new { name, description, requireSboxAuth, hostingProfile = profile }, ct);
        if (createPublicKey)
        {
            // Same label as `sbox-ns quickstart`; public keys stay visible in the key table, so no one-time notice.
            var (key, _) = await projects.CreateProjectKeyAsync(Owner, result.ProjectId, "Game client", "public", null, ct);
            await AuditAsync(result.ProjectId, "key.create", new { keyType = "public", key.Label }, ct);
        }
        OwnerFlash.Success(this, $"Project {name} created.");
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
        ViewData["GameLogCounters"] = gameLogs.Snapshot(projectId);
        ViewData["FetchBlocked"] = await FetchBlockedHostsAsync(model.Project, ct);
        return View("~/Views/Owner/Project.cshtml", model);
    }

    /// <summary>
    /// Recently refused <c>ns.fetch</c> calls for this project, newest first, one row per host and reason. A host the
    /// project allows now drops out, so a row stays only while the call would still be refused.
    /// </summary>
    private async Task<IReadOnlyList<OwnerFetchBlockedHost>> FetchBlockedHostsAsync(WorkspaceProject project, CancellationToken ct)
    {
        var archived = await errorArchive.ListAsync(new InternalErrorArchiveQuery(ProjectId: project.Id, Limit: 200), ct);
        var rows = new List<OwnerFetchBlockedHost>();
        foreach (var error in archived.Errors)
        {
            if (error.Classification != EndpointErrorReporter.FetchBlockedClassification || error.Tags is null)
                continue;
            var host = TagValue(error.Tags, EndpointErrorReporter.FetchHostTagPrefix);
            var reason = TagValue(error.Tags, EndpointErrorReporter.FetchReasonTagPrefix);
            if (host is null || reason is null)
                continue;
            if (reason == "host_not_allowed" && (project.FetchAllowAllPublic == true || FetchHostRules.IsAllowed(host, project.FetchAllowedHosts ?? [])))
                continue;
            var endpoint = error.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            rows.Add(new OwnerFetchBlockedHost(host, reason, endpoint, error.Timestamp));
        }
        return rows.DistinctBy(row => (row.Host, row.Reason)).ToList();
    }

    private static string? TagValue(IReadOnlyList<string> tags, string prefix) =>
        tags.FirstOrDefault(tag => tag.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static bool GameLogFormValid(Dictionary<string, string> values)
    {
        foreach (var stream in new[] { GameLogPolicy.DefaultStream, GameLogPolicy.ChatStream, GameLogPolicy.AuditStream })
        {
            var retention = values.GetValueOrDefault($"gameLogs_{stream}_retention");
            var sampling = values.GetValueOrDefault($"gameLogs_{stream}_sampling");
            if (!string.IsNullOrWhiteSpace(retention) && GameLogSettingsForm.ParseRetention(retention) is null) return false;
            if (!string.IsNullOrWhiteSpace(sampling) && GameLogSettingsForm.ParseSampling(sampling) is null) return false;
        }

        var rate = values.GetValueOrDefault("gameLogsRateLimitPerMinute");
        return string.IsNullOrWhiteSpace(rate) || GameLogSettingsForm.ParseRateLimit(rate) is not null;
    }

    [HttpPost("/dashboard/projects/{projectId}/settings")]
    public async Task<IActionResult> Settings(string projectId, [FromForm] string? tab, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        if (tab is not ("project" or "security" or "player-keys" or "legacy-projections" or "revisions" or "game-logs" or "analytics" or "media" or "fetch" or "fetch-allow-host")) return BadRequest("Unknown settings tab.");
        var form = await Request.ReadFormAsync(ct);
        var values = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        if (tab == "project" && (values.GetValueOrDefault("name")?.Trim().Length is not (>= 1 and <= 64)
            || values.GetValueOrDefault("description")?.Length > 256))
            return await ProjectErrorAsync(projectId, "Name must contain 1-64 characters; description may contain at most 256.", ct);
        if (tab == "player-keys" && values.GetValueOrDefault("playerKeyMode") is not ("player" or "playerSave"))
            return BadRequest("Invalid player key mode.");
        if (tab == "revisions" && (values.GetValueOrDefault("revisionEnforcementMode") is not ("force_upgrade" or "allow_continue")
            || values.GetValueOrDefault("revisionPostGraceAction") is not ("block_writes" or "block_all")
            || values.GetValueOrDefault("revisionInGraceAction") is not (null or "" or "continue" or "notify" or "rejoin")
            || values.GetValueOrDefault("revisionNotifyMessage")?.Length > 500
            || !IsOptionalLong(values.GetValueOrDefault("revisionMinimumRevisionId"))
            || !IsOptionalIntRange(values.GetValueOrDefault("revisionGracePeriodMinutes"), 0, 10080)
            || !IsOptionalIntRange(values.GetValueOrDefault("revisionRetentionCount"), 0, 20)))
            return await ProjectErrorAsync(projectId, "Choose an enforcement mode, in-grace and post-grace action, a minimum revision, a grace period of 0-10080 minutes and retention of 0-20; the notice may contain at most 500 characters.", ct);
        if (tab == "game-logs" && !GameLogFormValid(values))
            return await ProjectErrorAsync(projectId, "Game-log retention must be 1-3650 days, sampling in (0, 1], and the rate limit at least 1 entry per minute.", ct);
        if (tab == "analytics" && PlayerAnalyticsAllowedEvents.ParseForm(values.GetValueOrDefault("analyticsAllowedEventTypes")) is null)
            return await ProjectErrorAsync(projectId, $"Custom event names may contain only lowercase letters, digits, '_', '.', ':' and '-', up to {PlayerAnalyticsAllowedEvents.MaxNameLength} characters each, at most {PlayerAnalyticsAllowedEvents.MaxNames} names. Write spaces as '_'.", ct);
        try
        {
            await projects.UpdateProjectSettingsAsync(Owner, projectId, tab, values, ct);
        }
        catch (MediaPolicyValidationException ex)
        {
            // Refused before anything was written; show the reason on the project page.
            return await ProjectErrorAsync(projectId, ex.Failure.Message, ct);
        }
        catch (FetchPolicyValidationException ex)
        {
            // Refused before anything was written; show the reason on the project page.
            return await ProjectErrorAsync(projectId, ex.Message, ct);
        }
        await AuditAsync(projectId, "project.settings", new { tab }, ct);
        OwnerFlash.Success(this, "Settings saved");
        return Redirect(ProjectUrl(projectId));
    }

    /// <summary>Rotates the server-wide request encryption key. The previous key stays valid for the grace period. Needs the owner's password, and the authenticator or a recovery code when enabled, as for export.</summary>
    [HttpPost("/dashboard/projects/{projectId}/encryption/rotate")]
    public async Task<IActionResult> RotateEncryptionKey(string projectId, [FromForm] string? graceSeconds,
        [FromForm] string? password, [FromForm] string? secondFactor, CancellationToken ct)
    {
        if (await LoadProjectAsync(projectId, ct) is null) return NotFound();
        try
        {
            if (!await accounts.ConfirmAsync(User.Identity!.Name!, User.FindFirstValue(OwnerHostingExtensions.StampClaim), password, secondFactor, ct))
                return await ProjectErrorAsync(projectId, "Enter your password, and your authenticator or recovery code if you enabled one.", ct);
        }
        catch (OwnerAuthenticatorUnavailableException)
        {
            return await ProjectErrorAsync(projectId, "This server cannot read your authenticator. Use a recovery code, or run sbox-ns admin reset-2fa on the server.", ct);
        }
        if (!int.TryParse(graceSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var grace)
            || grace < 0 || grace > RequestEncryptionKeyring.MaxGraceSeconds)
            return await ProjectErrorAsync(projectId, $"Grace period must be a whole number of seconds from 0 to {RequestEncryptionKeyring.MaxGraceSeconds}.", ct);
        var previousKeyId = encryptionKeys.Current().Current.KeyId;
        var keyring = RequestEncryptionKeyring.Rotate(encryptionKeys.Path, time.GetUtcNow(), grace);
        encryptionKeys.Reload();
        await AuditAsync(projectId, "encryption.key.rotate", new { previousKeyId, keyId = keyring.Current.KeyId, graceSeconds = grace }, ct);
        OwnerFlash.Success(this, $"Request encryption key rotated. The previous key stays valid for {grace} seconds.");
        return Redirect(ProjectUrl(projectId) + "#project-settings");
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
        OwnerFlash.Success(this, $"API key {key.Label} created.");
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
        OwnerFlash.Success(this, "Key permissions saved.");
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
        OwnerFlash.Success(this, "Project deleted.");
        return Redirect("/dashboard");
    }

    private async Task<IActionResult> ChangeKeyAsync(string projectId, string? key, bool revoke, CancellationToken ct)
    {
        var model = await LoadProjectAsync(projectId, ct);
        if (model is null) return NotFound();
        if (key is null || !model.Keys.Any(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal))) return NotFound();
        if (revoke && !OwnerConfirm.IsConfirmed(Request)) return OwnerConfirm.Page(this);
        var label = model.Keys.First(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal)).Label;
        if (revoke) await projects.RemoveProjectKeyAsync(Owner, projectId, key!, ct);
        else await projects.ToggleProjectKeyAsync(Owner, projectId, key!, ct);
        await AuditAsync(projectId, revoke ? "key.revoke" : "key.toggle", new { }, ct);
        OwnerFlash.Success(this, revoke ? $"API key {label} revoked." : $"API key {label} updated.");
        return Redirect(ProjectUrl(projectId));
    }

    private static string? DialogId(string? dialog) => dialog switch
    {
        "create-project" or "import" or "export" => dialog,
        _ => null,
    };

    private async Task<OwnerDashboardModel> DashboardModelAsync(string? query, string? error, string? openDialog,
        string? createName, string? createDescription, CancellationToken ct)
    {
        query = query?.Trim() ?? "";
        var cards = await OwnerProjectCards.LoadAsync(workspace, store, Owner, ct);
        var total = cards.Count;
        if (query.Length > 0) cards.RemoveAll(card => !OwnerDashboardModel.Matches(card, query));
        var releaseCard = releaseStatus.Enabled ? OwnerReleaseStatusCard.From(releaseStatus, time.GetUtcNow()) : null;
        releaseStatus.RequestRefresh();
        return new OwnerDashboardModel(cards, OwnerDashboardModel.SshHostFor(config, Request), error,
            query.Length == 0 ? null : query, openDialog, createName, createDescription, total, releaseCard);
    }

    private async Task<OwnerProjectModel?> LoadProjectAsync(string projectId, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return null;
        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicUrl = ServerBaseUrl.PublicUrl(config);
        var connect = new OwnerConnectInfo(ServerBaseUrl.ForRequest(config, Request), publicUrl is not null,
            keys.Where(key => key.Enabled && key.KeyType == "public").OrderBy(key => key.CreatedAt).FirstOrDefault()?.Key);
        var rawRequests = await store.ListStorageRequestLogAsync(projectId, RecentRequests, ct);
        var requests = rawRequests.Select(Summary).ToList();
        var since = time.GetUtcNow().AddDays(-1);
        var rejected = requests.Where(request => request.Status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
            && request.At >= since).ToList();
        var resources = await projects.GetProjectResourcesForOwnerAsync(Owner, projectId, ct);
        var authority = AuthorityCheckService.AnalyzeLoaded(access.Project.HostingProfile, resources, keys, rawRequests);
        return new OwnerProjectModel(access.Project, resources,
            keys, connect, requests.FirstOrDefault(), rejected, authority, EncryptionKey: encryptionKeys.Current().Current,
            SigningKeyFingerprint: NetworkStorageSecurityConfigSigningKey.Fingerprint);
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

    // Phase 4 (game-version-policy): empty means unset; otherwise a bounded integer.
    private static bool IsOptionalIntRange(string? value, int min, int max)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase)
            || (int.TryParse(value, out var parsed) && parsed >= min && parsed <= max);

    private static bool IsOptionalLong(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase)
            || (long.TryParse(value, out var parsed) && parsed >= 0);

     private static string ProjectUrl(string projectId) => $"/dashboard/projects/{Uri.EscapeDataString(projectId)}";
}
