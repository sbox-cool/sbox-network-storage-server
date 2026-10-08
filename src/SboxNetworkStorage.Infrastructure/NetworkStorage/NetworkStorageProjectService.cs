using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

// CDN key-index types.
public sealed record CdnKeyIndex(IReadOnlyList<CdnKeyIndexEntry> Keys);
public sealed record CdnKeyIndexEntry(string Key, string KeyType, bool Enabled, string? KeyIdentifier, DateTimeOffset CreatedAt);
public sealed class NetworkStorageProjectService(
    IBunnyWorkspaceClient bunnyWorkspaceClient,
    IWorkspaceStorageEnumerator bunnyStorageEnumerator,
    IConfiguration configuration,
    IStorageKeyCdnWriter keyCdnWriter,
    INetworkStorageStore scyllaStore,
    ILogger<NetworkStorageProjectService> logger)
    : INetworkStorageProjectService
{
    // Most-recent audit-log entries pulled from ScyllaDB for the dashboard
    // before in-memory filtering/pagination. Older entries live in the legacy
    // Bunny CDN log files (read only when ScyllaDB returns none).
    private const int ScyllaAuditLogReadLimit = 2000;

    public async Task<NetworkStorageProjectCreateResult> CreateProjectAsync(
        long userId,
        string name,
        string? description,
        bool enabled,
        bool requireSboxAuth,
        string keyMode,
        string organizationId,
        CancellationToken cancellationToken)
    {
        var projects = await bunnyWorkspaceClient.GetUserProjectsAsync(userId, cancellationToken);

        var projectId = GenerateProjectId();
        var now = DateTimeOffset.UtcNow;

        var project = new BunnyProject(
            Id: projectId,
            Name: name,
            Description: string.IsNullOrEmpty(description) ? null : description[..Math.Min(description.Length, 256)],
            Enabled: enabled,
            CreatedAt: now,
            UpdatedAt: now,
            CompiledAt: null,
            RequireSboxAuth: requireSboxAuth,
            PlayerKeyMode: keyMode
        );

        var updatedProjects = projects.Concat([project]).ToList();
        await bunnyWorkspaceClient.SaveUserProjectsAsync(userId, updatedProjects, cancellationToken);
        return new NetworkStorageProjectCreateResult(projectId);
    }

    // Self-hosted servers have no organizations: the owner's project list is authoritative.
    public async Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(
        long userId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var projects = await bunnyWorkspaceClient.GetUserProjectsAsync(userId, cancellationToken);
        var project = projects.FirstOrDefault(p =>
            string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));

        return project is null
            ? null
            : await BuildAccessResultAsync(project, organization: null, userId, organizationId: null, canManage: true, cancellationToken);
    }

    /// <summary>
    /// Builds a project access result, running the three independent lookups —
    /// resource counts (which already fan out internally), team-member count, and
    /// rate-limit rules — concurrently instead of one after another. The
    /// rate-limit-rules read is skipped entirely when endpoint rate limits are
    /// already known to be enabled from project metadata.
    /// </summary>
    private async Task<NetworkStorageProjectAccessResult> BuildAccessResultAsync(
        BunnyProject project,
        WorkspaceInfo? organization,
        long storageOwnerUserId,
        string? organizationId,
        bool canManage,
        CancellationToken cancellationToken)
    {
        var countsTask = LoadResourceCountsAsync(storageOwnerUserId, project.Id, cancellationToken);
        var teamTask = CountTeamMembersAsync(storageOwnerUserId, project.Id, organizationId, cancellationToken);
        var endpointRateLimitsEnabled = HasEnabledEndpointRateLimits(project.EndpointRateLimits);
        var rateRulesTask = endpointRateLimitsEnabled
            ? null
            : ReadRateLimitRulesAsync(storageOwnerUserId, project.Id, cancellationToken);

        await Task.WhenAll(rateRulesTask is null
            ? new Task[] { countsTask, teamTask }
            : new Task[] { countsTask, teamTask, rateRulesTask });

        var resourceCounts = await countsTask;
        var teamMemberCount = await teamTask;
        var hasRateLimits = endpointRateLimitsEnabled
            || (rateRulesTask is not null && (await rateRulesTask).Any(rule => rule.Enabled));

        return new NetworkStorageProjectAccessResult(
            Project: project,
            Organization: organization,
            StorageOwnerUserId: storageOwnerUserId,
            CollectionCount: resourceCounts.CollectionCount,
            ApiKeyCount: resourceCounts.ApiKeyCount,
            TeamMemberCount: teamMemberCount,
            QueryCount: resourceCounts.QueryCount,
            WorkflowCount: resourceCounts.WorkflowCount,
            EndpointCount: resourceCounts.EndpointCount,
            RequireSboxAuth: project.RequireSboxAuth ?? true,
            PlayerKeyMode: project.PlayerKeyMode ?? "player",
            HasRateLimits: hasRateLimits,
            CanManage: canManage,
            HeartbeatStatus: null,
            HeartbeatColor: null,
            HeartbeatText: null);
    }

    public async Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken)
    {
        var access = await ResolveProjectAccessAsync(userId, projectId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        return await LoadProjectResourcesAsync(access.StorageOwnerUserId, projectId, cancellationToken);
    }

    public async Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(
        long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        return await LoadProjectResourcesAsync(storageOwnerUserId, projectId, cancellationToken);
    }

    public async Task<NetworkStorageTeamData?> GetProjectTeamAsync(
        long storageOwnerUserId,
        string projectId,
        string? organizationId,
        string callerRole,
        CancellationToken cancellationToken)
    {
        var owner = await LoadTeamOwnerAsync(storageOwnerUserId, cancellationToken);
        if (owner is null)
        {
            return null;
        }

        var isOrganizationProject = !string.IsNullOrWhiteSpace(organizationId);
        var members = isOrganizationProject
            ? await LoadOrganizationTeamMembersAsync(organizationId!, cancellationToken)
            : await LoadProjectTeamMembersAsync(storageOwnerUserId, projectId, cancellationToken);

        return new NetworkStorageTeamData(
            Owner: owner,
            Members: members,
            CallerRole: callerRole,
            TeamMode: isOrganizationProject ? "org" : "legacy");
    }

    // Self-hosted servers have a single owner and no team/organization membership.
    private static Task<int> CountTeamMembersAsync(
        long storageOwnerUserId,
        string projectId,
        string? organizationId,
        CancellationToken cancellationToken)
        => Task.FromResult(1);

    private static Task<NetworkStorageTeamUser?> LoadTeamOwnerAsync(
        long storageOwnerUserId,
        CancellationToken cancellationToken)
        => Task.FromResult<NetworkStorageTeamUser?>(new NetworkStorageTeamUser(
            Id: storageOwnerUserId,
            Username: "owner",
            Email: string.Empty,
            DisplayName: "Server owner",
            AvatarUrl: null));

    private static Task<IReadOnlyList<NetworkStorageTeamMember>> LoadProjectTeamMembersAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NetworkStorageTeamMember>>([]);

    private static Task<IReadOnlyList<NetworkStorageTeamMember>> LoadOrganizationTeamMembersAsync(
        string organizationId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NetworkStorageTeamMember>>([]);

    // ── Keys ──

    public async Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        // ScyllaDB is the sole source for API keys.
        IReadOnlyList<JsonElement> rows;
        try
        {
            rows = await scyllaStore.ListApiKeysAsync(projectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ScyllaDB unavailable while listing API keys for project {ProjectId}; returning empty list", projectId);
            return [];
        }
        if (rows.Count > 0)
        {
            return rows
                .Where(r => r.ValueKind == JsonValueKind.Object
                    && string.Equals(ReadJsonString(r, "user_id"), storageOwnerUserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                .Select(r => new ApiKeyInfo(
                    Key: ReadJsonString(r, "api_key"),
                    Label: ReadJsonString(r, "label"),
                    KeyType: ReadJsonString(r, "key_type"),
                    Enabled: r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                    KeyIdentifier: ReadJsonNullableString(r, "key_identifier"),
                    CreatedAt: r.TryGetProperty("updated_at_unix_ms", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue,
                    Permissions: ApiKeyPermissionsParser.Parse(
                        r.TryGetProperty("permissions_json", out var pj) && pj.ValueKind == JsonValueKind.String ? pj.GetString() : null)))
                .OrderByDescending(k => k.CreatedAt)
                .ToList();
        }

        // Local dev fallback: no database configured at all.
        var keys = await bunnyWorkspaceClient.GetProjectResourceAsync<List<ApiKeyInfo>>(
            storageOwnerUserId, projectId, "keys.json", cancellationToken);
        return keys is { Count: > 0 }
            ? keys.OrderByDescending(k => k.CreatedAt).ToList()
            : [];
    }

    private static string ReadJsonString(JsonElement row, string name)
        => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    private static string? ReadJsonNullableString(JsonElement row, string name)
        => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long ReadJsonLong(JsonElement row, string name)
        => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : 0;

    private static bool ReadJsonBool(JsonElement row, string name)
        => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private async Task<JsonElement?> FindScyllaApiKeyRowAsync(long userId, string projectId, string key, CancellationToken cancellationToken)
    {
        var rows = await scyllaStore.ListApiKeysAsync(projectId, cancellationToken);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(ReadJsonString(row, "user_id"), userId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                continue;

            var apiKey = ReadJsonString(row, "api_key");
            if (string.Equals(apiKey, key, StringComparison.Ordinal))
            {
                return row;
            }
        }
        return null;
    }

    private async Task UpsertScyllaApiKeyRowAsync(
        JsonElement row,
        string projectId,
        bool? enabledOverride,
        Dictionary<string, string>? permissionsOverride,
        CancellationToken cancellationToken)
    {
        var permissionsJson = permissionsOverride is not null
            ? JsonSerializer.SerializeToElement(permissionsOverride)
            : (row.TryGetProperty("permissions_json", out var pj) && pj.ValueKind == JsonValueKind.String
                ? JsonSerializer.SerializeToElement(ApiKeyPermissionsParser.Parse(pj.GetString()) ?? new Dictionary<string, string>())
                : JsonSerializer.SerializeToElement(new Dictionary<string, string>()));

        await scyllaStore.UpsertApiKeyAsync(
            projectId,
            ReadJsonString(row, "api_key"),
            ReadJsonString(row, "user_id"),
            ReadJsonString(row, "key_type"),
            ReadJsonString(row, "key_hash"),
            ReadJsonString(row, "key_identifier"),
            ReadJsonString(row, "label"),
            enabledOverride ?? ReadJsonBool(row, "enabled"),
            permissionsJson,
            Math.Max(1, ReadJsonLong(row, "version") + 1),
            cancellationToken);
    }

    public async Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(
        long storageOwnerUserId,
        string projectId,
        string label,
        string keyType,
        Dictionary<string, string>? permissions,
        CancellationToken cancellationToken)
    {
        // Enforce the per-project key cap against the authoritative Postgres count,
        // not the (possibly cached / fallback) list returned by GetProjectKeysAsync.
        if (await CountProjectApiKeysAsync(storageOwnerUserId, projectId, cancellationToken) >= 100)
        {
            throw new InvalidOperationException("Maximum 100 API keys per project.");
        }

        string rawKey;
        string keyHash;
        string keyIdentifier;
        string displayKey;

        if (keyType == "secret")
        {
            rawKey = "sbox_sk_" + GenerateCryptoString(48);
            keyIdentifier = StorageKeyCrypto.DeriveSecretKeyIdentifier(rawKey, projectId, configuration)!;
            if (keyIdentifier is null)
            {
                throw new InvalidOperationException("STORAGE_ENCRYPTION_KEY is required for secret key creation.");
            }

            keyHash = StorageKeyCrypto.HashSecretKey(rawKey);
            displayKey = StorageKeyCrypto.MaskSecretKey(rawKey);

            var perms = permissions ?? DefaultPermissions();
            await InsertApiKeyAsync(storageOwnerUserId, projectId, displayKey, label, keyType, keyHash, keyIdentifier, perms, cancellationToken);
            try
            {
                await keyCdnWriter.WriteSecretKeyFileAsync(keyIdentifier, keyHash, storageOwnerUserId, projectId, perms, cancellationToken);
                await AppendKeyToIndexAsync(projectId, new CdnKeyIndexEntry(displayKey, keyType, true, keyIdentifier, DateTimeOffset.UtcNow), cancellationToken);
            }
            catch
            {
                await DeleteApiKeyAsync(storageOwnerUserId, projectId, displayKey, cancellationToken);
                throw;
            }

            var keyEntry = new ApiKeyInfo(
                Key: displayKey,
                Label: label,
                KeyType: keyType,
                Enabled: true,
                KeyIdentifier: keyIdentifier,
                CreatedAt: DateTimeOffset.UtcNow,
                Permissions: perms
            );

            return (keyEntry, rawKey);
        }
        else
        {
            rawKey = "sbox_ns_" + GenerateCryptoString(48);
            keyIdentifier = GenerateId("pkey_");

            var perms = DefaultPermissions();
            await InsertApiKeyAsync(storageOwnerUserId, projectId, rawKey, label, keyType, null, keyIdentifier, perms, cancellationToken);
            try
            {
                await keyCdnWriter.WritePublicKeyFileAsync(rawKey, storageOwnerUserId, projectId, true, keyType, cancellationToken);
                await AppendKeyToIndexAsync(projectId, new CdnKeyIndexEntry(rawKey, keyType, true, keyIdentifier, DateTimeOffset.UtcNow), cancellationToken);
            }
            catch
            {
                await DeleteApiKeyAsync(storageOwnerUserId, projectId, rawKey, cancellationToken);
                throw;
            }

            var keyEntry = new ApiKeyInfo(
                Key: rawKey,
                Label: label,
                KeyType: keyType,
                Enabled: true,
                KeyIdentifier: keyIdentifier,
                CreatedAt: DateTimeOffset.UtcNow,
                Permissions: perms
            );

            return (keyEntry, rawKey);
        }
    }

    public async Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
    {
        var row = await FindScyllaApiKeyRowAsync(storageOwnerUserId, projectId, key, cancellationToken);
        if (row is not { ValueKind: JsonValueKind.Object })
        {
            return;
        }

        var newEnabled = !ReadJsonBool(row.Value, "enabled");
        await UpsertScyllaApiKeyRowAsync(row.Value, projectId, newEnabled, null, cancellationToken);

        if (key.StartsWith("sbox_sk_", StringComparison.OrdinalIgnoreCase))
        {
            var identifier = ReadJsonNullableString(row.Value, "key_identifier");
            if (identifier is not null)
            {
                await keyCdnWriter.UpdateSecretKeyFileAsync(identifier, projectId,
                    new Dictionary<string, object> { ["enabled"] = newEnabled }, cancellationToken);
                await UpdateKeyInIndexByIdentifierAsync(projectId, identifier, newEnabled, cancellationToken);
            }
        }
        else
        {
            await keyCdnWriter.WritePublicKeyFileAsync(key, storageOwnerUserId, projectId, newEnabled, "public", cancellationToken);
            await UpdateKeyInIndexByKeyAsync(projectId, key, newEnabled, cancellationToken);
        }
    }

    public async Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
    {
        var row = await FindScyllaApiKeyRowAsync(storageOwnerUserId, projectId, key, cancellationToken);
        if (row is not { ValueKind: JsonValueKind.Object })
        {
            return;
        }

        if (key.StartsWith("sbox_sk_", StringComparison.OrdinalIgnoreCase))
        {
            var identifier = ReadJsonNullableString(row.Value, "key_identifier");
            await scyllaStore.DeleteApiKeyAsync(projectId, ReadJsonString(row.Value, "api_key"), cancellationToken);
            if (identifier is not null)
            {
                await keyCdnWriter.DeleteSecretKeyFileAsync(identifier, projectId, cancellationToken);
                await RemoveKeyFromIndexByIdentifierAsync(projectId, identifier, cancellationToken);
            }
        }
        else
        {
            await scyllaStore.DeleteApiKeyAsync(projectId, ReadJsonString(row.Value, "api_key"), cancellationToken);
            await keyCdnWriter.DeletePublicKeyFileAsync(key, projectId, cancellationToken);
            await RemoveKeyFromIndexByKeyAsync(projectId, key, cancellationToken);
        }
    }

    public async Task UpdateProjectKeyPermissionsAsync(
        long storageOwnerUserId,
        string projectId,
        string keyIdentifier,
        Dictionary<string, string> permissions,
        CancellationToken cancellationToken)
    {
        var rows = await scyllaStore.ListApiKeysAsync(projectId, cancellationToken);
        var row = rows.FirstOrDefault(r => r.ValueKind == JsonValueKind.Object
            && string.Equals(ReadJsonString(r, "user_id"), storageOwnerUserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && string.Equals(ReadJsonString(r, "key_identifier"), keyIdentifier, StringComparison.Ordinal));
        if (row.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        await UpsertScyllaApiKeyRowAsync(row, projectId, null, permissions, cancellationToken);
        await keyCdnWriter.UpdateSecretKeyFileAsync(keyIdentifier, projectId,
            new Dictionary<string, object> { ["permissions"] = permissions }, cancellationToken);
        await UpdateKeyPermissionsInIndexAsync(projectId, keyIdentifier, permissions, cancellationToken);
    }

    public async Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        var projects = await bunnyWorkspaceClient.GetUserProjectsAsync(storageOwnerUserId, cancellationToken);
        var updated = projects.Where(p => !string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
        await bunnyWorkspaceClient.SaveUserProjectsAsync(storageOwnerUserId, updated, cancellationToken);
    }

    // ── Settings ──

    public async Task UpdateProjectSettingsAsync(
        long storageOwnerUserId,
        string projectId,
        string settingsTab,
        Dictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        var projects = (await bunnyWorkspaceClient.GetUserProjectsAsync(storageOwnerUserId, cancellationToken)).ToList();
        var idx = projects.FindIndex(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        var project = projects[idx];

        switch (settingsTab)
        {
            case "project":
                {
                    var name = (formValues.GetValueOrDefault("name") ?? "").Trim();
                    if (!string.IsNullOrEmpty(name) && name.Length <= 64)
                        project = project with { Name = name };
                    var desc = (formValues.GetValueOrDefault("description") ?? "").Trim();
                    project = project with { Description = desc.Length > 256 ? desc[..256] : string.IsNullOrEmpty(desc) ? null : desc };
                    project = project with { Enabled = IsChecked(formValues.GetValueOrDefault("enabled")) };
                    break;
                }
            case "security":
                {
                    project = project with
                    {
                        RequireSboxAuth = IsChecked(formValues.GetValueOrDefault("requireSboxAuth")),
                        EnableAuthSessions = IsChecked(formValues.GetValueOrDefault("enableAuthSessions")),
                        AuthSessionTtlSeconds = ParseIntOrDefault(formValues.GetValueOrDefault("authSessionTtlSeconds"), 3600, 60, 86400),
                        EnableEncryptedRequests = IsChecked(formValues.GetValueOrDefault("enableEncryptedRequests")),
                        EncryptedRequestWindowSeconds = ParseIntOrDefault(formValues.GetValueOrDefault("encryptedRequestWindowSeconds"), 120, 30, 600)
                    };
                    break;
                }
            case "player-keys":
                {
                    var keyMode = formValues.GetValueOrDefault("playerKeyMode");
                    if (keyMode == "player" || keyMode == "playerSave")
                        project = project with { PlayerKeyMode = keyMode };
                    break;
                }
            case "webhooks":
                {
                    var wh = new Dictionary<string, object>();
                    var defaultUrl = formValues.GetValueOrDefault("webhookDefault") ?? "";
                    if (!string.IsNullOrEmpty(defaultUrl))
                        wh["defaultUrl"] = defaultUrl;

                    var webhookTypes = new[] { "errors", "rateLimit", "auth", "saves", "ledger" };
                    foreach (var t in webhookTypes)
                    {
                        var enabled = IsChecked(formValues.GetValueOrDefault($"wh_{t}"));
                        var url = formValues.GetValueOrDefault($"wh_{t}_url") ?? "";
                        if (enabled || !string.IsNullOrEmpty(url))
                        {
                            wh[t] = new Dictionary<string, object>
                            {
                                ["enabled"] = enabled,
                                ["url"] = url
                            };
                        }
                    }

                    project = project with
                    {
                        Webhooks = wh,
                        DiscordWebhook = defaultUrl
                    };
                    break;
                }
            case "analytics":
                {
                    var analytics = new Dictionary<string, object>
                    {
                        ["enabled"] = IsChecked(formValues.GetValueOrDefault("analyticsEnabled")),
                        ["captureSessions"] = IsChecked(formValues.GetValueOrDefault("captureSessions")),
                        ["captureEndpoints"] = IsChecked(formValues.GetValueOrDefault("captureEndpoints")),
                        ["captureStorage"] = IsChecked(formValues.GetValueOrDefault("captureStorage"))
                    };
                    project = project with { Analytics = analytics };
                    break;
                }
            case "revisions":
                {
                    var enforcementMode = formValues.GetValueOrDefault("revisionEnforcementMode");
                    if (enforcementMode != "force_upgrade" && enforcementMode != "allow_continue")
                    {
                        enforcementMode = NormalizeRevisionMode(project);
                    }

                    var graceRaw = formValues.GetValueOrDefault("revisionGracePeriodMinutes");
                    var escalateRaw = formValues.GetValueOrDefault("revisionEscalateAfterMinutes");
                    var postGraceAction = formValues.GetValueOrDefault("revisionPostGraceAction");
                    if (string.Equals(postGraceAction, "allow_readonly", StringComparison.OrdinalIgnoreCase))
                    {
                        enforcementMode = "allow_continue";
                        postGraceAction = "block_writes";
                    }
                    if (postGraceAction != "block_writes" && postGraceAction != "block_all")
                    {
                        postGraceAction = string.IsNullOrWhiteSpace(project.RevisionPostGraceAction) ? "block_writes" : project.RevisionPostGraceAction;
                    }

                    project = project with
                    {
                        RevisionEnforcementEnabled = IsChecked(formValues.GetValueOrDefault("revisionEnforcementEnabled")),
                        RevisionShowDefaultMessage = IsChecked(formValues.GetValueOrDefault("revisionShowDefaultMessage")),
                        RevisionEnforcementMode = enforcementMode,
                        RevisionGracePeriodMinutes = ParseOptionalInt(graceRaw, min: 5, max: 1440),
                        RevisionPostGraceAction = postGraceAction,
                        RevisionForceEndpointUpgrade = IsChecked(formValues.GetValueOrDefault("revisionForceEndpointUpgrade")),
                        RevisionNotifyMessage = (formValues.GetValueOrDefault("revisionNotifyMessage") ?? string.Empty).Trim()[..Math.Min((formValues.GetValueOrDefault("revisionNotifyMessage") ?? string.Empty).Trim().Length, 500)],
                        RevisionShowNewVersionBanner = !formValues.ContainsKey("revisionShowNewVersionBanner") || IsChecked(formValues.GetValueOrDefault("revisionShowNewVersionBanner")),
                        RevisionShowUpdateOptions = !formValues.ContainsKey("revisionShowUpdateOptions") || IsChecked(formValues.GetValueOrDefault("revisionShowUpdateOptions")),
                        RevisionEscalateAfterMinutes = ParseOptionalInt(escalateRaw, min: 60, max: 1440),
                        RevisionShowPopupOnce = !formValues.ContainsKey("revisionShowPopupOnce") || IsChecked(formValues.GetValueOrDefault("revisionShowPopupOnce")),
                        RevisionTestOutdatedEditor = IsChecked(formValues.GetValueOrDefault("revisionTestOutdatedEditor")),
                        RevisionTestOutdatedLive = IsChecked(formValues.GetValueOrDefault("revisionTestOutdatedLive"))
                    };
                    break;
                }
            case "collection-create":
                await CollectionDashboardMutations.CreateCollectionAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues,
                    cancellationToken);
                break;
            case "collection-edit":
                await CollectionDashboardMutations.UpdateCollectionAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues,
                    cancellationToken);
                break;
            case "collection-delete":
                await CollectionDashboardMutations.DeleteCollectionAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues.GetValueOrDefault("collectionId") ?? string.Empty,
                    cancellationToken);
                break;
            case "collection-values":
                await CollectionDashboardMutations.UpdateCollectionValuesAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues,
                    cancellationToken);
                break;
            case "collection-reset":
                await CollectionDashboardMutations.ResetCollectionDataAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues.GetValueOrDefault("collectionId") ?? string.Empty,
                    cancellationToken);
                break;
            case "endpoint-create":
                await EndpointDashboardMutations.CreateEndpointAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues,
                    cancellationToken);
                break;
            case "endpoint-update":
                await EndpointDashboardMutations.UpdateEndpointAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues,
                    cancellationToken);
                break;
            case "endpoint-delete":
                await EndpointDashboardMutations.DeleteEndpointAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    formValues.GetValueOrDefault("endpointId") ?? string.Empty,
                    cancellationToken);
                break;
            case "endpoint-delete-all":
                await EndpointDashboardMutations.DeleteAllEndpointsAsync(
                    bunnyWorkspaceClient,
                    storageOwnerUserId,
                    projectId,
                    cancellationToken);
                break;
        }

        project = project with { UpdatedAt = DateTimeOffset.UtcNow };
        projects[idx] = project;
        await bunnyWorkspaceClient.SaveUserProjectsAsync(storageOwnerUserId, projects, cancellationToken);
    }

    // ── Usage ──

    public async Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var currentPeriodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var currentPeriodEnd = currentPeriodStart.AddMonths(1);
        var previousPeriodStart = currentPeriodStart.AddMonths(-1);
        var previousPeriodEnd = currentPeriodStart;

        // Monthly totals route through the (ScyllaDB-authoritative, Bunny-
        // fallback) workspace client — see ScyllaMetadataWorkspaceClient.
        var currentUsage = await bunnyWorkspaceClient.GetProjectUsageAsync(
            storageOwnerUserId, projectId, currentPeriodStart.ToString("yyyy-MM"), cancellationToken);
        var previousUsage = await bunnyWorkspaceClient.GetProjectUsageAsync(
            storageOwnerUserId, projectId, previousPeriodStart.ToString("yyyy-MM"), cancellationToken);

        // Daily series, top endpoints, and avg response time come from the
        // ScyllaDB usage counters directly (no Bunny equivalent survives —
        // legacy perDay/perEndpoint lived inside the usage JSON, which is no
        // longer written). Best-effort: a ScyllaDB failure leaves them null and
        // the page renders totals only.
        IReadOnlyList<DailyUsagePoint>? dailyRequests = null;
        UsageResponseTime? responseTime = null;
        IReadOnlyList<EndpointUsageSummary>? topEndpoints = null;
        try
        {
            var monthKey = currentPeriodStart.ToString("yyyy-MM");
            static long ReadLong(JsonElement el, string name)
                => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
            static string ReadStr(JsonElement el, string name)
                => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

            var monthly = await scyllaStore.ReadProjectUsageMonthlyAsync(projectId, monthKey, cancellationToken);
            if (monthly is { } m)
            {
                var samples = ReadLong(m, "duration_samples");
                if (samples > 0)
                {
                    // Percentiles need a per-request sample store; only the
                    // additive avg is derivable from counters (design D4/D5).
                    responseTime = new UsageResponseTime(
                        Avg: ReadLong(m, "duration_ms_sum") / (double)samples,
                        P95: 0, P99: 0, Samples: samples);
                }
            }

            var daily = await scyllaStore.ReadProjectUsageDailyAsync(projectId, monthKey, cancellationToken);
            if (daily.Count > 0)
            {
                dailyRequests = daily
                    .Select(d => new DailyUsagePoint(
                        ReadStr(d, "day"),
                        ReadLong(d, "requests"),
                        ReadLong(d, "errors"),
                        ReadLong(d, "bytes_in"),
                        ReadLong(d, "bytes_out")))
                    .OrderBy(d => d.Day, StringComparer.Ordinal)
                    .ToList();
            }

            var endpointRows = await scyllaStore.ReadProjectUsageEndpointsAsync(projectId, monthKey, 100, cancellationToken);
            if (endpointRows.Count > 0)
            {
                topEndpoints = endpointRows
                    .Select(e =>
                    {
                        var calls = ReadLong(e, "calls");
                        var errors = ReadLong(e, "errors");
                        var computeUnits = ReadLong(e, "compute_units");
                        return new EndpointUsageSummary(
                            EndpointLabel: ReadStr(e, "endpoint_slug"),
                            Requests: calls,
                            Errors: errors,
                            ErrorRate: calls > 0 ? errors / (double)calls : 0,
                            TotalComputeUnits: computeUnits,
                            AvgComputeUnits: calls > 0 ? computeUnits / (double)calls : 0,
                            AvgCpuUtilPct: 0);
                    })
                    .OrderByDescending(e => e.Requests)
                    .Take(10)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ScyllaDB usage detail read failed for {ProjectId} — totals-only usage page", projectId);
        }

        return new ProjectUsageData(
            CurrentRequests: currentUsage?.Requests ?? 0,
            CurrentBytesIn: currentUsage?.BytesIn ?? 0,
            CurrentBytesOut: currentUsage?.BytesOut ?? 0,
            CurrentComputeUnits: currentUsage?.ComputeUnits ?? 0,
            CurrentAvgComputeUnits: currentUsage is { Requests: > 0 }
                ? currentUsage.ComputeUnits / currentUsage.Requests
                : 0,
            CurrentAvgCpuUtilPct: 0,
            CurrentErrors: currentUsage?.Errors ?? 0,
            CurrentBillableBandwidth: currentUsage?.BytesOut ?? 0,
            PreviousRequests: previousUsage?.Requests ?? 0,
            PreviousBytesIn: previousUsage?.BytesIn ?? 0,
            PreviousBytesOut: previousUsage?.BytesOut ?? 0,
            PreviousComputeUnits: previousUsage?.ComputeUnits ?? 0,
            PreviousErrors: previousUsage?.Errors ?? 0,
            PreviousBillableBandwidth: previousUsage?.BytesOut ?? 0,
            PreviousStorageBytes: previousUsage?.StorageBytes ?? 0,
            StorageFootprintBytes: currentUsage?.StorageBytes ?? 0,
            StorageDataBytes: currentUsage?.StorageBytes ?? 0,
            StorageLogBytes: 0,
            CurrentPeriodStart: currentPeriodStart.ToUnixTimeSeconds(),
            CurrentPeriodEnd: currentPeriodEnd.ToUnixTimeSeconds(),
            PreviousPeriodStart: previousPeriodStart.ToUnixTimeSeconds(),
            PreviousPeriodEnd: previousPeriodEnd.ToUnixTimeSeconds(),
            DailyRequests: dailyRequests,
            ResponseTime: responseTime,
            TopEndpoints: topEndpoints)
        {
            UsageSource = currentUsage?.Source ?? "unavailable",
            UsageLastUpdatedAt = currentUsage?.LastUpdatedAt,
        };
    }

    // ── Rate Limits ──

    public async Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        // Endpoint call limits live on the project's `endpointRateLimits` field in projects.json
        // (matching the legacy data plane), not a separate rate-limits.json resource. The caller
        // passes the loaded project field into the view; here we return only the field rules,
        // mapped from the authoritative rate-limit-rules.json (windows schema).
        var rules = await ReadRateLimitRulesAsync(storageOwnerUserId, projectId, cancellationToken);
        return new ProjectRateLimits(EndpointRateLimits: null, Rules: rules);
    }

    private static bool HasEnabledEndpointRateLimits(Dictionary<string, object>? endpointRateLimits)
    {
        if (endpointRateLimits is null || !endpointRateLimits.TryGetValue("enabled", out var enabled))
        {
            return false;
        }

        return enabled switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } element => IsTruthy(element.GetString()),
            string value => IsTruthy(value),
            _ => false
        };
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<RateLimitRule>> ReadRateLimitRulesAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        var raw = await bunnyWorkspaceClient.GetProjectResourceAsync<List<JsonElement>>(
            storageOwnerUserId, projectId, "rate-limit-rules.json", cancellationToken);
        if (raw is null || raw.Count == 0) return Array.Empty<RateLimitRule>();

        var rules = new List<RateLimitRule>(raw.Count);
        foreach (var element in raw)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            rules.Add(MapRateLimitRule(element));
        }
        return rules;
    }

    private static RateLimitRule MapRateLimitRule(JsonElement element)
    {
        string ReadString(string key) =>
            element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        var hasWindows = element.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object;
        var windowsElement = windows;

        int? ReadWindow(string windowKey, string flatKey)
        {
            if (hasWindows && windowsElement.TryGetProperty(windowKey, out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var wv))
                return wv;
            if (element.TryGetProperty(flatKey, out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var fv))
                return fv;
            return null;
        }

        var enabled = !element.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
        var action = ReadString("action");
        var scope = ReadString("scope");

        return new RateLimitRule(
            Id: ReadString("id"),
            Collection: ReadString("collection"),
            Field: ReadString("field"),
            Action: string.IsNullOrEmpty(action) ? "reject" : action,
            MaxPerMinute: ReadWindow("perMinute", "maxPerMinute"),
            MaxPerHour: ReadWindow("perHour", "maxPerHour"),
            MaxPerDay: ReadWindow("perDay", "maxPerDay"),
            Enabled: enabled,
            Scope: string.IsNullOrEmpty(scope) ? "per_player" : scope
        );
    }

    public async Task SaveEndpointRateLimitsAsync(
        long storageOwnerUserId,
        string projectId,
        Dictionary<string, object> endpointRateLimits,
        CancellationToken cancellationToken)
    {
        // Endpoint call limits persist on the project's endpointRateLimits field in projects.json
        // (matching the legacy data plane): load the full list, update the one project, save it all.
        // NOTE: never call SaveUserProjectsAsync with an empty list as a "trigger" - it overwrites
        // projects.json and wipes the user's projects.
        var projectList = (await bunnyWorkspaceClient.GetUserProjectsAsync(storageOwnerUserId, cancellationToken)).ToList();
        var idx = projectList.FindIndex(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            throw new InvalidOperationException(
                $"Project '{projectId}' not found for user {storageOwnerUserId} while saving endpoint rate limits.");
        }

        projectList[idx] = projectList[idx] with { EndpointRateLimits = endpointRateLimits };
        await bunnyWorkspaceClient.SaveUserProjectsAsync(storageOwnerUserId, projectList, cancellationToken);
    }


    public async Task SaveRateLimitRulesJsonAsync(
        long storageOwnerUserId,
        string projectId,
        JsonElement rules,
        CancellationToken cancellationToken)
    {
        object payload = rules.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<object>(rules.GetRawText()) ?? Array.Empty<object>(),
            JsonValueKind.Object when rules.TryGetProperty("rules", out var nested) =>
                JsonSerializer.Deserialize<object>(nested.GetRawText()) ?? Array.Empty<object>(),
            _ => Array.Empty<object>(),
        };

        await bunnyWorkspaceClient.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "rate-limit-rules.json", payload, cancellationToken);
    }
    public async Task SaveRateLimitRulesAsync(
        long storageOwnerUserId,
        string projectId,
        IReadOnlyList<RateLimitRule> rules,
        CancellationToken cancellationToken)
    {
        // Persist to the authoritative rate-limit-rules.json in the data plane's windows schema
        // (tools/sbox/rate-limits-v2.js), so saved rules reload here and enforce at runtime.
        var payload = rules.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["collection"] = r.Collection,
            ["field"] = r.Field,
            ["scope"] = string.IsNullOrEmpty(r.Scope) ? "per_player" : r.Scope,
            ["windows"] = new Dictionary<string, object?>
            {
                ["perMinute"] = r.MaxPerMinute,
                ["perHour"] = r.MaxPerHour,
                ["perDay"] = r.MaxPerDay,
            },
            ["action"] = string.IsNullOrEmpty(r.Action) ? "reject" : r.Action,
            ["enabled"] = r.Enabled,
        }).ToList();

        await bunnyWorkspaceClient.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "rate-limit-rules.json", payload, cancellationToken);
    }

    // ── Private helpers ──

    /// <summary>
    /// Reads a resource list the same tolerant way the workspace project card counts it
    /// (see ProjectActivitySupport.ParseResourceArray): raw text, then per-element
    /// binding. A strict whole-list bind here meant one resource with a null flag — the
    /// YAML source compiler emits any key the author wrote, blank ones included — made
    /// the dashboard render zero collections/endpoints while the card still counted
    /// them from the very same file.
    /// </summary>
    private async Task<List<T>> ReadResourceListAsync<T>(
        long userId, string projectId, string resourceName, CancellationToken cancellationToken)
    {
        var json = await bunnyWorkspaceClient.GetProjectResourceTextAsync(userId, projectId, resourceName, cancellationToken);
        return ResilientResourceJson.DeserializeList<T>(json, (index, ex) =>
        {
            if (index < 0)
                logger.LogWarning(ex, "{Resource} for project {ProjectId} is not valid JSON; treating as empty", resourceName, projectId);
            else
                logger.LogWarning(ex, "Skipping malformed entry #{Index} in {Resource} for project {ProjectId}", index, resourceName, projectId);
        });
    }

    private async Task<NetworkStorageProjectResources?> LoadProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken)
    {
        var collectionsTask = ReadResourceListAsync<CollectionResource>(userId, projectId, "collections.json", cancellationToken);
        var endpointsTask = ReadResourceListAsync<EndpointResource>(userId, projectId, "endpoints.json", cancellationToken);

        await Task.WhenAll(collectionsTask, endpointsTask);

        return new NetworkStorageProjectResources(
            await collectionsTask,
            await endpointsTask
        );
    }

    private async Task<(int CollectionCount, int ApiKeyCount, int QueryCount, int WorkflowCount, int EndpointCount)> LoadResourceCountsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        var collectionsTask = ReadResourceListAsync<CollectionResource>(storageOwnerUserId, projectId, "collections.json", cancellationToken);
        var endpointsTask = ReadResourceListAsync<EndpointResource>(storageOwnerUserId, projectId, "endpoints.json", cancellationToken);
        var keysTask = GetProjectKeysAsync(storageOwnerUserId, projectId, cancellationToken);
        var queriesTask = bunnyWorkspaceClient.GetProjectResourceAsync<List<object>>(storageOwnerUserId, projectId, "queries.json", cancellationToken);
        var workflowsTask = bunnyWorkspaceClient.GetProjectResourceAsync<List<object>>(storageOwnerUserId, projectId, "workflows.json", cancellationToken);

        await Task.WhenAll(collectionsTask, endpointsTask, keysTask, queriesTask, workflowsTask);

        return (
            (await collectionsTask).Count,
            (await keysTask).Count,
            (await queriesTask)?.Count ?? 0,
            (await workflowsTask)?.Count ?? 0,
            (await endpointsTask).Count
        );
    }

    private static string GenerateProjectId()
    {
        var bytes = new byte[12];
        RandomNumberGenerator.Fill(bytes);
        return "proj_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateCryptoString(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateId(string prefix)
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ParseIntOrDefault(string? value, int defaultValue, int min, int max)
    {
        if (int.TryParse(value, out var parsed))
            return Math.Clamp(parsed, min, max);
        return defaultValue;
    }

    private static int? ParseOptionalInt(string? value, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, out var parsed) && parsed > 0
            ? Math.Clamp(parsed, min, max)
            : null;
    }

    private static string NormalizeRevisionMode(BunnyProject project)
    {
        if (string.IsNullOrWhiteSpace(project.RevisionEnforcementMode)
            && string.Equals(project.RevisionPostGraceAction, "allow_readonly", StringComparison.OrdinalIgnoreCase))
        {
            return "allow_continue";
        }

        return string.Equals(project.RevisionEnforcementMode, "force_upgrade", StringComparison.OrdinalIgnoreCase)
            ? "force_upgrade"
            : "allow_continue";
    }

    private static bool IsChecked(string? value)
        => string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> DefaultPermissions()
    {
        return new Dictionary<string, string>
        {
            ["endpoints"] = "rwx",
            ["queries"] = "rwx",
            ["collections"] = "rwx",
            ["workflows"] = "rw",
            ["game_values"] = "rw",
            ["rate_limits"] = "rw",
            ["settings"] = "rw"
        };
    }

    private async Task InsertApiKeyAsync(long userId, string projectId, string apiKey, string label, string keyType,
        string? keyHash, string? keyIdentifier, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
    {
        await scyllaStore.UpsertApiKeyAsync(
            projectId,
            apiKey,
            userId.ToString(CultureInfo.InvariantCulture),
            keyType,
            keyHash ?? string.Empty,
            keyIdentifier ?? string.Empty,
            label,
            enabled: true,
            JsonSerializer.SerializeToElement(permissions ?? new Dictionary<string, string>()),
            version: 1,
            cancellationToken);
    }

    private async Task<int> CountProjectApiKeysAsync(long userId, string projectId, CancellationToken cancellationToken)
    {
        var rows = await scyllaStore.ListApiKeysAsync(projectId, cancellationToken);
        return rows.Count(r => r.ValueKind == JsonValueKind.Object
            && string.Equals(ReadJsonString(r, "user_id"), userId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    private async Task<bool> GetKeyEnabledStateAsync(long userId, string projectId, string apiKey, CancellationToken cancellationToken)
    {
        var row = await FindScyllaApiKeyRowAsync(userId, projectId, apiKey, cancellationToken);
        return row is { ValueKind: JsonValueKind.Object } && ReadJsonBool(row.Value, "enabled");
    }

    private async Task<string?> GetSecretKeyIdentifierAsync(long userId, string projectId, string maskedKey, CancellationToken cancellationToken)
    {
        var row = await FindScyllaApiKeyRowAsync(userId, projectId, maskedKey, cancellationToken);
        return row is { ValueKind: JsonValueKind.Object } ? ReadJsonNullableString(row.Value, "key_identifier") : null;
    }
    // ── CDN key-index helpers ──

    private async Task AppendKeyToIndexAsync(string projectId, CdnKeyIndexEntry entry, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken) ?? new CdnKeyIndex(Array.Empty<CdnKeyIndexEntry>());
        var keys = index.Keys.ToList();
        keys.RemoveAll(k => string.Equals(k.KeyIdentifier, entry.KeyIdentifier, StringComparison.Ordinal));
        keys.Add(entry);
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task UpdateKeyInIndexByKeyAsync(string projectId, string key, bool enabled, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken);
        if (index is null) return;

        var keys = index.Keys.ToList();
        var entry = keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));
        if (entry is null) return;

        var idx = keys.IndexOf(entry);
        keys[idx] = entry with { Enabled = enabled };
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task UpdateKeyInIndexByIdentifierAsync(string projectId, string keyIdentifier, bool enabled, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken);
        if (index is null) return;

        var keys = index.Keys.ToList();
        var entry = keys.FirstOrDefault(k => string.Equals(k.KeyIdentifier, keyIdentifier, StringComparison.Ordinal));
        if (entry is null) return;

        var idx = keys.IndexOf(entry);
        keys[idx] = entry with { Enabled = enabled };
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task RemoveKeyFromIndexByKeyAsync(string projectId, string key, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken);
        if (index is null) return;

        var keys = index.Keys.Where(k => !string.Equals(k.Key, key, StringComparison.Ordinal)).ToList();
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task RemoveKeyFromIndexByIdentifierAsync(string projectId, string keyIdentifier, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken);
        if (index is null) return;

        var keys = index.Keys.Where(k => !string.Equals(k.KeyIdentifier, keyIdentifier, StringComparison.Ordinal)).ToList();
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task UpdateKeyPermissionsInIndexAsync(string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        var index = await keyCdnWriter.ReadKeyIndexAsync(projectId, cancellationToken);
        if (index is null) return;

        var keys = index.Keys.ToList();
        var entry = keys.FirstOrDefault(k => string.Equals(k.KeyIdentifier, keyIdentifier, StringComparison.Ordinal));
        if (entry is null) return;

        var idx = keys.IndexOf(entry);
        keys[idx] = entry with { Key = entry.Key }; // preserve key; permissions live on the CDN file
        await keyCdnWriter.WriteKeyIndexAsync(projectId, new CdnKeyIndex(keys), cancellationToken);
    }

    private async Task DeleteApiKeyAsync(long userId, string projectId, string apiKey, CancellationToken cancellationToken)
    {
        await scyllaStore.DeleteApiKeyAsync(projectId, apiKey, cancellationToken);
    }
    public async Task<ProjectAuditLogResult> BrowseProjectLogsAsync(
        long storageOwnerUserId,
        string projectId,
        string? search,
        string? action,
        string? date,
        string sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Sanitize pagination
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 25;
        if (pageSize > 500) pageSize = 500;

        // ScyllaDB is the live store for audit logs. The Bunny CDN holds only
        // legacy entries written before the ScyllaDB cutover.
        var scyllaEntries = await TryReadFromScyllaAsync(
            projectId, search, action, date, cancellationToken);

        if (scyllaEntries.Count > 0)
        {
            return BuildPaginatedResult(scyllaEntries, sort, page, pageSize);
        }

        // Fall back to the legacy Bunny CDN log files.
        return await ReadFromCdnAsync(
            storageOwnerUserId, projectId, search, action, date, sort, page, pageSize, cancellationToken);
    }

    private async Task<IReadOnlyList<ProjectAuditLogEntry>> TryReadFromScyllaAsync(
        string projectId, string? search, string? action, string? date,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await scyllaStore.ListAuditLogsAsync(projectId, ScyllaAuditLogReadLimit, cancellationToken);
            if (rows.Count == 0) return Array.Empty<ProjectAuditLogEntry>();

            var entries = rows.Select(MapScyllaAuditLogEntry);

            if (!string.IsNullOrWhiteSpace(search))
            {
                entries = entries.Where(e =>
                    e.ResourceName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || e.ResourceType.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || e.ActorName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (e.CollectionName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.EndpointName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.WorkflowName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.DiffText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.ResourceSlug?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                entries = entries.Where(e => e.Action.Contains(action, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(date))
            {
                entries = entries.Where(e => e.Timestamp.StartsWith(date, StringComparison.OrdinalIgnoreCase));
            }

            return entries.ToList();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to read audit logs from ScyllaDB for project {ProjectId}", projectId);
            return Array.Empty<ProjectAuditLogEntry>();
        }
    }

    private ProjectAuditLogEntry MapScyllaAuditLogEntry(JsonElement row)
    {
        var action = ReadString(row, "action");

        var actorName = "";
        var ip = "";
        var actorJson = ReadString(row, "actor_json");
        if (!string.IsNullOrEmpty(actorJson))
        {
            try
            {
                using var actorDoc = JsonDocument.Parse(actorJson);
                actorName = ReadString(actorDoc.RootElement, "name");
                ip = ReadString(actorDoc.RootElement, "ip");
            }
            catch (JsonException exception)
            {
                logger.LogDebug(exception, "Ignoring malformed audit actor metadata");
            }
        }

        var resourceType = "";
        var resourceName = "";
        var resourceSlug = "";
        string? collectionName = null;
        string? endpointName = null;
        string? workflowName = null;
        var targetJson = ReadString(row, "target_json");
        if (!string.IsNullOrEmpty(targetJson))
        {
            try
            {
                using var targetDoc = JsonDocument.Parse(targetJson);
                resourceType = ReadString(targetDoc.RootElement, "resourceType");
                resourceName = ReadString(targetDoc.RootElement, "resourceName");
                resourceSlug = ReadString(targetDoc.RootElement, "resourceSlug");
                collectionName = ReadNullableString(targetDoc.RootElement, "collectionName");
                endpointName = ReadNullableString(targetDoc.RootElement, "endpointName");
                workflowName = ReadNullableString(targetDoc.RootElement, "workflowName");
            }
            catch (JsonException exception)
            {
                logger.LogDebug(exception, "Ignoring malformed audit target metadata");
            }
        }

        var addedLines = 0;
        var removedLines = 0;
        var summaryJson = ReadString(row, "summary_json");
        if (!string.IsNullOrEmpty(summaryJson))
        {
            try
            {
                using var summaryDoc = JsonDocument.Parse(summaryJson);
                addedLines = ReadInt(summaryDoc.RootElement, "addedLines");
                removedLines = ReadInt(summaryDoc.RootElement, "removedLines");
            }
            catch (JsonException exception)
            {
                logger.LogDebug(exception, "Ignoring malformed audit summary metadata");
            }
        }

        var diffJson = ReadString(row, "diff_json");
        var diffText = string.IsNullOrEmpty(diffJson) ? null : diffJson;
        string? beforeText = null;
        string? afterText = null;
        if (!string.IsNullOrEmpty(diffJson) && diffJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var diffDoc = JsonDocument.Parse(diffJson);
                beforeText = ReadNullableString(diffDoc.RootElement, "before");
                afterText = ReadNullableString(diffDoc.RootElement, "after");
            }
            catch (JsonException exception)
            {
                logger.LogDebug(exception, "Ignoring malformed audit diff metadata");
            }
        }

        var timestamp = "";
        if (row.TryGetProperty("created_at_unix_ms", out var tsEl)
            && tsEl.ValueKind == JsonValueKind.Number
            && tsEl.TryGetInt64(out var ms) && ms > 0)
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        return new ProjectAuditLogEntry(
            Action: action,
            ResourceType: resourceType,
            ResourceName: resourceName,
            ResourceSlug: resourceSlug,
            Timestamp: timestamp,
            ActorName: actorName,
            Ip: ip,
            CollectionName: collectionName,
            EndpointName: endpointName,
            WorkflowName: workflowName,
            DiffText: diffText,
            BeforeText: beforeText,
            AfterText: afterText,
            AddedLines: addedLines,
            RemovedLines: removedLines);
    }

    private async Task<ProjectAuditLogResult> ReadFromCdnAsync(
        long storageOwnerUserId, string projectId, string? search, string? action, string? date,
        string sort, int page, int pageSize, CancellationToken cancellationToken)
    {
        // List available log date files from CDN
        IReadOnlyList<BunnyStorageEntry> logFiles;
        try
        {
            logFiles = await bunnyStorageEnumerator.ListProjectResourceAsync(
                storageOwnerUserId, projectId, "logs/project", cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to list audit log files for project {ProjectId}", projectId);
            return EmptyResult(pageSize);
        }

        if (logFiles.Count == 0)
        {
            return EmptyResult(pageSize);
        }

        // Filter to .json files, extract date from filename
        var dateFileNames = logFiles
            .Where(f => !f.IsDirectory && f.ObjectName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.ObjectName)
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();

        // Filter by date if provided (format: yyyy-MM-dd)
        if (!string.IsNullOrWhiteSpace(date))
        {
            dateFileNames = dateFileNames
                .Where(fn => fn.StartsWith(date, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Read and parse all matching log files
        var allEntries = new List<ProjectAuditLogEntry>();
        foreach (var fileName in dateFileNames)
        {
            var resourcePath = $"logs/project/{fileName}";
            string? json;
            try
            {
                json = await bunnyWorkspaceClient.GetProjectResourceTextAsync(
                    storageOwnerUserId, projectId, resourcePath, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to read audit log file {FileName} for project {ProjectId}", fileName, projectId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(json)) continue;

            var parsed = ParseLogFile(json);
            allEntries.AddRange(parsed);
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            allEntries = allEntries.Where(e =>
                e.ResourceName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.ResourceType.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.ActorName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (e.CollectionName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.EndpointName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.WorkflowName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.DiffText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.ResourceSlug?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        // Apply action filter
        if (!string.IsNullOrWhiteSpace(action))
        {
            allEntries = allEntries.Where(e =>
                e.Action.Contains(action, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        return BuildPaginatedResult(allEntries, sort, page, pageSize);
    }

    private static ProjectAuditLogResult BuildPaginatedResult(
        IReadOnlyList<ProjectAuditLogEntry> allEntries, string sort, int page, int pageSize)
    {
        // Sort
        var oldestFirst = string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase);
        var sorted = oldestFirst
            ? allEntries.OrderBy(e => e.Timestamp, StringComparer.Ordinal).ToList()
            : allEntries.OrderByDescending(e => e.Timestamp, StringComparer.Ordinal).ToList();

        // Paginate
        var total = sorted.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)total / pageSize));
        if (page > totalPages) page = totalPages;

        var skip = (page - 1) * pageSize;
        var pageEntries = sorted.Skip(skip).Take(pageSize).ToList();

        return new ProjectAuditLogResult(
            Logs: pageEntries,
            Total: total,
            TotalPages: totalPages,
            Page: page,
            PageSize: pageSize,
            HasPrev: page > 1,
            HasNext: page < totalPages);
    }

    private static ProjectAuditLogResult EmptyResult(int pageSize) => new(
        Logs: Array.Empty<ProjectAuditLogEntry>(),
        Total: 0, TotalPages: 1, Page: 1, PageSize: pageSize,
        HasPrev: false, HasNext: false);

    private static IReadOnlyList<ProjectAuditLogEntry> ParseLogFile(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ProjectAuditLogEntry>();
            }

            var entries = new List<ProjectAuditLogEntry>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                entries.Add(new ProjectAuditLogEntry(
                    Action: ReadString(element, "_action"),
                    ResourceType: ReadString(element, "_resourceType"),
                    ResourceName: ReadString(element, "_resourceName"),
                    ResourceSlug: ReadString(element, "_resourceSlug"),
                    Timestamp: ReadString(element, "_ts"),
                    ActorName: ReadString(element, "_actorName"),
                    Ip: ReadString(element, "_ip"),
                    CollectionName: ReadNullableString(element, "_collectionName"),
                    EndpointName: ReadNullableString(element, "_endpointName"),
                    WorkflowName: ReadNullableString(element, "_workflowName"),
                    DiffText: ReadNullableString(element, "_diff"),
                    BeforeText: ReadNullableString(element, "_before"),
                    AfterText: ReadNullableString(element, "_after"),
                    AddedLines: ReadInt(element, "_addedLines"),
                    RemovedLines: ReadInt(element, "_removedLines")));
            }

            return entries;
        }
        catch (JsonException)
        {
            return Array.Empty<ProjectAuditLogEntry>();
        }
    }

    private static string ReadString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string? ReadNullableString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int ReadInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

}
