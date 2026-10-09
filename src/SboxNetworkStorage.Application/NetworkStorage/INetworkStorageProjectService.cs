using System.Text.Json;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage;

public sealed record NetworkStorageProjectCreateResult(string ProjectId);

public sealed record NetworkStorageProjectAccessResult(
    WorkspaceProject Project,
    WorkspaceInfo? Organization,
    long StorageOwnerUserId,
    bool RequireSboxAuth,
    string? PlayerKeyMode,
    bool CanManage
);

public sealed record NetworkStorageProjectResources(
    IReadOnlyList<CollectionResource> Collections,
    IReadOnlyList<EndpointResource> Endpoints
);

public sealed record CollectionResource(
    string Id,
    string Name,
    string? Description,
    string CollectionType,
    Dictionary<string, object>? Schema,
    IReadOnlyList<object>? Constants,
    IReadOnlyList<object>? Tables,
    string? RevisionTarget,
    bool HasStaged,
    int MaxRecords = 1,
    bool AllowRecordDelete = false
);

public sealed record EndpointResource(
    string Id,
    string Name,
    string Slug,
    string Method,
    bool Enabled,
    string? Exposure,
    bool Internal,
    bool Deprecated,
    bool RequiresSecretKey,
    string? Notes,
    string? AuthoringMode,
    string? SourceFormat,
    IReadOnlyList<object>? Steps,
    string? SourceText = null,
    JsonElement? Input = null,
    JsonElement? Response = null,
    JsonElement? Let = null,
    JsonElement? Definition = null
);

public sealed record NetworkStorageTeamUser(
    long Id,
    string Username,
    string Email,
    string DisplayName,
    string? AvatarUrl
);

public sealed record NetworkStorageTeamMember(
    long Id,
    long? UserId,
    string Email,
    string Role,
    string Status,
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string? InviterUsername,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset CreatedAt
);

public sealed record NetworkStorageTeamData(
    NetworkStorageTeamUser Owner,
    IReadOnlyList<NetworkStorageTeamMember> Members,
    string CallerRole,
    string TeamMode
);


public interface INetworkStorageProjectService
{
    Task<NetworkStorageProjectCreateResult> CreateProjectAsync(
        long userId,
        string name,
        string? description,
        bool enabled,
        bool requireSboxAuth,
        string keyMode,
        string organizationId,
        CancellationToken cancellationToken);

    Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(
        long userId,
        string projectId,
        CancellationToken cancellationToken);

    Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(
        long userId,
        string projectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads project resources directly for a known storage owner, skipping the
    /// ownership resolution that <see cref="GetProjectResourcesAsync"/> performs.
    /// Use when the caller has already resolved project access (e.g. the
    /// controller's own <c>ResolveProjectAccessAsync</c>).
    /// </summary>
    Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken);

    Task<NetworkStorageTeamData?> GetProjectTeamAsync(
        long storageOwnerUserId,
        string projectId,
        string? organizationId,
        string callerRole,
        CancellationToken cancellationToken);

    // -- Keys --

    Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken);

    Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(
        long storageOwnerUserId,
        string projectId,
        string label,
        string keyType,
        Dictionary<string, string>? permissions,
        CancellationToken cancellationToken);

    Task ToggleProjectKeyAsync(
        long storageOwnerUserId,
        string projectId,
        string key,
        CancellationToken cancellationToken);

    Task RemoveProjectKeyAsync(
        long storageOwnerUserId,
        string projectId,
        string key,
        CancellationToken cancellationToken);

    Task UpdateProjectKeyPermissionsAsync(
        long storageOwnerUserId,
        string projectId,
        string keyIdentifier,
        Dictionary<string, string> permissions,
        CancellationToken cancellationToken);

    Task DeleteProjectAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken);

    // -- Settings --

    Task UpdateProjectSettingsAsync(
        long storageOwnerUserId,
        string projectId,
        string settingsTab,
        Dictionary<string, string> formValues,
        CancellationToken cancellationToken);

    // -- Usage --

    Task<ProjectUsageData> GetProjectUsageAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken);

    // -- Rate Limits --

    Task<ProjectRateLimits> GetProjectRateLimitsAsync(
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken);

    Task SaveEndpointRateLimitsAsync(
        long storageOwnerUserId,
        string projectId,
        Dictionary<string, object> endpointRateLimits,
        CancellationToken cancellationToken);

    Task SaveRateLimitRulesAsync(
        long storageOwnerUserId,
        string projectId,
        IReadOnlyList<RateLimitRule> rules,
        CancellationToken cancellationToken);

    Task SaveRateLimitRulesJsonAsync(
        long storageOwnerUserId,
        string projectId,
        JsonElement rules,
        CancellationToken cancellationToken)
        => SaveRateLimitRulesAsync(storageOwnerUserId, projectId, [], cancellationToken);
    // -- Audit Logs --

    Task<ProjectAuditLogResult> BrowseProjectLogsAsync(
        long storageOwnerUserId,
        string projectId,
        string? search,
        string? action,
        string? date,
        string sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

}