using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SboxNetworkStorage.Domain.Workspace;

public sealed record WorkspaceInfo(
    string Id,
    string Slug,
    string DisplayName,
    string? Description,
    string? AvatarUrl,
    string Type,
    long? OwnerUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Role = null,
    string? MembershipStatus = null
);

public sealed record WorkspaceMember(
    long Id,
    string OrganizationId,
    long? UserId,
    string Email,
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string Role,
    string Status,
    long? InvitedBy,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset CreatedAt
);

public sealed record WorkspaceProjectUsage(
    long Requests,
    long BytesIn,
    long BytesOut,
    long Errors,
    long StorageBytes,
    long ComputeUnits
)
{
    public string Source { get; init; } = "unknown";
    public DateTimeOffset? LastUpdatedAt { get; init; }
}

public sealed record WorkspaceProject(
    string Id,
    string Name,
    string? Description,
    bool Enabled,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CompiledAt,
    bool? RequireSboxAuth = null,
    bool? EnableAuthSessions = null,
    int? AuthSessionTtlSeconds = null,
    bool? EnableEncryptedRequests = null,
    int? EncryptedRequestWindowSeconds = null,
    string? PlayerKeyMode = null,
    string? HostingProfile = null,
    bool? RevisionEnforcementEnabled = null,
    string? RevisionEnforcementMode = null,
    int? RevisionGracePeriodMinutes = null,
    string? RevisionPostGraceAction = null,
    bool? RevisionForceEndpointUpgrade = null,
    string? RevisionNotifyMessage = null,
    bool? RevisionShowDefaultMessage = null,
    bool? RevisionShowNewVersionBanner = null,
    bool? RevisionShowUpdateOptions = null,
    int? RevisionEscalateAfterMinutes = null,
    bool? RevisionShowPopupOnce = null,
    bool? RevisionTestOutdatedEditor = null,
    bool? RevisionTestOutdatedLive = null,
    // Phase 4 (game-version-policy): additive version-rules fields. All null by
    // default so untouched projects keep legacy behavior (no _revisionStatus,
    // no enforcement, live definitions). Missing reads as unset (D9 migration
    // is applied at evaluation time, never by rewriting stored projects).
    // Minimum supported revision: clients below it are treated as past grace.
    long? RevisionMinimumRevisionId = null,
    // In-grace player action: "continue", "notify" or "rejoin". Null derives
    // from the legacy RevisionEnforcementMode per D9 (allow_continue→notify,
    // force_upgrade→rejoin).
    string? RevisionInGraceAction = null,
    // Server-side data-plane enforcement switch. Off (null/false) by default;
    // the legacy RevisionEnforcementEnabled only gates client-library action
    // delivery, never server blocking.
    bool? RevisionServerEnforcementEnabled = null,
    // Retained snapshots besides live: default 3, range 0-20.
    int? RevisionRetentionCount = null,
    Dictionary<string, object>? Webhooks = null,
    string? DiscordWebhook = null,
    Dictionary<string, object>? Analytics = null,
    Dictionary<string, object>? EndpointRateLimits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LegacyPlayerProjections = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LibraryMinimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LibraryAlertPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameLogSettings? GameLogs = null,
    // Media store policy (per project, off by default). Shape: { enabled, purposes: { <name>: {...} } }; parsed by MediaPolicyParser.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, object>? Media = null,
    /// <summary>Opt-in (off when absent): endpoint webhook steps enqueue to the durable outbox instead of sending inline.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? WebhookOutbox = null,
    /// <summary>ns.fetch host allow-list: exact hosts or *.example.com (docs/endpoints-javascript.md). Absent = none.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? FetchAllowedHosts = null,
    /// <summary>Opt-in: ns.fetch may call any public host. Private-network addresses stay blocked unless endpoints.fetch_private_networks is set; metadata and reserved addresses are always blocked.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FetchAllowAllPublic = null,
    // Opt-in (off when absent): every endpoint call must carry an auth session (SECURITY_SESSION_REQUIRED).
    // Not part of the signed security config, matching the reference (services/network-storage-security-config.js).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RequireAuthSessions = null
);
public static class WorkspaceProjectProfiles
{
    /// <summary>Hosting profiles: how the game is hosted. Missing or unknown values read as "unset".</summary>
    public static string NormalizeHostingProfile(string? value) => value switch
    {
        "player-hosted" or "dedicated" or "hybrid" => value,
        _ => "unset",
    };

    public static string DisplayName(string? value) => NormalizeHostingProfile(value) switch
    {
        "player-hosted" => "Player-hosted",
        "dedicated" => "Dedicated",
        "hybrid" => "Hybrid",
        _ => "Not set",
    };
}

public sealed record WorkspaceProjectContext(
    WorkspaceProject Project,
    string OrganizationId,
    long StorageOwnerUserId,
    long OwnerUserId,
    long? CreatedByUserId,
    DateTimeOffset CreatedAt
);

public sealed record ApiKeyInfo(
    string Key,
    string Label,
    string KeyType,  // "public" or "secret"
    bool Enabled,
    string? KeyIdentifier,
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Permissions = null
);

public sealed record StorageApiKeyAuthResult(
    long UserId,
    string ProjectId,
    bool Enabled,
    string KeyType,  // "public" or "secret"
    Dictionary<string, string>? Permissions = null
);

public sealed record RateLimitRule(
    string Id,
    string Collection,
    string Field,
    string Action,  // "reject", "clamp", "flag"
    int? MaxPerMinute,
    int? MaxPerHour,
    int? MaxPerDay,
    bool Enabled,
    string Scope = "per_player"  // "per_player" or "global"
);

public sealed record ProjectRateLimits(
    Dictionary<string, object>? EndpointRateLimits,
    IReadOnlyList<RateLimitRule>? Rules
);

public sealed record ProjectUsageData(
    long CurrentRequests,
    long CurrentBytesIn,
    long CurrentBytesOut,
    long CurrentComputeUnits,
    long CurrentAvgComputeUnits,
    double CurrentAvgCpuUtilPct,
    long CurrentErrors,
    long CurrentBillableBandwidth,
    long PreviousRequests,
    long PreviousBytesIn,
    long PreviousBytesOut,
    long PreviousComputeUnits,
    long PreviousErrors,
    long PreviousBillableBandwidth,
    long PreviousStorageBytes,
    long StorageFootprintBytes,
    long StorageDataBytes,
    long StorageLogBytes,
    long CurrentPeriodStart,
    long CurrentPeriodEnd,
    long PreviousPeriodStart,
    long PreviousPeriodEnd,
    IReadOnlyList<DailyUsagePoint>? DailyRequests,
    UsageResponseTime? ResponseTime,
    IReadOnlyList<EndpointUsageSummary>? TopEndpoints
)
{
    public string UsageSource { get; init; } = "unknown";
    public DateTimeOffset? UsageLastUpdatedAt { get; init; }
}

public sealed record DailyUsagePoint(
    string Day,
    long Requests,
    long Errors,
    long BytesIn,
    long BytesOut
);

public sealed record UsageResponseTime(
    double Avg,
    double P95,
    double P99,
    long Samples
);

public sealed record EndpointUsageSummary(
    string EndpointLabel,
    long Requests,
    long Errors,
    double ErrorRate,
    long TotalComputeUnits,
    double AvgComputeUnits,
    double AvgCpuUtilPct
);
