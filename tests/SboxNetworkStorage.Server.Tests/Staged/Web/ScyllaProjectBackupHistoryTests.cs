using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Covers the regression where automatic/manual project exports were recorded
/// only in the Postgres <c>scylla_backups</c> table and never surfaced in the
/// Backups page's run history / stored-packages sections (which read the
/// ScyllaDB project payload). <see cref="ScyllaProjectBackupService.RecordBackupResultAsync"/>
/// is the seam that closes that gap.
/// </summary>
public sealed class ScyllaProjectBackupHistoryTests
{
    private const string ProjectId = "6c22075ca036481e";

    private static ScyllaProjectBackupService CreateService(out InMemoryNetworkStorageStore store)
    {
        store = new InMemoryNetworkStorageStore();
        return new ScyllaProjectBackupService(store, TimeProvider.System, NullLogger<ScyllaProjectBackupService>.Instance);
    }

    private static void SeedProjectWithSettings(InMemoryNetworkStorageStore store)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            name = "Test Project",
            backupSettings = new { enabled = true, frequencyMinutes = 10080, retentionCount = 7, pruneOnStoragePressure = true },
        });
        store.UpsertProjectAsync(ProjectId, payload, 1, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RecordBackupResultAsync_SurfacesRunAndPackageOnBackupsSurface()
    {
        var svc = CreateService(out var store);
        SeedProjectWithSettings(store);

        var startedAt = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
        var run = BackupHistoryFactory.CompletedRun("automatic", startedAt, startedAt.AddSeconds(3), backupId: 4242, sizeBytes: 9001);
        var package = BackupHistoryFactory.Package("automatic", startedAt.AddSeconds(3), backupId: 4242, sizeBytes: 9001);

        await svc.RecordBackupResultAsync(0, ProjectId, run, package, CancellationToken.None);

        var runs = await svc.ListBackupRunsAsync(0, ProjectId, 10, CancellationToken.None);
        var recorded = Assert.Single(runs);
        Assert.Equal("automatic", recorded.Type);
        Assert.Equal("completed", recorded.Status);
        Assert.Equal("4242", recorded.PackageId);
        Assert.Equal(9001, recorded.SizeBytes);

        var packages = await svc.ListBackupPackagesAsync(0, ProjectId, 10, CancellationToken.None);
        var pkg = Assert.Single(packages);
        Assert.Equal("4242", pkg.Id);
        Assert.Equal("automatic", pkg.Source);
        Assert.Equal("available", pkg.Status);
        Assert.Null(pkg.PrunedAt);
    }

    [Fact]
    public async Task RecordBackupResultAsync_PreservesExistingSettings()
    {
        var svc = CreateService(out var store);
        SeedProjectWithSettings(store);

        var now = DateTime.UtcNow;
        await svc.RecordBackupResultAsync(
            0, ProjectId,
            BackupHistoryFactory.CompletedRun("manual", now, now, 1, 100),
            BackupHistoryFactory.Package("manual", now, 1, 100),
            CancellationToken.None);

        // Recording a run must not clobber the project's backup schedule.
        var settings = await svc.GetBackupSettingsAsync(0, ProjectId, CancellationToken.None);
        Assert.True(settings.Enabled);
        Assert.Equal(10080, settings.FrequencyMinutes);
    }

    [Fact]
    public async Task RecordBackupResultAsync_FailedRunRecordsNoPackage()
    {
        var svc = CreateService(out var store);
        SeedProjectWithSettings(store);

        var now = DateTime.UtcNow;
        await svc.RecordBackupResultAsync(
            0, ProjectId,
            BackupHistoryFactory.FailedRun("automatic", now, now.AddSeconds(1), "upload to Bunny CDN failed"),
            package: null,
            CancellationToken.None);

        var run = Assert.Single(await svc.ListBackupRunsAsync(0, ProjectId, 10, CancellationToken.None));
        Assert.Equal("failed", run.Status);
        Assert.Equal("upload to Bunny CDN failed", run.Error);
        Assert.Null(run.PackageId);

        Assert.Empty(await svc.ListBackupPackagesAsync(0, ProjectId, 10, CancellationToken.None));
    }

    [Fact]
    public async Task RecordBackupResultAsync_TrimsHistoryToBoundedCap()
    {
        var svc = CreateService(out var store);
        SeedProjectWithSettings(store);

        var baseTime = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 30; i++)
        {
            var t = baseTime.AddMinutes(i);
            await svc.RecordBackupResultAsync(
                0, ProjectId,
                BackupHistoryFactory.CompletedRun("automatic", t, t, backupId: i, sizeBytes: i),
                BackupHistoryFactory.Package("automatic", t, backupId: i, sizeBytes: i),
                CancellationToken.None);
        }

        // The payload is byte-bounded, so history is capped at the most-recent 25.
        var runs = await svc.ListBackupRunsAsync(0, ProjectId, 100, CancellationToken.None);
        Assert.Equal(25, runs.Count);
        // Newest first; the oldest five (backupId 0..4) were dropped.
        Assert.Equal("29", runs.First().PackageId);
        Assert.DoesNotContain(runs, r => r.PackageId == "4");

        var packages = await svc.ListBackupPackagesAsync(0, ProjectId, 100, CancellationToken.None);
        Assert.Equal(25, packages.Count);
        Assert.Equal("29", packages.First().Id);
    }

    [Fact]
    public void BackupHistoryFactory_LinksPackageIdToScyllaBackupId()
    {
        var now = DateTime.UtcNow;
        var run = BackupHistoryFactory.CompletedRun("manual", now, now, backupId: 77, sizeBytes: 5);
        Assert.Equal("77", run.PackageId);
        Assert.Equal("completed", run.Status);

        var pkg = BackupHistoryFactory.Package("manual", now, backupId: 77, sizeBytes: 5);
        Assert.Equal("77", pkg.Id);
        Assert.Equal("available", pkg.Status);

        var failed = BackupHistoryFactory.FailedRun("manual", now, now, "boom");
        Assert.Null(failed.PackageId);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("boom", failed.Error);
    }
}
