namespace SboxNetworkStorage.Contracts.Diagnostics;

public sealed record HealthResponse(
    bool Ok,
    string Service,
    string Environment,
    string Version,
    DateTimeOffset Timestamp,
    string CorrelationId,
    int UptimeSeconds = 0);

public sealed record ReadyResponse(
    bool Ready,
    string Service,
    string Environment,
    IReadOnlyDictionary<string, bool> Checks,
    DateTimeOffset Timestamp,
    string CorrelationId);

public sealed record RouteOwnershipRecord(
    string Pattern,
    RouteOwner Owner,
    string Description,
    bool LiveProductionResponseOwner = true);

public sealed record StatsResponse(
    string Service,
    string Environment,
    string Version,
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    long TotalRequests,
    int ActiveRequests,
    IReadOnlyDictionary<int, long> StatusCodes,
    IReadOnlyList<RouteOwnershipRecord> RouteOwners,
    IReadOnlyDictionary<string, object?> Dependencies,
    string CorrelationId);
