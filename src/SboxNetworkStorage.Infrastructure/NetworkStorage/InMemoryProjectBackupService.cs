using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public sealed class InMemoryProjectBackupService : IProjectBackupService
{
    private readonly ConcurrentDictionary<(long UserId, string ProjectId), BackupSettings> _settings = new();
    private readonly ConcurrentDictionary<string, BackupPackage> _packages = new();
    private readonly ConcurrentDictionary<string, BackupRun> _runs = new();

    public Task<BackupSettings> GetBackupSettingsAsync(long userId, string projectId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_settings.GetValueOrDefault((userId, projectId), new BackupSettings(true, 1440, 7, true, null)));
    }

    public Task SaveBackupSettingsAsync(long userId, string projectId, BackupSettings settings, CancellationToken cancellationToken)
    {
        _settings[(userId, projectId)] = settings with { UpdatedAt = DateTime.UtcNow.ToString("o") };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackupPackage>> ListBackupPackagesAsync(long userId, string projectId, int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult((IReadOnlyList<BackupPackage>)_packages.Values
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .ToList()
            .AsReadOnly());
    }

    public Task<IReadOnlyList<BackupRun>> ListBackupRunsAsync(long userId, string projectId, int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult((IReadOnlyList<BackupRun>)_runs.Values
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToList()
            .AsReadOnly());
    }

    public Task<string> StartBackupJobAsync(long userId, string projectId, string source, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString();
        var run = new BackupRun(
            id, source, "running", DateTime.UtcNow.ToString("o"), null, 0, null, null, 0, "queued", "Queued", "");
        _runs[id] = run;
        return Task.FromResult(id);
    }

    public Task<BackupRun?> GetBackupJobStatusAsync(long userId, string projectId, string jobId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_runs.GetValueOrDefault(jobId));
    }

    public Task RecordBackupResultAsync(long userId, string projectId, BackupRun run, BackupPackage? package, CancellationToken cancellationToken)
    {
        _runs[run.Id] = run;
        if (package is not null) _packages[package.Id] = package;
        return Task.CompletedTask;
    }
}
