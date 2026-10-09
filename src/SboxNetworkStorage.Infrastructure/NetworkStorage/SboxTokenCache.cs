using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Remembers successful Facepunch token verifications for a short time, keyed by
/// SHA-256(token + steamId) so neither value is stored. The backing
/// <see cref="IMemoryCache"/> is size-limited. Singleton.
/// </summary>
public sealed class SboxTokenCache(IMemoryCache cache, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public bool IsVerified(string token, string steamId)
        => cache.TryGetValue(Key(token, steamId), out long expiresAtMs)
           && expiresAtMs > time.GetUtcNow().ToUnixTimeMilliseconds();

    public void MarkVerified(string token, string steamId)
    {
        using var entry = cache.CreateEntry(Key(token, steamId));
        entry.Size = 1;
        entry.AbsoluteExpirationRelativeToNow = Lifetime;
        entry.Value = time.GetUtcNow().Add(Lifetime).ToUnixTimeMilliseconds();
    }

    public void Dispose() => cache.Dispose();

    private static string Key(string token, string steamId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + steamId)));
}
