using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// ScyllaDB-backed Network Storage API-key resolver.
///
/// Public keys are read directly by exact <c>api_key</c> from the ScyllaDB
/// <c>api_keys</c> table. Secret keys derive the HMAC-based
/// <c>key_identifier</c>, scan the project's keys, then verify the incoming raw
/// key's SHA-256 against <c>key_hash</c> exactly like the Bunny resolver.
///
/// ScyllaDB is authoritative once this resolver is active: misses return null,
/// not a secondary-store fallback. This removes the last auth-critical runtime
/// dependency outside ScyllaDB.
/// </summary>
public sealed class ScyllaStorageApiKeyResolver(
    INetworkStorageStore store,
    IMemoryCache memoryCache,
    IConfiguration configuration,
    ILogger<ScyllaStorageApiKeyResolver> logger) : IStorageApiKeyResolver
{
    private const string PublicCacheKeyPrefix = "scylla-storageapikey:";
    private const string SecretCacheKeyPrefix = "scylla-storagesk:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(projectId))
            return null;
        try
        {
            return apiKey.StartsWith("sbox_sk_", StringComparison.Ordinal)
                ? await ResolveSecretKeyAsync(apiKey, projectId, cancellationToken).ConfigureAwait(false)
                : await ResolvePublicKeyAsync(apiKey, projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ScyllaDB unreachable or under-replicated (UnavailableException,
            // NoHostAvailableException, socket errors, etc.). Log and return
            // null so callers get their existing auth failure response instead
            // of a backend 500. The await above is intentional: returning the
            // inner task would let asynchronous driver exceptions bypass this
            // catch block.
            logger.LogWarning(ex, "ScyllaDB API key resolution failed for project={ProjectId}; returning null", projectId);
            return null;
        }
    }

    private async Task<StorageApiKeyAuthResult?> ResolvePublicKeyAsync(string apiKey, string projectId, CancellationToken ct)
    {
        var cacheKey = PublicCacheKeyPrefix + projectId + ":" + apiKey;
        if (memoryCache.TryGetValue(cacheKey, out StorageApiKeyAuthResult? cached) && cached is not null)
            return cached;

        var row = await store.ReadApiKeyAsync(projectId, apiKey, ct);
        var resolved = row is not null ? MapRow(row.Value, projectId) : null;
        if (resolved is null)
        {
            logger.LogDebug("ScyllaDB public key miss for project={ProjectId}", projectId);
            return null;
        }

        memoryCache.Set(cacheKey, resolved, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });
        return resolved;
    }

    private async Task<StorageApiKeyAuthResult?> ResolveSecretKeyAsync(string rawKey, string projectId, CancellationToken ct)
    {
        var identifier = StorageKeyCrypto.DeriveSecretKeyIdentifier(rawKey, projectId, configuration);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            logger.LogWarning("Failed to derive secret key identifier for project={ProjectId}", projectId);
            return null;
        }

        var cacheKey = SecretCacheKeyPrefix + projectId + ":" + identifier;
        if (memoryCache.TryGetValue(cacheKey, out StorageApiKeyAuthResult? cached) && cached is not null)
            return cached;

        var rows = await store.ListApiKeysAsync(projectId, ct);
        var row = rows.FirstOrDefault(r =>
            r.ValueKind == JsonValueKind.Object
            && TryGetString(r, "key_type") != "public"
            && string.Equals(TryGetString(r, "key_identifier"), identifier, StringComparison.Ordinal));

        if (row.ValueKind != JsonValueKind.Object)
        {
            logger.LogDebug("ScyllaDB secret key miss for project={ProjectId}", projectId);
            return null;
        }

        var storedHash = TryGetString(row, "key_hash");
        var incomingHash = StorageKeyCrypto.HashSecretKey(rawKey);
        if (!string.Equals(storedHash, incomingHash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("ScyllaDB secret key hash mismatch for project={ProjectId}", projectId);
            return null;
        }

        var resolved = MapRow(row, projectId);
        if (resolved is not null)
            memoryCache.Set(cacheKey, resolved, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });
        return resolved;
    }

    private static StorageApiKeyAuthResult? MapRow(JsonElement row, string projectId)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var userIdStr = TryGetString(row, "user_id");
        if (!long.TryParse(userIdStr, out var userId)) return null;
        var enabled = row.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        var keyType = TryGetString(row, "key_type") ?? "public";
        Dictionary<string, string>? permissions = null;
        if (row.TryGetProperty("permissions_json", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            permissions = JsonSerializer.Deserialize<Dictionary<string, string>>(p.GetRawText());
        }
        return new StorageApiKeyAuthResult(userId, projectId, enabled, keyType, permissions);
    }

    private static string? TryGetString(JsonElement row, string name)
        => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
