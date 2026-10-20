using System.Globalization;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Lifetime and ownership rules of the async sync-job store behind PUT endpoints?async=true.</summary>
public sealed class ManagementSyncJobStoreTests
{
    [Fact]
    public void FinishedJobsExpireOnceAfterTheTtlWhileProcessingJobsStayReadable()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var store = new ManagementSyncJobStore(clock);
        var finished = store.Create(1, "project-a", "endpoint");
        store.Complete(finished, new JsonObject { ["ok"] = true });
        var running = store.Create(1, "project-a", "endpoint");
        store.Stage(running, "saving", "processing", "Saving to storage...");

        clock.Advance(ManagementSyncJobStore.JobTtl + TimeSpan.FromSeconds(1));

        Assert.Equal(SyncJobLookupState.Expired, store.Lookup(finished, 1, "project-a").State);
        Assert.Equal(SyncJobLookupState.NotFound, store.Lookup(finished, 1, "project-a").State);
        Assert.Equal(SyncJobLookupState.Found, store.Lookup(running, 1, "project-a").State);
    }

    [Fact]
    public void LookupIsScopedToTheOwningUserAndProject()
    {
        var store = new ManagementSyncJobStore(new ManualClock(DateTimeOffset.UtcNow));
        var jobId = store.Create(1, "project-a", "endpoint");

        Assert.Equal(SyncJobLookupState.NotFound, store.Lookup(jobId, 2, "project-a").State);
        Assert.Equal(SyncJobLookupState.NotFound, store.Lookup(jobId, 1, "project-b").State);
        Assert.Equal(SyncJobLookupState.Found, store.Lookup(jobId, 1, "project-a").State);
    }

    [Fact]
    public void FailedJobsReportTheirTerminalStatusAndError()
    {
        var store = new ManagementSyncJobStore(new ManualClock(DateTimeOffset.UtcNow));
        var jobId = store.Create(1, "project-a", "endpoint");

        store.Fail(jobId, new JsonObject { ["code"] = "VALIDATION_FAILED", ["message"] = "bad" });

        var lookup = store.Lookup(jobId, 1, "project-a");
        Assert.Equal(SyncJobLookupState.Found, lookup.State);
        Assert.Equal("failed", lookup.Job!["status"]!.GetValue<string>());
        Assert.Equal("VALIDATION_FAILED", lookup.Job!["error"]!["code"]!.GetValue<string>());
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
