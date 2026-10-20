using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public enum SyncJobLookupState
{
    Found,
    NotFound,
    Expired,
}

/// <summary>Outcome of <see cref="ManagementSyncJobStore.Lookup"/>; <see cref="Job"/> is set only when Found.</summary>
public readonly record struct SyncJobLookup(SyncJobLookupState State, JsonObject? Job);

/// <summary>
/// In-memory async sync jobs behind <c>PUT endpoints?async=true</c> and <c>GET sync-jobs/:jobId</c>.
/// Mirrors the reference sync-job-manager: a job expires 5 minutes after creation unless it is still
/// processing, and an expired job reads as <see cref="SyncJobLookupState.Expired"/> once before it is forgotten.
/// Jobs live in this process only; a multi-instance deployment needs a shared store.
/// </summary>
public sealed class ManagementSyncJobStore
{
    public static readonly TimeSpan JobTtl = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, SyncJob> _jobs = new(StringComparer.Ordinal);

    public ManagementSyncJobStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Creates an accepted job owned by one user and project and returns its id.</summary>
    public string Create(long userId, string projectId, string resourceType)
    {
        lock (_gate)
        {
            PruneExpired();
            var now = _time.GetUtcNow();
            var id = $"sync_{now.ToUnixTimeMilliseconds():x}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
            var job = new SyncJob(id, userId, projectId, resourceType, now);
            job.Stages.Add(new SyncJobStage("accepted", "complete", "Sync request accepted", now));
            _jobs[id] = job;
            return id;
        }
    }

    /// <summary>Appends a stage transition. Unknown or expired jobs are ignored.</summary>
    public void Stage(string jobId, string name, string status, string detail)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            job.Stages.Add(new SyncJobStage(name, status, detail, _time.GetUtcNow()));
            if (status == "processing")
            {
                job.Status = "processing";
            }
        }
    }

    public void Complete(string jobId, JsonNode results)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            var now = _time.GetUtcNow();
            job.Status = "complete";
            job.Results = results;
            job.CompletedAt = now;
            job.Stages.Add(new SyncJobStage("complete", "complete", "Sync completed", now));
        }
    }

    public void Fail(string jobId, JsonObject error)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return;
            }

            var now = _time.GetUtcNow();
            job.Status = "failed";
            job.Error = error;
            job.CompletedAt = now;
            job.Stages.Add(new SyncJobStage("failed", "failed", error["message"]?.GetValue<string>() ?? "Unknown error", now));
        }
    }

    /// <summary>Looks up a job for one user and project. Other owners' jobs read as NotFound.</summary>
    public SyncJobLookup Lookup(string jobId, long userId, string projectId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.UserId != userId || job.ProjectId != projectId)
            {
                return new SyncJobLookup(SyncJobLookupState.NotFound, null);
            }

            if (IsExpired(job))
            {
                _jobs.Remove(jobId);
                return new SyncJobLookup(SyncJobLookupState.Expired, null);
            }

            return new SyncJobLookup(SyncJobLookupState.Found, Snapshot(job));
        }
    }

    private bool IsExpired(SyncJob job) =>
        job.Status != "processing" && _time.GetUtcNow() - job.CreatedAt > JobTtl;

    private void PruneExpired()
    {
        foreach (var (id, job) in _jobs.ToList())
        {
            if (IsExpired(job))
            {
                _jobs.Remove(id);
            }
        }
    }

    private static JsonObject Snapshot(SyncJob job)
    {
        var stages = new JsonArray();
        foreach (var stage in job.Stages)
        {
            stages.Add(new JsonObject
            {
                ["name"] = stage.Name,
                ["status"] = stage.Status,
                ["detail"] = stage.Detail,
                ["at"] = Iso(stage.At),
            });
        }

        return new JsonObject
        {
            ["id"] = job.Id,
            ["userId"] = job.UserId,
            ["projectId"] = job.ProjectId,
            ["resourceType"] = job.ResourceType,
            ["status"] = job.Status,
            ["stages"] = stages,
            ["results"] = job.Results?.DeepClone(),
            ["error"] = job.Error?.DeepClone(),
            ["createdAt"] = Iso(job.CreatedAt),
            ["completedAt"] = job.CompletedAt is { } done ? Iso(done) : null,
        };
    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class SyncJob(string id, long userId, string projectId, string resourceType, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public long UserId { get; } = userId;
        public string ProjectId { get; } = projectId;
        public string ResourceType { get; } = resourceType;
        public string Status { get; set; } = "accepted";
        public List<SyncJobStage> Stages { get; } = [];
        public JsonNode? Results { get; set; }
        public JsonObject? Error { get; set; }
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private sealed record SyncJobStage(string Name, string Status, string Detail, DateTimeOffset At);
}
