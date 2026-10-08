namespace SboxNetworkStorage.Contracts.NetworkStorage;

public sealed record ProjectAuditLogResult(
    IReadOnlyList<ProjectAuditLogEntry> Logs,
    int Total,
    int TotalPages,
    int Page,
    int PageSize,
    bool HasPrev,
    bool HasNext);

public sealed record ProjectAuditLogEntry(
    string Action,
    string ResourceType,
    string ResourceName,
    string ResourceSlug,
    string Timestamp,
    string ActorName,
    string Ip,
    string? CollectionName,
    string? EndpointName,
    string? WorkflowName,
    string? DiffText,
    string? BeforeText,
    string? AfterText,
    int AddedLines,
    int RemovedLines);
