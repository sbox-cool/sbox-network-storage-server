using System.Collections.Concurrent;

namespace SboxNetworkStorage.Storage;

public partial class InMemoryNetworkStorageStore
{
    private readonly SemaphoreSlim _projectImportGate = new(1, 1);

    public async Task<bool> TryImportProjectAsync(string projectId,
        Func<INetworkStorageStore, CancellationToken, Task> restore, CancellationToken ct)
    {
        Id(projectId);
        ArgumentNullException.ThrowIfNull(restore);
        await _projectImportGate.WaitAsync(ct);
        try
        {
            if (Projects.ContainsKey(projectId)) return false;
            // Test-only staging: failed replay never changes any live dictionary.
            var staged = new InMemoryNetworkStorageStore(_time);
            await restore(staged, ct);
            ct.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
            if (!staged.Projects.TryGetValue(projectId, out var project))
                throw new InvalidOperationException("The import did not restore its project row.");
            if (!Projects.TryAdd(projectId, project)) return false;

            Merge(Collections, staged.Collections);
            Merge(Endpoints, staged.Endpoints);
            Merge(Workflows, staged.Workflows);
            Merge(GameValues, staged.GameValues);
            Merge(RateLimitRules, staged.RateLimitRules);
            Merge(Queries, staged.Queries);
            Merge(Records, staged.Records);
            Merge(GlobalRecords, staged.GlobalRecords);
            Merge(LedgerEntries, staged.LedgerEntries);
            Merge(CheckpointCursors, staged.CheckpointCursors);
            Merge(ApiKeys, staged.ApiKeys);
            Merge(AuditLogs, staged.AuditLogs);
            Merge(PlayerAnalyticsEvents, staged.PlayerAnalyticsEvents);
            Merge(PlayerProfiles, staged.PlayerProfiles);
            Merge(PlayerSessions, staged.PlayerSessions);
            Merge(ProjectMembers, staged.ProjectMembers);
            Merge(Pages, staged.Pages);
            Merge(UsageMonthly, staged.UsageMonthly);
            Merge(UsageDaily, staged.UsageDaily);
            Merge(UsageEndpoints, staged.UsageEndpoints);
            Merge(WorkspaceObjects, staged.WorkspaceObjects);
            return true;
            }
        }
        finally
        {
            _projectImportGate.Release();
        }
    }

    private static void Merge<T>(ConcurrentDictionary<string, T> target, ConcurrentDictionary<string, T> staged)
    {
        foreach (var (key, value) in staged) target[key] = value;
    }
}
