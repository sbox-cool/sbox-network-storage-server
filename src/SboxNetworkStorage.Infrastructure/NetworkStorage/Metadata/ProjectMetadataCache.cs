using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

/// <summary>
/// Per-project <see cref="ProjectMetadataSnapshot"/> cache in <see cref="IMemoryCache"/>. Each project has a
/// generation <see cref="CancellationTokenSource"/> (the same pattern as the API-key resolver): every cached
/// snapshot hangs off its project's current generation, and <see cref="Invalidate"/> cancels it so the next
/// read reloads. The lifetime is only a safety net for changes made by another process.
/// </summary>
public sealed class ProjectMetadataCache(IMemoryCache cache)
{
    private const string KeyPrefix = "project-metadata:";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _generations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loading = new(StringComparer.Ordinal);

    /// <summary>Drops the project's snapshot; the next <see cref="GetAsync"/> reloads it from the store.</summary>
    public void Invalidate(string projectId)
    {
        // Cancel only: live cache entries still reference the token, and reading a disposed source throws.
        if (_generations.TryRemove(projectId, out var generation)) generation.Cancel();
    }

    public async ValueTask<ProjectMetadataSnapshot> GetAsync(INetworkStorageStore source, string projectId, CancellationToken ct)
    {
        var key = KeyPrefix + projectId;
        if (cache.TryGetValue(key, out ProjectMetadataSnapshot? cached) && cached is not null) return cached;

        var gate = _loading.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached) && cached is not null) return cached;

            // Take the generation before reading: a change that lands while the read is in flight cancels it,
            // so the snapshot that read stale rows expires immediately instead of outliving the change.
            var generation = _generations.GetOrAdd(projectId, static _ => new CancellationTokenSource());
            var snapshot = new ProjectMetadataSnapshot(
                await source.ListCollectionsAsync(projectId, ct),
                await source.ListEndpointsAsync(projectId, ct),
                await source.ReadGameValuesAsync(projectId, ct));
            cache.Set(key, snapshot, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime }
                .AddExpirationToken(new CancellationChangeToken(generation.Token)));
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }
}
