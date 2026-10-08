using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

public interface IPlayerAnalyticsReader
{
    Task<object?> GetProjectAnalyticsAsync(long ownerUserId, string projectId, IReadOnlyDictionary<string, object>? analyticsSettings, string? tab, string? query, int page, CancellationToken cancellationToken);
    Task<object?> GetProjectLedgerInsightsAsync(long ownerUserId, string projectId, ProjectLedgerInsightQuery query, CancellationToken cancellationToken);
    Task<object?> GetPlayerAnalyticsAsync(long ownerUserId, string projectId, string steamId, IReadOnlyDictionary<string, object>? analyticsSettings, PlayerTimelineQuery query, CancellationToken cancellationToken);
    Task<object?> GetPlayerLedgerInsightsAsync(long ownerUserId, string projectId, string steamId, PlayerLedgerInsightQuery query, CancellationToken cancellationToken);
    Task<object?> GetPlayerTransactionsAsync(long ownerUserId, string projectId, string steamId, string? collection, int page, CancellationToken cancellationToken);
    Task<object?> GetPlayerLedgerAsync(long ownerUserId, string projectId, string steamId, string? field, string? source, string? from, string? to, CancellationToken cancellationToken);
    Task<object?> GetProjectLogsAsync(long ownerUserId, string projectId, string? collection, string? steamId, string? op, string? query, int page, CancellationToken cancellationToken);
    Task<object?> GetProjectAuditLogsAsync(long ownerUserId, string projectId, int page, int pageSize, string? search, string? action, string? date, string? sort, CancellationToken cancellationToken);
}

/// <summary>
/// Ledger-insight tuning options parsed from the request query string
/// (parity with <c>ledgerInsightOptionsFromUrl</c> in
/// <c>controllers/storage-modules/insights-routes.js</c>). Null members fall
/// back to the engine defaults.
/// </summary>
public sealed record LedgerInsightQuery(
    string? FieldKey = null,
    double? PointLimit = null,
    double? MilestoneLimit = null,
    double? CorrelationWindowSeconds = null,
    double? CorrelationBeforeSeconds = null,
    double? CorrelationAfterSeconds = null,
    double? AbsoluteThreshold = null,
    double? ProportionalThreshold = null,
    double? SessionNetAbsoluteThreshold = null);

/// <summary>Per-player ledger-insight request: window + tuning options.</summary>
public sealed record PlayerLedgerInsightQuery(int Days, LedgerInsightQuery Options);

/// <summary>Project-wide ledger-insight summary request.</summary>
public sealed record ProjectLedgerInsightQuery(int Days, int PlayerLimit, int Limit, LedgerInsightQuery Options);

/// <summary>Per-player timeline request (parity with <c>routeStoragePlayerAnalyticsTimeline</c>).</summary>
public sealed record PlayerTimelineQuery(int Days, string? Type, string? Group, bool ShowNoise, LedgerInsightQuery Options);
