using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>Why a request is refused: the block scope and the owner's public reason.</summary>
public sealed record PlayerBlockDenial(string Scope, string? Reason);

/// <summary>
/// Player blocks for the integrity module. Reads are served from a short
/// in-process cache; every mutation goes through this service and invalidates
/// the entry, so blocking and unblocking take effect immediately.
/// Secret-key (dedicated-server) callers are always exempt.
/// </summary>
public sealed class PlayerBlockService(
    IModuleDataStore modules,
    IMemoryCache cache,
    TimeProvider time)
{
    public const string WritesScope = "writes";
    public const string AllScope = "all";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static bool IsWriteScope(string scope) => string.Equals(scope, WritesScope, StringComparison.Ordinal);
    public static bool IsAllScope(string scope) => string.Equals(scope, AllScope, StringComparison.Ordinal);

    public static bool IsValidScope(string? scope)
        => IsWriteScope(scope ?? string.Empty) || IsAllScope(scope ?? string.Empty);

    /// <summary>
    /// Returns the denial for a blocked player, or null when the request may
    /// proceed. <paramref name="isWrite"/> is true for mutating calls
    /// (endpoint definitions with write/delete steps, non-GET record calls).
    /// </summary>
    public async Task<PlayerBlockDenial?> CheckAsync(string projectId, string? steamId, bool isWrite,
        bool hasSecretKey, CancellationToken ct)
    {
        if (hasSecretKey || string.IsNullOrWhiteSpace(steamId)) return null;
        var block = await ReadCachedAsync(projectId, steamId, ct);
        if (block is null) return null;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (block.ExpiresAtUnixMs.HasValue && block.ExpiresAtUnixMs.Value <= now) return null;
        if (IsAllScope(block.Scope)) return new PlayerBlockDenial(block.Scope, block.Reason);
        if (IsWriteScope(block.Scope) && isWrite) return new PlayerBlockDenial(block.Scope, block.Reason);
        return null;
    }

    public async Task BlockAsync(string projectId, string steamId, string scope, string? reason,
        long? expiresAtUnixMs, string? createdBy, CancellationToken ct)
    {
        if (!IsValidScope(scope)) throw new ArgumentException($"Unknown block scope '{scope}'.", nameof(scope));
        await modules.UpsertBlockAsync(projectId, steamId, scope, reason,
            time.GetUtcNow().ToUnixTimeMilliseconds(), expiresAtUnixMs, createdBy, ct);
        cache.Remove(CacheKey(projectId, steamId));
    }

    public async Task UnblockAsync(string projectId, string steamId, CancellationToken ct)
    {
        await modules.DeleteBlockAsync(projectId, steamId, ct);
        cache.Remove(CacheKey(projectId, steamId));
    }

    public Task<IReadOnlyList<JsonElement>> ListAsync(string projectId, CancellationToken ct)
        => modules.ListBlocksAsync(projectId, ct);

    public async Task DeleteExpiredAsync(CancellationToken ct)
    {
        // Expiry is enforced on read; this pass reclaims rows. Projects are
        // enumerated lazily through blocks only when listed, so expiry cleanup
        // runs per project from the owner Blocks page and the retention pass.
        await Task.CompletedTask;
    }

    public async Task<int> DeleteExpiredForProjectAsync(string projectId, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var removed = 0;
        foreach (var row in await modules.ListBlocksAsync(projectId, ct))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var expires = row.TryGetProperty("expires_at_unix_ms", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64() : (long?)null;
            var steamId = row.TryGetProperty("steam_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
            if (expires.HasValue && expires.Value <= now && steamId is not null)
            {
                await modules.DeleteBlockAsync(projectId, steamId, ct);
                cache.Remove(CacheKey(projectId, steamId));
                removed++;
            }
        }
        return removed;
    }

    private async Task<CachedBlock?> ReadCachedAsync(string projectId, string steamId, CancellationToken ct)
    {
        var key = CacheKey(projectId, steamId);
        if (cache.TryGetValue(key, out CachedBlock? cached)) return cached;
        var row = await modules.ReadBlockAsync(projectId, steamId, ct);
        CachedBlock? block = null;
        if (row.HasValue && row.Value.ValueKind == JsonValueKind.Object)
        {
            block = new CachedBlock(
                row.Value.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() ?? string.Empty : string.Empty,
                row.Value.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
                row.Value.TryGetProperty("expires_at_unix_ms", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetInt64() : null);
        }
        cache.Set(key, block, CacheTtl);
        return block;
    }

    private static string CacheKey(string projectId, string steamId) => $"player-block:{projectId}:{steamId}";

    private sealed record CachedBlock(string Scope, string? Reason, long? ExpiresAtUnixMs);
}
