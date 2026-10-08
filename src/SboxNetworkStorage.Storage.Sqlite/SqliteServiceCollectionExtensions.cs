using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="SqliteNetworkStorageStore"/> singleton as
    /// <see cref="INetworkStorageStore"/>, <see cref="INetworkStorageStoreAdmin"/>
    /// and itself. Does not migrate; call <see cref="INetworkStorageStoreAdmin.MigrateAsync"/>.
    /// Uses a registered <see cref="TimeProvider"/> when present, otherwise the system clock.
    /// </summary>
    public static IServiceCollection AddSqliteNetworkStorageStore(this IServiceCollection services, SqliteStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(sp => new SqliteNetworkStorageStore(
            options,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<SqliteNetworkStorageStore>>()));
        services.AddSingleton<INetworkStorageStore>(sp => sp.GetRequiredService<SqliteNetworkStorageStore>());
        services.AddSingleton<INetworkStorageStoreAdmin>(sp => sp.GetRequiredService<SqliteNetworkStorageStore>());
        return services;
    }
}
