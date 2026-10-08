using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="PostgresNetworkStorageStore"/> singleton as
    /// <see cref="INetworkStorageStore"/>, <see cref="INetworkStorageStoreAdmin"/>
    /// and itself. Does not connect or migrate; call <see cref="INetworkStorageStoreAdmin.MigrateAsync"/>.
    /// Uses a registered <see cref="TimeProvider"/> when present, otherwise the system clock.
    /// </summary>
    public static IServiceCollection AddPostgresNetworkStorageStore(this IServiceCollection services, PostgresStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(sp => new PostgresNetworkStorageStore(
            options,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<PostgresNetworkStorageStore>>()));
        services.AddSingleton<INetworkStorageStore>(sp => sp.GetRequiredService<PostgresNetworkStorageStore>());
        services.AddSingleton<INetworkStorageStoreAdmin>(sp => sp.GetRequiredService<PostgresNetworkStorageStore>());
        return services;
    }
}
