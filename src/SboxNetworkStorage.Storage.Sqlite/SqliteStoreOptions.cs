namespace SboxNetworkStorage.Storage.Sqlite;

/// <summary>Settings for <see cref="SqliteNetworkStorageStore"/>.</summary>
public sealed record SqliteStoreOptions
{
    /// <summary>Path of the single database file. Relative paths resolve against the working directory; parent directories are created.</summary>
    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>How long a statement waits for a competing writer's lock before failing (SQLite <c>busy_timeout</c>).</summary>
    public int BusyTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Maximum accepted JSON payload size in UTF-8 bytes (production default: 64 KiB).</summary>
    public int MaxPayloadBytes { get; init; } = 64 * 1024;
}
