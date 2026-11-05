namespace SboxNetworkStorage.Storage.Sqlite;

/// <summary>Settings for <see cref="SqliteNetworkStorageStore"/>.</summary>
public sealed record SqliteStoreOptions
{
    /// <summary>Path of the single database file. Relative paths resolve against the working directory; parent directories are created.</summary>
    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>How long a statement waits for a competing writer's lock before failing (SQLite <c>busy_timeout</c>).</summary>
    public int BusyTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Maximum accepted record payload size in UTF-8 bytes (default: 64 KiB).</summary>
    public int MaxPayloadBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum accepted definition size in UTF-8 bytes for collections, endpoints, workflows, queries, game values, rate-limit rules, projects and API keys (default: 4 MiB).</summary>
    public int MaxDefinitionBytes { get; init; } = 4 * 1024 * 1024;
}
