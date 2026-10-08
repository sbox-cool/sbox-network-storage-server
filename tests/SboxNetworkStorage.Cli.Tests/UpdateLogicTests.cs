using System.Security.Cryptography;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class UpdateLogicTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.2.0", "1.10.0", true)]
    [InlineData("1.0.0-rc.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-rc.9", false)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", true)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", true)]
    [InlineData("0.1.0-dev", "0.1.0", true)]
    [InlineData("2.0.0", "1.99.99", false)]
    public void Update_available_follows_semver_precedence(string current, string latest, bool expected)
    {
        var notice = UpdateCheckService.Evaluate(current, new ReleaseInfo(latest, null, false, false, null, null), DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, notice.UpdateAvailable);
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3+build.5", true)]
    [InlineData("1.2", false)]
    [InlineData("latest", false)]
    public void Version_parsing(string text, bool valid)
        => Assert.Equal(valid, SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Unparseable_versions_never_announce_an_update()
    {
        var notice = UpdateCheckService.Evaluate("1.0.0", new ReleaseInfo("garbage", null, false, false, null, null), DateTimeOffset.UnixEpoch);

        Assert.False(notice.UpdateAvailable);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("dotnet.exe")]
    public void In_place_update_refuses_to_replace_the_shared_dotnet_host(string executable)
    {
        var error = Assert.Throws<CliException>(() => UpdateCommands.RequireStandaloneExecutable(executable));
        Assert.Contains("self-contained sbox-ns", error.Message);
    }

    [Fact]
    public void Checksum_verification_accepts_match_and_rejects_mismatch_or_missing_entry()
    {
        var dir = Directory.CreateTempSubdirectory("sbox-ns-sums-").FullName;
        try
        {
            var archive = Path.Combine(dir, "sbox-ns-1.0.0-linux-x64.tar.gz");
            File.WriteAllBytes(archive, [1, 2, 3, 4]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
            var sums = Path.Combine(dir, "SHA256SUMS");

            File.WriteAllText(sums, $"{new string('0', 64)}  other.zip\n{hash}  sbox-ns-1.0.0-linux-x64.tar.gz\n");
            UpdateCommands.VerifyChecksum(archive, "sbox-ns-1.0.0-linux-x64.tar.gz", sums);

            File.WriteAllText(sums, $"{new string('a', 64)} *sbox-ns-1.0.0-linux-x64.tar.gz\n");
            var mismatch = Assert.Throws<CliException>(() => UpdateCommands.VerifyChecksum(archive, "sbox-ns-1.0.0-linux-x64.tar.gz", sums));
            Assert.Contains("checksum mismatch", mismatch.Message);

            File.WriteAllText(sums, $"{hash}  something-else.tar.gz\n");
            var missing = Assert.Throws<CliException>(() => UpdateCommands.VerifyChecksum(archive, "sbox-ns-1.0.0-linux-x64.tar.gz", sums));
            Assert.Contains("not listed", missing.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
