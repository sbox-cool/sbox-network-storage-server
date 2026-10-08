using System.Text.Json.Nodes;
using Npgsql;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>Deterministic clock; starts at a whole millisecond.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(long startUnixMs) => _now = DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs);

    public override DateTimeOffset GetUtcNow() => _now;

    public long UnixMs => _now.ToUnixTimeMilliseconds();

    public void Advance(long milliseconds) => _now = _now.AddMilliseconds(milliseconds);
}

public static class Json
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts structural equality (property order ignored, number/string/bool/null kinds significant).</summary>
    public static void Equal(string expectedJson, JsonElement? actual)
    {
        Assert.True(actual.HasValue, $"Expected {expectedJson} but got no row.");
        var expected = JsonNode.Parse(expectedJson);
        var actualNode = JsonNode.Parse(actual!.Value.GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, actualNode), $"Expected {expected?.ToJsonString()}\n  Actual {actualNode?.ToJsonString()}");
    }

    public static string Str(JsonElement row, string name) => row.GetProperty(name).GetString()!;

    public static long Long(JsonElement row, string name) => row.GetProperty(name).GetInt64();
}

/// <summary>Creates throwaway databases for the relational drivers.</summary>
public static class TestDatabases
{
    public const string PostgresEnvVar = "NS_TEST_POSTGRES";

    public const string PostgresSkipReason = "PostgreSQL conformance skipped: set NS_TEST_POSTGRES to a connection string (e.g. Host=localhost;Port=55432;Username=postgres;Database=postgres).";

    public static string? PostgresConnectionString => Environment.GetEnvironmentVariable(PostgresEnvVar) is { Length: > 0 } cs ? cs : null;

    public static string NewSqlitePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sbox-ns-conformance");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    public static void DeleteSqlite(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public static SqliteNetworkStorageStore CreateSqlite(string path, TimeProvider? time)
        => new(new SqliteStoreOptions { DatabasePath = path }, time);

    public static string NewPostgresSchema() => $"nst_{Guid.NewGuid():N}";

    public static PostgresNetworkStorageStore CreatePostgres(string schema, TimeProvider? time)
    {
        var connectionString = PostgresConnectionString ?? throw new InvalidOperationException(PostgresSkipReason);
        return new PostgresNetworkStorageStore(new PostgresStoreOptions { ConnectionString = connectionString, Schema = schema }, time);
    }

    public static async Task ExecutePostgresAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static Task DropPostgresSchemaAsync(string schema) => ExecutePostgresAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
}
