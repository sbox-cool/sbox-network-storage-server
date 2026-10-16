using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.EndpointSpike.Stores;

/// <summary>An opened, migrated store and the admin surface used to migrate it.</summary>
public sealed record OpenedStore(INetworkStorageStore Store, INetworkStorageStoreAdmin Admin, string Provider, string Target);

/// <summary>
/// Opens the real production store drivers. Secrets come from the environment only (<c>SBOX_NS_PG_USER</c>,
/// <c>SBOX_NS_PG_PASSWORD</c>), never from argv, so they cannot leak into process listings or logs.
/// </summary>
public static class StoreFactory
{
    /// <summary>Dedicated schema so the spike never touches a production schema.</summary>
    public const string SpikeSchema = "ns_endpoint_spike";

    public static async Task<OpenedStore> OpenSqliteAsync(string databasePath, int maxPayloadBytes, CancellationToken ct)
    {
        var store = new SqliteNetworkStorageStore(new SqliteStoreOptions
        {
            DatabasePath = databasePath,
            MaxPayloadBytes = maxPayloadBytes,
        });
        await store.MigrateAsync(ct);
        return new OpenedStore(store, store, "sqlite", databasePath);
    }

    /// <summary>Opens PostgreSQL from <c>host:port/database</c>. The cluster is parent-managed and never started here.</summary>
    public static async Task<OpenedStore> OpenPostgresAsync(string target, int maxPayloadBytes, CancellationToken ct)
    {
        var slash = target.IndexOf('/');
        var hostPort = slash < 0 ? target : target[..slash];
        var database = slash < 0 ? string.Empty : target[(slash + 1)..];
        var colon = hostPort.LastIndexOf(':');
        var host = colon < 0 ? hostPort : hostPort[..colon];
        var port = colon < 0 ? 5432 : int.Parse(hostPort[(colon + 1)..]);
        if (database.Length == 0)
        {
            throw new ArgumentException("PostgreSQL target must be host[:port]/database.");
        }

        var options = new PostgresStoreOptions
        {
            Host = host,
            Port = port,
            Database = database,
            Username = Environment.GetEnvironmentVariable("SBOX_NS_PG_USER") ?? throw new InvalidOperationException("SBOX_NS_PG_USER is required for PostgreSQL."),
            Password = Environment.GetEnvironmentVariable("SBOX_NS_PG_PASSWORD"),
            SslMode = Environment.GetEnvironmentVariable("SBOX_NS_PG_SSLMODE") ?? "Prefer",
            Schema = SpikeSchema,
            MaxPayloadBytes = maxPayloadBytes,
        };
        var store = new PostgresNetworkStorageStore(options);
        await store.MigrateAsync(ct);
        return new OpenedStore(store, store, "postgres", options.RedactedTarget);
    }
}
