using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Media;

internal sealed class FakeMediaStore : IMediaStore
{
    public HashSet<string> Used { get; } = [];
    public List<MediaObjectRecord> Objects { get; } = [];
    public List<MediaGrantRecord> Grants { get; } = [];
    /// <summary>Runs once before the next blob-locked section, to stand in for another writer committing after the batch was read.</summary>
    public Func<Task>? BeforeNextBlobLock { get; set; }

    public Task<bool> TryInsertMediaGrantAsync(MediaGrantRecord grant, long windowStartUnixMs, int perOwnerLimit, CancellationToken ct)
    {
        var inWindow = Grants.Count(g => g.ProjectId == grant.ProjectId && g.OwnerSteamId == grant.OwnerSteamId
            && g.Purpose == grant.Purpose && g.IssuedAtUnixMs >= windowStartUnixMs);
        if (inWindow >= perOwnerLimit) return Task.FromResult(false);
        Grants.Add(grant);
        return Task.FromResult(true);
    }

    public Task<bool> TryConsumeGrantAsync(string projectId, string grantId, long nowUnixMs, CancellationToken ct) =>
        Task.FromResult(Used.Add($"{projectId}/{grantId}"));

    public Task InsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct)
    {
        Objects.Add(record);
        return Task.CompletedTask;
    }

    public Task<MediaObjectRecord?> GetMediaObjectAsync(string projectId, string mediaId, CancellationToken ct) =>
        Task.FromResult(Objects.FirstOrDefault(o => o.ProjectId == projectId && o.MediaId == mediaId));

    public Task<long> SumMediaObjectBytesAsync(string projectId, string purpose, long nowUnixMs, CancellationToken ct) =>
        Task.FromResult(Objects.Where(o => o.ProjectId == projectId && o.Purpose == purpose && o.ExpiresAtUnixMs > nowUnixMs).Sum(o => o.SizeBytes));

    public Task<IReadOnlyList<MediaObjectRecord>> ListMediaObjectsAsync(string projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaObjectRecord>>(Objects.Where(o => o.ProjectId == projectId).ToList());

    public Task UpsertMediaObjectAsync(MediaObjectRecord record, CancellationToken ct)
    {
        Objects.RemoveAll(o => o.ProjectId == record.ProjectId && o.MediaId == record.MediaId);
        Objects.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaObjectRecord>> ListExpiredMediaObjectsAsync(long nowUnixMs, (string ProjectId, string MediaId)? after, int limit, CancellationToken ct)
    {
        IEnumerable<MediaObjectRecord> rows = Objects
            .Where(o => o.ExpiresAtUnixMs <= nowUnixMs)
            .OrderBy(o => o.ProjectId, StringComparer.Ordinal)
            .ThenBy(o => o.MediaId, StringComparer.Ordinal);
        if (after is { } key)
        {
            rows = rows.Where(o => string.CompareOrdinal(o.ProjectId, key.ProjectId) > 0
                || (o.ProjectId == key.ProjectId && string.CompareOrdinal(o.MediaId, key.MediaId) > 0));
        }
        return Task.FromResult<IReadOnlyList<MediaObjectRecord>>(rows.Take(limit).ToList());
    }

    public Task<IReadOnlyList<MediaReference>> ListMediaReferencesBySha256Async(string projectId, string sha256, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MediaReference>>(Objects
            .Where(o => o.ProjectId == projectId && o.Sha256 == sha256)
            .Select(o => new MediaReference(o.MediaId, o.ExpiresAtUnixMs))
            .ToList());

    // The fake has no cross-process lock; the service's in-process stripes are what the sweep and upload tests exercise.
    public async Task RunUnderMediaBlobLockAsync(string projectId, string sha256, Func<IMediaStore, Task> work, CancellationToken ct)
    {
        var hook = BeforeNextBlobLock;
        BeforeNextBlobLock = null;
        if (hook is not null) await hook();
        await work(this);
    }

    public Task<IDisposable> EnterMediaSweepGateAsync(CancellationToken ct) => Task.FromResult<IDisposable>(new NoGate());

    private sealed class NoGate : IDisposable
    {
        public void Dispose() { }
    }

    public Task<bool> DeleteExpiredMediaObjectAsync(string projectId, string mediaId, long nowUnixMs, CancellationToken ct)
    {
        var index = Objects.FindIndex(o => o.ProjectId == projectId && o.MediaId == mediaId && o.ExpiresAtUnixMs <= nowUnixMs);
        if (index < 0) return Task.FromResult(false);
        Objects.RemoveAt(index);
        return Task.FromResult(true);
    }
}

internal sealed class FakeBlobStore : IMediaBlobStore
{
    public Dictionary<(string ProjectId, string Sha256), byte[]> Items { get; } = [];

    /// <summary>Blobs whose delete throws, to exercise the sweeper's retry path.</summary>
    public HashSet<(string ProjectId, string Sha256)> FailingDeletes { get; } = [];

    /// <summary>When set, every delete waits on this before acting, so a test can hold a sweep mid-delete.</summary>
    public Func<CancellationToken, Task>? DeleteGate { get; set; }

    public int Deletes { get; private set; }

    public Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct) =>
        Task.FromResult(Items.ContainsKey((projectId, sha256)));

    public Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct)
    {
        Items[(projectId, sha256)] = bytes;
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string projectId, string sha256, CancellationToken ct)
    {
        if (DeleteGate is not null) await DeleteGate(ct);
        if (FailingDeletes.Contains((projectId, sha256))) throw new IOException("blob backend unavailable");
        Items.Remove((projectId, sha256));
        Deletes++;
    }

    public int OpenReads { get; private set; }

    public Task<MediaBlob?> OpenReadAsync(string projectId, string sha256, CancellationToken ct)
    {
        if (!Items.TryGetValue((projectId, sha256), out var bytes)) return Task.FromResult<MediaBlob?>(null);
        OpenReads++;
        return Task.FromResult<MediaBlob?>(new MediaBlob(new MemoryStream(bytes), bytes.Length, null));
    }
}
