using System.Globalization;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// <c>sbox-ns security status|rotate-key|rotate-signing-key</c>: the signing key games pin and the rotations that let them
/// follow it, and the key that unwraps encrypted-request v2 payloads.
/// </summary>
public static class SecurityCommands
{
    public static int Run(CliContext context)
    {
        var command = context.Args.Positional(1);
        if (command is not ("status" or "rotate-key" or "rotate-signing-key"))
        {
            throw new CliException("Use security status|rotate-key|rotate-signing-key [--grace-seconds N].", CliApp.Usage);
        }

        var config = context.LoadValidConfig();
        var path = config.GetPath("auth.request_encryption_keys_file", config.RuntimeDirectory);
        var signingPath = config.GetPath("auth.security_signing_key_file", config.RuntimeDirectory);
        var rotationsPath = config.GetPath("auth.security_signing_rotations_file", config.RuntimeDirectory);
        using var identity = RuntimeIdentity.Enter(config);
        config.EnsureRuntimeDirectory();
        return command switch
        {
            "status" => Status(signingPath, rotationsPath, config.GetString("auth.security_signing_key_id"), path),
            "rotate-key" => RotateKey(path, context.Args),
            _ => RotateSigningKey(signingPath, rotationsPath, context.Args),
        };
    }

    private static int Status(string signingPath, string rotationsPath, string configuredKeyId, string path)
    {
        PrintSigningKey(signingPath, rotationsPath, configuredKeyId);

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

    private static void PrintSigningKey(string keyPath, string rotationsPath, string configuredKeyId)
    {
        Console.WriteLine($"Security signing key: {keyPath}");
        if (!File.Exists(keyPath))
        {
            Console.WriteLine("  Not generated yet; the server creates it on first start.");
            return;
        }

        var signing = new SecuritySigningKeyStore(keyPath, rotationsPath, configuredKeyId).Reload();
        Console.WriteLine($"  Key id: {signing.KeyId}");
        Console.WriteLine($"  Fingerprint: {signing.Fingerprint}");
        Console.WriteLine("  Games pin this fingerprint. Rotating announces the new key to games that trust the old one; pinned games must be repinned.");
        if (signing.Rotations.Count == 0)
        {
            Console.WriteLine("  Announced rotations: none");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Console.WriteLine("  Announced rotations (oldest first):");
        foreach (var link in signing.Rotations)
        {
            var state = link.NotAfter >= now ? $"announced until {link.NotAfter:O}" : $"expired {link.NotAfter:O}";
            Console.WriteLine($"    {link.PreviousFingerprint} -> {link.NextFingerprint}  {state}");
        }
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

    private static int RotateSigningKey(string keyPath, string rotationsPath, CliArguments args)
    {
        var graceSeconds = ParseGraceSeconds(args.Option("grace-seconds"));
        SigningKeyRotation rotation;
        try
        {
            rotation = SecuritySigningKeyStore.Rotate(keyPath, rotationsPath, DateTimeOffset.UtcNow, graceSeconds);
        }
        catch (FileNotFoundException)
        {
            throw new CliException($"No signing key file exists at {keyPath}; start the server once to create it. Nothing was rotated.");
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException($"{ex.Message} Nothing was rotated.");
        }

        TunnelFiles.Restrict(keyPath);
        TunnelFiles.Restrict(rotationsPath);
        Console.WriteLine($"New signing key: {rotation.NextFingerprint}");
        Console.WriteLine($"  Key id: {rotation.NextKeyId}");
        Console.WriteLine(graceSeconds == 0
            ? $"Previous key {rotation.PreviousFingerprint} is not announced (grace 0); games that trust it cannot follow and need a reset or repin."
            : $"Previous key {rotation.PreviousFingerprint} stays announced until {rotation.AnnouncedUntil:O}; games that trust it follow the rotation.");
        Console.WriteLine("Running servers use the new key within a few seconds; no restart is needed.");
        Console.WriteLine("Games pinned to the old fingerprint need a repin.");
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
