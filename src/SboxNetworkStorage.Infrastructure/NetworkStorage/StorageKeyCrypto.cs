using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public static class StorageKeyCrypto
{
    private const int KeyHexLength = 64;

    public static bool IsEncryptionAvailable(IConfiguration configuration)
    {
        var hex = GetConfiguredKeyHex(configuration);
        return !string.IsNullOrEmpty(hex) && hex.Length >= KeyHexLength;
    }

    public static string? GetStorageEncryptionKeyHex(IConfiguration configuration)
    {
        return GetConfiguredKeyHex(configuration);
    }

    private static string? GetConfiguredKeyHex(IConfiguration configuration)
    {
        var value = configuration["STORAGE_ENCRYPTION_KEY"] ?? string.Empty;
        return value.Trim();
    }

    public static byte[]? GetKeyBytes(IConfiguration configuration)
    {
        var hex = GetConfiguredKeyHex(configuration);
        if (string.IsNullOrEmpty(hex) || hex.Length < KeyHexLength) return null;

        var normalized = hex[..KeyHexLength];
        if (!Regex.IsMatch(normalized, "^[0-9a-fA-F]+$")) return null;

        var bytes = Convert.FromHexString(normalized);
        return bytes.Length == 32 ? bytes : null;
    }

    public static string HashSecretKey(string rawKey)
    {
        var bytes = Encoding.UTF8.GetBytes(rawKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? DeriveSecretKeyIdentifier(string rawKey, string projectId, IConfiguration configuration)
    {
        var keyBytes = GetKeyBytes(configuration);
        if (keyBytes is null) return null;

        using var hmac = new HMACSHA256(keyBytes);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{rawKey}:{projectId}"));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    public static string MaskSecretKey(string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey) || rawKey.Length < 16) return "sbox_sk_****";
        return $"sbox_sk_{rawKey[8..12]}...{rawKey[^4..]}";
    }
}
