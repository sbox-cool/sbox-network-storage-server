using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;
using SboxNetworkStorage.Application.Errors;

namespace SboxNetworkStorage.Server.Tests;

// Pins the SpacetimeDB-decommission behavior: project audit logs and player
// analytics are written to and read from ScyllaDB (Bunny is consulted only as a
// legacy fallback when ScyllaDB has nothing). Covers the write-target swap
// (ScyllaAuditLogger / ScyllaPlayerAnalyticsService) and the ScyllaDB read
// mapping used by the audit dashboard.
public sealed class ScyllaNetworkStorageAuditAnalyticsTests
{
    private static NetworkStorageProjectService CreateService(InMemoryNetworkStorageStore store)
        => new(
            bunnyWorkspaceClient: null!,
            bunnyStorageEnumerator: null!,
            new ConfigurationBuilder().Build(),
            keyCdnWriter: null!,
            scyllaStore: store,
            NullLogger<NetworkStorageProjectService>.Instance);

    private static AuditLogRequest MakeAudit(string projectId, string action, string resourceName)
        => new(
            ProjectId: projectId,
            UserId: "77",
            Action: action,
            Actor: new { name = "alice", ip = "1.2.3.4" },
            Target: new { resourceType = "collection", resourceName, resourceSlug = resourceName },
            Summary: new { addedLines = 3, removedLines = 1 },
            Before: null,
            After: null,
            Diff: null);

    [Fact]
    public async Task AuditLog_WrittenToScylla_RoundTripsThroughBrowseProjectLogs()
    {
        var store = new InMemoryNetworkStorageStore();
        var auditLogger = new ScyllaAuditLogger(store, NullLogger<ScyllaAuditLogger>.Instance);

        await auditLogger.LogActionAsync(MakeAudit("proj_audit", "collection-create", "players"), CancellationToken.None);

        var service = CreateService(store);
        var result = await service.BrowseProjectLogsAsync(
            storageOwnerUserId: 77, projectId: "proj_audit",
            search: null, action: null, date: null, sort: "newest",
            page: 1, pageSize: 25, CancellationToken.None);

        var entry = Assert.Single(result.Logs);
        Assert.Equal("collection-create", entry.Action);
        Assert.Equal("alice", entry.ActorName);
        Assert.Equal("1.2.3.4", entry.Ip);
        Assert.Equal("collection", entry.ResourceType);
        Assert.Equal("players", entry.ResourceName);
        Assert.Equal(3, entry.AddedLines);
        Assert.Equal(1, entry.RemovedLines);
        Assert.False(string.IsNullOrEmpty(entry.Timestamp));
    }

    [Fact]
    public async Task BrowseProjectLogs_FiltersByAction()
    {
        var store = new InMemoryNetworkStorageStore();
        var auditLogger = new ScyllaAuditLogger(store, NullLogger<ScyllaAuditLogger>.Instance);
        await auditLogger.LogActionAsync(MakeAudit("proj_filter", "collection-create", "players"), CancellationToken.None);
        await auditLogger.LogActionAsync(MakeAudit("proj_filter", "endpoint-update", "save"), CancellationToken.None);

        var service = CreateService(store);
        var result = await service.BrowseProjectLogsAsync(
            storageOwnerUserId: 77, projectId: "proj_filter",
            search: null, action: "endpoint", date: null, sort: "newest",
            page: 1, pageSize: 25, CancellationToken.None);

        var entry = Assert.Single(result.Logs);
        Assert.Equal("endpoint-update", entry.Action);
    }

    [Fact]
    public async Task BrowseProjectLogs_OtherProjectIsolated()
    {
        var store = new InMemoryNetworkStorageStore();
        var auditLogger = new ScyllaAuditLogger(store, NullLogger<ScyllaAuditLogger>.Instance);
        await auditLogger.LogActionAsync(MakeAudit("proj_a", "collection-create", "players"), CancellationToken.None);

        var service = CreateService(store);
        var result = await service.BrowseProjectLogsAsync(
            storageOwnerUserId: 77, projectId: "proj_b",
            search: null, action: null, date: null, sort: "newest",
            page: 1, pageSize: 25, CancellationToken.None);

        Assert.Empty(result.Logs);
    }

    [Fact]
    public async Task PlayerAnalytics_EventWrittenToScylla()
    {
        var store = new InMemoryNetworkStorageStore();
        var analytics = new PlayerAnalyticsIngester(store, TimeProvider.System, new AnalyticsIngestionFailureTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsIngestionFailureTracker>.Instance), NullLogger<PlayerAnalyticsIngester>.Instance);

        await analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: "proj_pa",
            CollectionId: "coll1",
            RecordKey: "76561198000000000",
            EventType: "save",
            Payload: new { level = 42 }),
            CancellationToken.None);

        // The V2 ingester writes to player_analytics_events (per-player timeline)
        // and player_profiles (presence). The legacy player_analytics table is
        // no longer written.
        var events = await store.ListPlayerEventsAsync(
            "proj_pa", "76561198000000000", 0, long.MaxValue, 10, CancellationToken.None);
        var ev = Assert.Single(events);
        Assert.Equal("save", ev.GetProperty("event_type").GetString());
        Assert.Equal("custom", ev.GetProperty("category").GetString());

        var profile = await store.ReadPlayerProfileAsync("proj_pa", "76561198000000000", CancellationToken.None);
        Assert.NotNull(profile);
        Assert.True(profile.Value.GetProperty("is_online").GetBoolean());
        Assert.Equal("save", profile.Value.GetProperty("last_event_type").GetString());
    }
}
