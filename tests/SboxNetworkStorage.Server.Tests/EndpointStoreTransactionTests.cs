using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests;

public sealed class EndpointStoreTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_endpoint_flush_commits_all_three_records_or_rolls_back_the_first_two(bool failThirdWrite)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sbox-endpoint-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.db");
        try
        {
            await using var store = new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = path });
            await store.MigrateAsync(default);
            await store.UpsertCollectionAsync("p", "c", "Players", "public", JsonSerializer.SerializeToElement(new { collectionType = "per-steamid" }), 1, default);
            await store.UpsertEndpointAsync("p", "save", "save", "POST", true, JsonSerializer.SerializeToElement(new
            {
                steps = new[] { WriteStep("one", "a"), WriteStep("two", "b"), WriteStep("three", "c") },
                response = new { status = 200, body = new { ok = true } },
            }), null, 1, default);
            if (failThirdWrite)
            {
                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_third BEFORE INSERT ON records WHEN NEW.record_key = 'c' BEGIN SELECT RAISE(ABORT, 'third write failed'); END";
                await command.ExecuteNonQueryAsync();
            }
            var source = new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance);
            var executor = new EndpointExecutor(source);
            var result = await executor.TryExecuteAsync("p", "save", new Dictionary<string, object?>(), "76561198363609085", "owner",
                new Dictionary<string, object?>(), true, false, default, liveServe: true);
            Assert.NotNull(result);
            Assert.Equal(failThirdWrite ? 500 : 200, result!.Status);
            Assert.Equal(!failThirdWrite, result.Ok);
            if (failThirdWrite)
            {
                Assert.Empty(await store.ListRecordsAsync("p", "c", default));
                var body = JsonSerializer.SerializeToElement(result.Body);
                Assert.Equal("STORAGE_WRITE_FAILED", body.GetProperty("error").GetProperty("code").GetString());
            }
            else
            {
                var rows = await store.ListRecordsAsync("p", "c", default);
                Assert.Equal(new[] { "a", "b", "c" }, rows.Select(row => row.GetProperty("record_key").GetString()));
                Assert.All(rows, row => Assert.Equal(1, row.GetProperty("payload_json").GetProperty("hp").GetInt32()));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static object WriteStep(string id, string key) => new
    {
        id, type = "write", collection = "Players", key,
        ops = new[] { new { op = "set", path = "hp", value = 1 } },
    };
}
