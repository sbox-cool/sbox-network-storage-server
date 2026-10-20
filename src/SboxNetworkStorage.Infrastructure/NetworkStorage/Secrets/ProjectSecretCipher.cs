using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SboxNetworkStorage.Application.NetworkStorage.Secrets;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Secrets;

/// <summary>
/// AES-256-GCM sealing for project secrets. The AES key is derived from STORAGE_ENCRYPTION_KEY with HKDF-SHA256 and a
/// purpose label, so the raw key (which also keys secret key identifiers) is never used directly for encryption.
/// Token layout, base64 encoded: version (1 byte) | nonce (12) | tag (16) | ciphertext. Each value uses a fresh random
/// nonce. The associated data is the project id and name, so a sealed value cannot be moved to another name or project.
/// </summary>
public sealed class ProjectSecretCipher : IProjectSecretCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("sbox-ns project-secrets v1");

    private readonly byte[]? _key;

    public ProjectSecretCipher(IConfiguration configuration)
    {
        var raw = StorageKeyCrypto.GetKeyBytes(configuration);
        _key = raw is null ? null : HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, KeySize, info: HkdfInfo);
    }

    public bool IsAvailable => _key is not null;

    public string Seal(string projectId, string name, string plaintext)
    {
        var key = RequireKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, data, ciphertext, tag, Aad(projectId, name));
        }

        var token = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        token[0] = Version;
        nonce.CopyTo(token, 1);
        tag.CopyTo(token, 1 + NonceSize);
        ciphertext.CopyTo(token, 1 + NonceSize + TagSize);
        return Convert.ToBase64String(token);
    }

    public string Open(string projectId, string name, string sealedValue)
    {
        var key = RequireKey();
        var token = TryDecodeToken(sealedValue)
            ?? throw new ProjectSecretUnavailableException("A stored project secret is not in a recognised format.");

        var nonce = token[1..(1 + NonceSize)];
        var tag = token[(1 + NonceSize)..(1 + NonceSize + TagSize)];
        var ciphertext = token[(1 + NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(projectId, name));
        }
        catch (CryptographicException)
        {
            throw new ProjectSecretUnavailableException("A stored project secret cannot be decrypted with the configured STORAGE_ENCRYPTION_KEY.");
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] RequireKey() => _key
        ?? throw new ProjectSecretUnavailableException("STORAGE_ENCRYPTION_KEY is required to store or read project secrets.");

    private static byte[]? TryDecodeToken(string sealedValue)
    {
        byte[] token;
        try
        {
            token = Convert.FromBase64String(sealedValue);
        }
        catch (FormatException)
        {
            return null;
        }
        return token.Length >= 1 + NonceSize + TagSize && token[0] == Version ? token : null;
    }

    private static byte[] Aad(string projectId, string name) => Encoding.UTF8.GetBytes($"{projectId}\0{name}");
}
