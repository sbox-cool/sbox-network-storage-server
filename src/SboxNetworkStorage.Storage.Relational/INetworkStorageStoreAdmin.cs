namespace SboxNetworkStorage.Storage.Relational;

/// <summary>
/// Operational surface of a database-backed store, used by <c>sbox-ns db …</c>,
/// <c>doctor</c>, and server startup. Never exposes credentials.
/// </summary>
public interface INetworkStorageStoreAdmin
{
    /// <summary>Driver name: <c>sqlite</c> or <c>postgres</c>.</summary>
    string ProviderName { get; }

    /// <summary>Credential-free description of the target (file path, or <c>postgres://user@host:port/db?schema=x</c>).</summary>
    string RedactedTarget { get; }

    /// <summary>Highest schema version this binary knows how to migrate to.</summary>
    int SupportedSchemaVersion { get; }

    /// <summary>Opens a connection and runs a trivial query. Throws the driver exception on failure.</summary>
    Task<StorePingResult> PingAsync(CancellationToken ct);

    /// <summary>Current schema version recorded in the database; 0 when the database has never been migrated.</summary>
    Task<int> GetSchemaVersionAsync(CancellationToken ct);

    /// <summary>
    /// Applies every pending forward-only migration inside one transaction.
    /// Throws <see cref="SchemaVersionTooNewException"/> when the database is newer than this binary.
    /// </summary>
    Task<SchemaMigrationResult> MigrateAsync(CancellationToken ct);

    /// <summary>
    /// Verifies the database schema matches this binary exactly. Throws
    /// <see cref="SchemaVersionTooNewException"/> or <see cref="SchemaMigrationRequiredException"/>.
    /// </summary>
    Task EnsureSchemaCompatibleAsync(CancellationToken ct);
}

/// <summary>Outcome of <see cref="INetworkStorageStoreAdmin.PingAsync"/>.</summary>
public sealed record StorePingResult(TimeSpan RoundTrip, string ServerVersion);

/// <summary>Outcome of <see cref="INetworkStorageStoreAdmin.MigrateAsync"/>.</summary>
public sealed record SchemaMigrationResult(int FromVersion, int ToVersion, IReadOnlyList<int> AppliedVersions);

/// <summary>One forward-only migration. <see cref="BuildSql"/> receives the table prefix (e.g. <c>"network_storage".</c>).</summary>
public sealed record SchemaMigration(int Version, string Description, Func<string, string> BuildSql);

/// <summary>The database schema was written by a newer sbox-ns than the running binary.</summary>
public sealed class SchemaVersionTooNewException : InvalidOperationException
{
    public SchemaVersionTooNewException(int databaseVersion, int supportedVersion)
        : base($"The database schema is at version {databaseVersion}, but this sbox-ns build supports schema version {supportedVersion} at most. "
            + "Upgrade sbox-ns to a release that supports this schema (schema downgrades are not supported; restore a backup taken before the upgrade to roll back).")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }
    public int SupportedVersion { get; }
}

/// <summary>The database schema is older than the running binary requires.</summary>
public sealed class SchemaMigrationRequiredException : InvalidOperationException
{
    public SchemaMigrationRequiredException(int databaseVersion, int supportedVersion)
        : base($"The database schema is at version {databaseVersion}, but this sbox-ns build requires schema version {supportedVersion}. Run `sbox-ns db migrate`.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }
    public int SupportedVersion { get; }
}
