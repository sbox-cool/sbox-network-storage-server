using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

public sealed record BackupSettings(
    bool Enabled,
    int FrequencyMinutes,
    int RetentionCount,
    bool PruneOnStoragePressure,
    string? UpdatedAt
);

public sealed record BackupPackage(
    string Id,
    string Source,
    string ObjectKey,
    long SizeBytes,
    string Status,
    string CreatedAt,
    string? CompletedAt,
    string? PrunedAt,
    string? PruneReason
);

public sealed record BackupRun(
    string Id,
    string Type,
    string Status,
    string StartedAt,
    string? CompletedAt,
    long SizeBytes,
    string? PackageId,
    string? Error,
    int ProgressPct,
    string CurrentStepId,
    string CurrentStepLabel,
    string CurrentStepDetail
);

public interface IProjectBackupService
{
    Task<BackupSettings> GetBackupSettingsAsync(long userId, string projectId, CancellationToken cancellationToken);
    Task SaveBackupSettingsAsync(long userId, string projectId, BackupSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupPackage>> ListBackupPackagesAsync(long userId, string projectId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRun>> ListBackupRunsAsync(long userId, string projectId, int limit, CancellationToken cancellationToken);
    Task<string> StartBackupJobAsync(long userId, string projectId, string source, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
    Task<BackupRun?> GetBackupJobStatusAsync(long userId, string projectId, string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a finished backup attempt (automatic or manual) in the project's
    /// backup history so the Backups surface shows the run and, when a package
    /// was produced, the retained downloadable package. <paramref name="package"/>
    /// is null for failed runs that produced no downloadable export.
    /// </summary>
    Task RecordBackupResultAsync(long userId, string projectId, BackupRun run, BackupPackage? package, CancellationToken cancellationToken);
}
