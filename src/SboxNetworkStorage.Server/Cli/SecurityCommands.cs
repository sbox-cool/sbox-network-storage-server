using System.Globalization;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns security status|rotate-key</c>: the signing key games pin, and the key that unwraps encrypted-request v2 payloads.</summary>
public static class SecurityCommands
{
    public static int Run(CliContext context)
    {
        var command = context.Args.Positional(1);
        if (command is not ("status" or "rotate-key"))
        {
            throw new CliException("Use security status|rotate-key [--grace-seconds N].", CliApp.Usage);
        }

        var config = context.LoadValidConfig();
        var path = config.GetPath("auth.request_encryption_keys_file", config.RuntimeDirectory);
        var signingPath = config.GetPath("auth.security_signing_key_file", config.RuntimeDirectory);
        using var identity = RuntimeIdentity.Enter(config);
        config.EnsureRuntimeDirectory();
        return command == "status" ? Status(signingPath, path) : RotateKey(path, context.Args);
    }

    private static int Status(string signingPath, string path)
    {
        PrintSigningKey(signingPath);

        var now = DateTimeOffset.UtcNow;
        var keyring = RequestEncryptionKeyring.EnsureAndLoad(path, now, Announce);
        Console.WriteLine($"Request encryption keys: {path}");
        Console.WriteLine($"Current key: {keyring.Current.KeyId}");
        Console.WriteLine($"  Fingerprint: {keyring.Current.Fingerprint}");
        Console.WriteLine($"  Created: {keyring.Current.CreatedAt:O}");

        var retired = keyring.Keys.Where(key => !key.IsCurrent).ToArray();
        if (retired.Length == 0)
        {
            Console.WriteLine("Retired keys: none");
            return CliApp.Ok;
        }

        Console.WriteLine("Retired keys:");
        foreach (var key in retired)
        {
            var state = key.GraceUntil > now ? $"accepted until {key.GraceUntil:O}" : $"expired {key.GraceUntil:O}";
            Console.WriteLine($"  {key.KeyId}  retired {key.RetiredAt:O}, {state}");
        }

        return CliApp.Ok;
    }

    private static void PrintSigningKey(string path)
    {
        Console.WriteLine($"Security signing key: {path}");
        if (!File.Exists(path))
        {
            Console.WriteLine("  Not generated yet; the server creates it on first start.");
            return;
        }

        Console.WriteLine($"  Fingerprint: {NetworkStorageSecurityConfigSigningKey.FingerprintOfPem(File.ReadAllText(path))}");
        Console.WriteLine("  Games pin this fingerprint. Replacing the signing key breaks every pinned game until it is repinned.");
    }

    private static int RotateKey(string path, CliArguments args)
    {
        var graceSeconds = ParseGraceSeconds(args.Option("grace-seconds"));
        var now = DateTimeOffset.UtcNow;
        var existed = File.Exists(path);
        var before = RequestEncryptionKeyring.EnsureAndLoad(path, now, Announce);
        if (!existed)
        {
            Console.WriteLine($"No key file existed; created {before.Current.KeyId} as the current key. Nothing was rotated.");
            return CliApp.Ok;
        }

        var after = RequestEncryptionKeyring.Rotate(path, now, graceSeconds);
        var previous = after.Keys.FirstOrDefault(key => key.KeyId == before.Current.KeyId);
        Console.WriteLine($"New current key: {after.Current.KeyId}");
        Console.WriteLine($"  Fingerprint: {after.Current.Fingerprint}");
        Console.WriteLine(previous is null
            ? $"Previous key {before.Current.KeyId} was removed at once (grace 0)."
            : $"Previous key {before.Current.KeyId} still accepts requests until {previous.GraceUntil:O}.");
        Console.WriteLine("Running servers use the new key within a few seconds; no restart is needed.");
        return CliApp.Ok;
    }

    private static int ParseGraceSeconds(string? value)
    {
        if (value is null) return RequestEncryptionKeyring.DefaultGraceSeconds;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > RequestEncryptionKeyring.MaxGraceSeconds)
        {
            throw new CliException(
                $"--grace-seconds must be a whole number from 0 to {RequestEncryptionKeyring.MaxGraceSeconds}.", CliApp.Usage);
        }

        return seconds;
    }

    private static void Announce(string path) => Console.Error.WriteLine($"Generated secret file {path}");
}
