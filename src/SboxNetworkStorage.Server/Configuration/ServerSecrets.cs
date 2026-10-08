using System.Security.Cryptography;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Resolved secret material the Network Storage core reads from configuration.</summary>
public sealed record ServerSecretValues(string AuthSessionSecret, string StorageEncryptionKeyHex, string SecuritySigningKeyPem);

/// <summary>
/// Creates (once) and loads the server's secret files. Files are created with
/// owner-only permissions on Unix and are never overwritten, because rotating
/// them invalidates player sessions, secret API keys and signed security configs.
/// </summary>
public static class ServerSecrets
{
    public static ServerSecretValues EnsureAndLoad(EffectiveConfig config, Action<string>? onCreated = null)
    {
        var sessionPath = config.GetPath("auth.session_secret_file", config.ConfigDirectory);
        var encryptionPath = config.GetPath("auth.storage_encryption_key_file", config.ConfigDirectory);
        var signingPath = config.GetPath("auth.security_signing_key_file", config.ConfigDirectory);

        EnsureFile(sessionPath, () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), onCreated);
        EnsureFile(encryptionPath, () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), onCreated);
        EnsureFile(signingPath, () =>
        {
            using var rsa = RSA.Create(2048);
            return rsa.ExportPkcs8PrivateKeyPem();
        }, onCreated);

        var encryptionKey = File.ReadAllText(encryptionPath).Trim();
        if (encryptionKey.Length < 64 || !encryptionKey[..64].All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"{encryptionPath} must contain at least 64 hexadecimal characters.");
        }

        return new ServerSecretValues(
            File.ReadAllText(sessionPath).Trim(),
            encryptionKey,
            File.ReadAllText(signingPath));
    }

    private static void EnsureFile(string path, Func<string> generate, Action<string>? onCreated)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(generate());
        }

        onCreated?.Invoke(path);
    }
}
