using System.Security.Cryptography;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

[Collection("Console")]
public sealed class SecurityCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-security-").FullName;
    private string Config => Path.Combine(_root, "config");
    private string Data => Path.Combine(_root, "data");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Status_reports_the_signing_key_as_not_generated_before_the_first_start()
    {
        ConfigFiles.WriteAll(Config, new Dictionary<string, object> { ["updates.check"] = false });

        var output = Run("status");

        Assert.Contains("Security signing key:", output);
        Assert.Contains("Not generated yet", output);
    }

    [Fact]
    public void Status_prints_the_fingerprint_games_pin_from_the_signing_key_file()
    {
        var expected = WriteSigningKey();

        var output = Run("status");

        Assert.Contains($"Fingerprint: {expected}", output);
        Assert.Contains("Games pin this fingerprint", output);
    }

    [Fact]
    public void Rotate_signing_key_announces_the_previous_key_and_status_lists_it()
    {
        var previous = WriteSigningKey();

        var rotated = Run("rotate-signing-key", "--grace-seconds", "3600");

        Assert.Contains("New signing key:", rotated);
        Assert.Contains($"Previous key {previous} stays announced until", rotated);
        Assert.Contains("Games pinned to the old fingerprint need a repin.", rotated);

        var status = Run("status");
        Assert.Contains($"{previous} -> ", status);
        Assert.Contains("announced until", status);
        Assert.DoesNotContain($"Fingerprint: {previous}", status);
    }

    [Fact]
    public void Rotate_signing_key_without_a_key_file_refuses_and_rotates_nothing()
    {
        ConfigFiles.WriteAll(Config, new Dictionary<string, object> { ["updates.check"] = false });
        var loaded = ConfigLoader.Load(Config, Data, null, _ => null);
        var rotationsPath = loaded.GetPath("auth.security_signing_rotations_file", loaded.RuntimeDirectory);

        var error = Assert.Throws<CliException>(() => Run("rotate-signing-key"));

        Assert.Contains("Nothing was rotated.", error.Message);
        Assert.False(File.Exists(rotationsPath));
    }

    [Fact]
    public void Rotate_signing_key_rejects_a_grace_outside_the_allowed_range()
    {
        WriteSigningKey();

        Assert.Throws<CliException>(() => Run("rotate-signing-key", "--grace-seconds", "2592001"));
    }

    /// <summary>Writes a fresh signing key where the config says, and returns the fingerprint games pin.</summary>
    private string WriteSigningKey()
    {
        ConfigFiles.WriteAll(Config, new Dictionary<string, object> { ["updates.check"] = false });
        var loaded = ConfigLoader.Load(Config, Data, null, _ => null);
        var signingPath = loaded.GetPath("auth.security_signing_key_file", loaded.RuntimeDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(signingPath)!);
        using var signer = RSA.Create(2048);
        File.WriteAllText(signingPath, signer.ExportPkcs8PrivateKeyPem());
        return Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    private string Run(string command, params string[] args)
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var parsed = CliArguments.Parse(["security", command, .. args, "--config-dir", Config, "--data-dir", Data]);
            Assert.Equal(CliApp.Ok, SecurityCommands.Run(new CliContext(parsed)));
        }
        finally
        {
            Console.SetOut(original);
        }

        return output.ToString();
    }
}
