using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerLoginLinkRecord(DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Single-use owner login links minted on the server by <c>sbox-ns admin login-link</c>: shell access
/// to the server proves authority. The 256-bit token is never persisted; only its SHA-256 (as the object
/// name) and the expiry are stored alongside the owner account in the configured provider.
/// </summary>
public sealed class OwnerLoginLinkService(INetworkStorageStore store)
{
    public const string DirectoryPath = "server/identity/login-links";
    public const int DefaultMinutes = 15;
    public const int MaxMinutes = 60;
    private const int TokenBytes = 32;
    // Consumption only happens inside the single server process; this gate makes read+delete atomic there.
    private static readonly SemaphoreSlim ConsumeGate = new(1, 1);

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(int minutes, CancellationToken ct)
    {
        if (minutes is < 1 or > MaxMinutes)
            throw new ArgumentException($"Login link lifetime must be 1–{MaxMinutes} minutes.");
        var now = DateTimeOffset.UtcNow;
        await PurgeExpiredAsync(now, ct);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        var record = new OwnerLoginLinkRecord(now, now.AddMinutes(minutes));
        await store.PutWorkspaceObjectAsync(ObjectPath(Hash(token)), JsonSerializer.Serialize(record), ct);
        return (token, record.ExpiresAt);
    }

    /// <summary>Checks a token without consuming it (safe for GET and link-preview fetches).</summary>
    public async Task<bool> IsValidAsync(string? token, CancellationToken ct)
        => IsWellFormed(token) && await ReadAsync(Hash(token!), ct) is { } record && record.ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>Atomically consumes a token; true exactly once for an unexpired token.</summary>
    public async Task<bool> TryConsumeAsync(string? token, CancellationToken ct)
    {
        if (!IsWellFormed(token)) return false;
        var path = ObjectPath(Hash(token!));
        await ConsumeGate.WaitAsync(ct);
        try
        {
            var content = await store.ReadWorkspaceObjectAsync(path, ct);
            if (content is null) return false;
            await store.DeleteWorkspaceObjectAsync(path, ct);
            return Parse(content) is { } record && record.ExpiresAt > DateTimeOffset.UtcNow;
        }
        finally { ConsumeGate.Release(); }
    }

    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    public static string ObjectPath(string hash) => $"{DirectoryPath}/{hash}.json";

    private static bool IsWellFormed(string? token)
        => token is { Length: TokenBytes * 2 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<OwnerLoginLinkRecord?> ReadAsync(string hash, CancellationToken ct)
        => await store.ReadWorkspaceObjectAsync(ObjectPath(hash), ct) is { } content ? Parse(content) : null;

    private async Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var entry in await store.ListWorkspaceObjectsAsync(DirectoryPath, ct))
        {
            if (entry.IsDirectory) continue;
            var path = $"{DirectoryPath}/{entry.Name}";
            var content = await store.ReadWorkspaceObjectAsync(path, ct);
            if (content is not null && (Parse(content) is not { } record || record.ExpiresAt <= now))
                await store.DeleteWorkspaceObjectAsync(path, ct);
        }
    }

    private static OwnerLoginLinkRecord? Parse(string content)
    {
        try { return JsonSerializer.Deserialize<OwnerLoginLinkRecord>(content); }
        catch (JsonException) { return null; }
    }
}
