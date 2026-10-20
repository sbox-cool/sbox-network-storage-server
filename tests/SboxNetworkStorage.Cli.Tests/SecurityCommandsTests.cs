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

        var output = Status();

        Assert.Contains("Security signing key:", output);
        Assert.Contains("Not generated yet", output);
    }

    [Fact]
    public void Status_prints_the_fingerprint_games_pin_from_the_signing_key_file()
    {
        ConfigFiles.WriteAll(Config, new Dictionary<string, object> { ["updates.check"] = false });
        var loaded = ConfigLoader.Load(Config, Data, null, _ => null);
        var signingPath = loaded.GetPath("auth.security_signing_key_file", loaded.RuntimeDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(signingPath)!);
        using var signer = RSA.Create(2048);
        File.WriteAllText(signingPath, signer.ExportPkcs8PrivateKeyPem());
        var expected = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var output = Status();

        Assert.Contains($"Fingerprint: {expected}", output);
        Assert.Contains("Games pin this fingerprint", output);
    }

    private string Status()
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(CliApp.Ok, SecurityCommands.Run(new CliContext(CliArguments.Parse(
                ["security", "status", "--config-dir", Config, "--data-dir", Data]))));
        }
        finally
        {
            Console.SetOut(original);
        }

        return output.ToString();
    }
}
