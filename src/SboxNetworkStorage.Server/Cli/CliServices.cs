using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Builds the same service graph as the server, without the web host, for CLI commands. Notices and logs go to
/// stderr so a command's stdout stays machine-readable (<c>--json</c>, <c>sbox-ns dev</c>).
/// </summary>
public static class CliServices
{
    public static ServiceProvider Build(EffectiveConfig config)
    {
        Directory.CreateDirectory(config.DataDirectory);
        var secrets = ServerSecrets.EnsureAndLoad(config, path => Console.Error.WriteLine($"Generated secret file {path}"));
        SecurityConfigEnvironment.Apply(config, secrets);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NETWORK_STORAGE_AUTH_SESSION_SECRET"] = secrets.AuthSessionSecret,
                ["STORAGE_ENCRYPTION_KEY"] = secrets.StorageEncryptionKeyHex,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        services.AddNetworkStorageServer(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
