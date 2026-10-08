using System;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Builds <see cref="BackupRun"/> / <see cref="BackupPackage"/> history entries
/// for a finished project export so the Backups surface shows automatic and
/// manual runs consistently. The package id is the <c>scylla_backups</c> export
/// id (as a string), so the project download/verify routes
/// (<c>/backups/{id}/download</c>, <c>/backups/{id}/verify</c>) resolve it directly.
/// </summary>
public static class BackupHistoryFactory
{
    /// <summary>A successful export run, linked to its retained package id.</summary>
    public static BackupRun CompletedRun(string source, DateTime startedAtUtc, DateTime completedAtUtc, int backupId, long sizeBytes)
        => new(
            Id: Guid.NewGuid().ToString(),
            Type: source,
            Status: "completed",
            StartedAt: startedAtUtc.ToString("o"),
            CompletedAt: completedAtUtc.ToString("o"),
            SizeBytes: sizeBytes,
            PackageId: backupId.ToString(),
            Error: null,
            ProgressPct: 100,
            CurrentStepId: "completed",
            CurrentStepLabel: "Completed",
            CurrentStepDetail: $"Exported {sizeBytes} bytes to backup storage.");

    /// <summary>A failed export run with a user-readable reason.</summary>
    public static BackupRun FailedRun(string source, DateTime startedAtUtc, DateTime completedAtUtc, string error)
        => new(
            Id: Guid.NewGuid().ToString(),
            Type: source,
            Status: "failed",
            StartedAt: startedAtUtc.ToString("o"),
            CompletedAt: completedAtUtc.ToString("o"),
            SizeBytes: 0,
            PackageId: null,
            Error: string.IsNullOrWhiteSpace(error) ? "Backup failed." : error,
            ProgressPct: 100,
            CurrentStepId: "failed",
            CurrentStepLabel: "Failed",
            CurrentStepDetail: string.IsNullOrWhiteSpace(error) ? "Backup failed." : error);

    /// <summary>The retained, downloadable package produced by a successful export.</summary>
    public static BackupPackage Package(string source, DateTime createdAtUtc, int backupId, long sizeBytes)
        => new(
            Id: backupId.ToString(),
            Source: source,
            ObjectKey: backupId.ToString(),
            SizeBytes: sizeBytes,
            Status: "available",
            CreatedAt: createdAtUtc.ToString("o"),
            CompletedAt: createdAtUtc.ToString("o"),
            PrunedAt: null,
            PruneReason: null);
}
