using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class AdminSecurityCommandTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("sbox-ns-admin-security-").FullName;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task DisableAndEnablePersistWithoutChangingGameListener()
    {
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Assert.Equal(CliApp.Ok, await CliApp.RunAsync(["adminpanel", "disable", "--config-dir", config, "--data-dir", data]));
        var disabled = ConfigLoader.Load(config, data, null, _ => null);
        Assert.True(disabled.IsValid);
        Assert.False(disabled.GetBoolean("adminpanel.enabled"));
        Assert.Equal("0.0.0.0:8080", disabled.GetString("server.listen"));
        Assert.Equal(CliApp.Ok, await CliApp.RunAsync(["adminpanel", "enable", "--config-dir", config, "--data-dir", data]));
        Assert.True(ConfigLoader.Load(config, data, null, _ => null).GetBoolean("adminpanel.enabled"));
    }

    [Theory]
    [InlineData("192.0.2.0/33")]
    [InlineData("not-an-address")]
    public void InvalidAdminCidrsAreRejected(string cidr)
    {
        var config = ConfigLoader.Load(Path.Combine(root, "config"), Path.Combine(root, "data"),
            new Dictionary<string, string> { ["adminpanel.allowed_ips"] = cidr }, _ => null);
        Assert.Contains(config.Issues, issue => issue.Message.Contains("IP restriction", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledTurnstileRequiresAllOperatorSettings()
    {
        var config = ConfigLoader.Load(Path.Combine(root, "config"), Path.Combine(root, "data"),
            new Dictionary<string, string> { ["adminpanel.turnstile.enabled"] = "true" }, _ => null);
        Assert.Contains(config.Issues, issue => issue.Message.Contains("sitekey", StringComparison.Ordinal));
        Assert.Contains(config.Issues, issue => issue.Message.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(config.Issues, issue => issue.Message.Contains("hostname", StringComparison.Ordinal));
    }
}
