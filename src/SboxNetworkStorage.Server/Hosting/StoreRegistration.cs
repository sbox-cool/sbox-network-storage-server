using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>Registers the configured database driver and runs startup schema checks.</summary>
public static class StoreRegistration
{
    public static IServiceCollection AddConfiguredStore(this IServiceCollection services, EffectiveConfig config)
    {
        switch (config.GetString("database.provider"))
        {
            case "postgres":
                services.AddPostgresNetworkStorageStore(BuildPostgresOptions(config));
                break;
            default:
                services.AddSqliteNetworkStorageStore(new SqliteStoreOptions
                {
                    DatabasePath = SqlitePath(config)
                });
                break;
        }

        return services;
    }

    public static string SqlitePath(EffectiveConfig config)
        => config.GetPath("database.sqlite.path", config.DataDirectory);

    public static PostgresStoreOptions BuildPostgresOptions(EffectiveConfig config)
    {
        var connectionString = config.GetString("database.postgres.connection_string");
        var password = config.GetString("database.postgres.password");
        var passwordFile = config.GetPath("database.postgres.password_file", config.ConfigDirectory);
        return new PostgresStoreOptions
        {
            ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString,
            Host = config.GetString("database.postgres.host"),
            Port = checked((int)config.GetInteger("database.postgres.port")),
            Database = config.GetString("database.postgres.database"),
            Username = config.GetString("database.postgres.username"),
            Password = string.IsNullOrEmpty(password) ? null : password,
            PasswordFile = string.IsNullOrEmpty(passwordFile) ? null : passwordFile,
            SslMode = config.GetString("database.postgres.ssl_mode"),
            MaxPoolSize = checked((int)config.GetInteger("database.postgres.max_pool_size")),
            Schema = config.GetString("database.postgres.schema"),
            ConnectTimeoutSeconds = checked((int)config.GetInteger("database.postgres.connect_timeout_seconds"))
        };
    }

    /// <summary>
    /// Waits for the database (retrying with backoff up to <c>database.startup_timeout_seconds</c>),
    /// then applies pending migrations. Refuses to continue when the schema is newer than this binary.
    /// </summary>
    public static async Task<SchemaMigrationResult> PrepareDatabaseAsync(
        INetworkStorageStoreAdmin admin,
        EffectiveConfig config,
        ILogger logger,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.GetInteger("database.startup_timeout_seconds"));
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                var ping = await admin.PingAsync(ct);
                logger.LogInformation("Connected to {Provider} at {Target} ({ServerVersion}, {RoundTripMs} ms)",
                    admin.ProviderName, admin.RedactedTarget, ping.ServerVersion, (int)ping.RoundTrip.TotalMilliseconds);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTimeOffset.UtcNow < deadline)
            {
                logger.LogWarning("Database {Target} is not reachable yet ({Error}); retrying in {Delay}s",
                    admin.RedactedTarget, ex.Message, (int)delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }

        var result = await admin.MigrateAsync(ct);
        if (result.AppliedVersions.Count > 0)
        {
            logger.LogInformation("Database schema migrated from version {From} to {To}", result.FromVersion, result.ToVersion);
        }

        return result;
    }
}
