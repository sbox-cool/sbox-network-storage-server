using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerAccount(string Username, string PasswordHash, string SecurityStamp, DateTimeOffset CreatedAt);

/// <summary>One local owner, stored alongside workspace objects in the configured provider.</summary>
public sealed class OwnerAccountService(INetworkStorageStore store, EffectiveConfig config)
{
    public const string AccountPath = "server/identity/owner.json";
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly PasswordHasher<OwnerAccount> Hasher = new(Options.Create(new PasswordHasherOptions
    {
        IterationCount = 210_000,
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3
    }));
    private static readonly OwnerAccount Dummy = new("owner", string.Empty, string.Empty, DateTimeOffset.MinValue);
    private static readonly string DummyHash = Hasher.HashPassword(Dummy, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public async Task<OwnerAccount?> GetAsync(CancellationToken ct)
    {
        var content = await store.ReadWorkspaceObjectAsync(AccountPath, ct);
        return content is null ? null : JsonSerializer.Deserialize<OwnerAccount>(content)
            ?? throw new InvalidDataException("The persisted owner account is invalid.");
    }

    public async Task<OwnerAccount?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var account = await GetAsync(ct);
        var candidate = account ?? Dummy;
        var result = Hasher.VerifyHashedPassword(candidate, account?.PasswordHash ?? DummyHash, password);
        return account is not null && result != PasswordVerificationResult.Failed
            && string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase) ? account : null;
    }

    public Task<OwnerAccount> CreateAsync(string username, string password, CancellationToken ct)
        => MutateAsync(username, password, reset: false, ct);

    public Task<OwnerAccount> ResetPasswordAsync(string password, CancellationToken ct)
        => MutateAsync(null, password, reset: true, ct);

    public static void ValidateCredentials(string? username, string password)
    {
        if (username is not null && (username.Trim().Length is < 1 or > 64 || username.Any(char.IsControl)))
            throw new ArgumentException("Owner username must contain 1–64 characters and no control characters.");
        if (password.Length is < 12 or > 1024)
            throw new ArgumentException("Owner password must contain 12–1024 characters.");
    }

    private async Task<OwnerAccount> MutateAsync(string? username, string password, bool reset, CancellationToken ct)
    {
        ValidateCredentials(username, password);
        await MutationGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(config.DataDirectory);
            // Serialize CLI and web mutations on this single-instance server. A second
            // process fails closed rather than racing a first-owner creation/reset.
            await using var fileLock = new FileStream(Path.Combine(config.DataDirectory, "owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await GetAsync(ct);
            if (!reset && current is not null) throw new InvalidOperationException("An owner already exists. Use admin reset-password.");
            if (reset && current is null) throw new InvalidOperationException("No owner exists. Use admin create.");
            var account = new OwnerAccount(reset ? current!.Username : username!.Trim(), string.Empty,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), current?.CreatedAt ?? DateTimeOffset.UtcNow);
            account = account with { PasswordHash = Hasher.HashPassword(account, password) };
            await store.PutWorkspaceObjectAsync(AccountPath, JsonSerializer.Serialize(account), ct);
            return account;
        }
        finally { MutationGate.Release(); }
    }
}

/// <summary>Ephemeral, single-use setup capability; rotated on every server start.</summary>
public sealed class OwnerSetupToken
{
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
    private readonly DateTimeOffset _expires = DateTimeOffset.UtcNow.AddHours(2);
    public string Value => Convert.ToHexString(_token);
    public bool IsValid(string? token)
    {
        if (DateTimeOffset.UtcNow >= _expires || token is null || token.Length != 64) return false;
        Span<byte> supplied = stackalloc byte[32];
        for (var index = 0; index < supplied.Length; index++)
        {
            var high = HexDigit(token[index * 2]);
            var low = HexDigit(token[index * 2 + 1]);
            if (high < 0 || low < 0) return false;
            supplied[index] = (byte)((high << 4) | low);
        }
        return CryptographicOperations.FixedTimeEquals(_token, supplied);
    }

    private static int HexDigit(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };
}
