using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class ConfigCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Get_redacts_secret_settings_unless_show_secrets_is_passed()
    {
        var config = Path.Combine(_root, "config");
        ConfigFiles.WriteAll(config, new Dictionary<string, object> { ["updates.check"] = false });
        ConfigFiles.SetValue(config, SettingDefinitions.Find("alerts.smtp.password")!, "hunter2-smtp");
        ConfigFiles.SetValue(config, SettingDefinitions.Find("alerts.smtp.host")!, "mail.example.com");

        // Other test classes may write to the shared console meanwhile, so check content, not equality.
        var redacted = Get(config, "alerts.smtp.password");
        Assert.Contains("********", redacted);
        Assert.DoesNotContain("hunter2-smtp", redacted);
        Assert.Contains("hunter2-smtp", Get(config, "alerts.smtp.password", "--show-secrets"));
        Assert.Contains("mail.example.com", Get(config, "alerts.smtp.host"));
    }

    private string Get(string config, params string[] args)
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(CliApp.Ok, ConfigCommands.Run(new CliContext(CliArguments.Parse(
                ["config", "get", .. args, "--config-dir", config, "--data-dir", Path.Combine(_root, "data")]))));
        }
        finally
        {
            Console.SetOut(original);
        }

        return output.ToString();
    }
}
