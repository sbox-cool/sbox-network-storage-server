using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Builds the same service graph as the server, without the web host, for CLI commands.</summary>
public static class CliServices
{
    public static ServiceProvider Build(EffectiveConfig config)
    {
        Directory.CreateDirectory(config.DataDirectory);
        var secrets = ServerSecrets.EnsureAndLoad(config, path => Console.WriteLine($"Generated secret file {path}"));
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
        services.AddNetworkStorageServer(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
