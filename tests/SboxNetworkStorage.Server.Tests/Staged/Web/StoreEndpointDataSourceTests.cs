using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Regression tests for <see cref="ScyllaEndpointShadowDataSource"/>.
///
/// The real ScyllaDB <c>records</c> row is a wrapper
/// (<c>{record_key, payload_json:{…}, deleted, version}</c>) where
/// <c>payload_json</c> is a nested object. A read/scan step must receive the inner
/// payload, never the wrapper — otherwise every <c>{{step.field}}</c> reference
/// resolves to undefined.
///
/// The split-brain regression tests below pin the fix for the cerbralone/sherwood
/// incident: the website collections browser reads ScyllaDB directly
/// (<c>NetworkStorageController.BrowseCollectionDataApi</c>) while the game-client
/// load-player endpoint reads via <c>ScyllaEndpointShadowDataSource</c>. Before the
/// fix, the shadow data source ALWAYS fell back to Bunny CDN on a ScyllaDB miss,
/// even when <c>Scylla:Primary=true</c> (production post-cutover). That meant a
/// stale <c>saved.json</c> in Bunny resurrected an old save (totalLevel 391)
/// while the dashboard showed the current ScyllaDB record (399).
/// </summary>
public sealed class ScyllaEndpointShadowDataSourceTests
{
    private static JsonElement Row(string payloadJson, bool deleted)
        => JsonSerializer.SerializeToElement(new
        {
            record_key = "k1",
            payload_json = JsonDocument.Parse(payloadJson).RootElement,
            deleted,
            version = 1L,
            updated_at_unix_ms = 1000L,
        });

    [Fact]
    public void ExtractRecordPayload_ReturnsInnerPayload_NotWrapper()
    {
        var row = Row("""{"totalLevel":399,"name":"cerbralone"}""", deleted: false);

        var payload = StoreEndpointDataSource.ExtractRecordPayload(row);

        var data = Assert.IsType<Dictionary<string, object?>>(payload);
        Assert.Equal(399d, data["totalLevel"]);
        Assert.Equal("cerbralone", data["name"]);
        Assert.False(data.ContainsKey("payload_json"), "Must unwrap — wrapper keys must not leak to the executor");
        Assert.False(data.ContainsKey("record_key"));
        Assert.False(data.ContainsKey("deleted"));
    }

    [Fact]
    public void ExtractRecordPayload_SoftDeletedTombstone_ReturnsNull()
    {
        var row = Row("""{"totalLevel":399}""", deleted: true);
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(row));
    }

    [Fact]
    public void ExtractRecordPayload_MissingPayload_ReturnsNull()
    {
        var row = JsonSerializer.SerializeToElement(new { record_key = "k1", deleted = false, version = 1L });
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(row));
    }

    [Fact]
    public void ExtractRecordPayload_NullPayload_ReturnsNull()
    {
        var row = JsonSerializer.SerializeToElement(new { record_key = "k1", payload_json = (object?)null, deleted = false });
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(row));
    }

    [Fact]
    public void ExtractRecordPayload_NonObjectRow_ReturnsNull()
    {
        Assert.Null(StoreEndpointDataSource.ExtractRecordPayload(JsonDocument.Parse("\"oops\"").RootElement));
    }

    // ── Split-brain regression: the store is the only record source ──
    // PORT-ADAPTED: the self-hosted data source has no Bunny fallback (removed with the
    // object bucket), so the Bunny fake/workspace repository arguments are gone and the
    // Primary=false fallback test is not ported (see PORTING.md).


    private static StoreEndpointDataSource CreateDataSource(INetworkStorageStore store)
        => new(store, NullLogger<StoreEndpointDataSource>.Instance);

    /// <summary>
    /// When Scylla:Primary=true (production post-cutover), the store value is served.
    /// Reproduces the cerbralone incident: dashboard showed 399, game loaded 391.
    /// </summary>
    [Fact]
    public async Task ReadRecordAsync_PrimaryTrue_ScyllaHit_ReturnsScyllaValue_NotBunny()
    {
        var store = new InMemoryNetworkStorageStore();
        // The store has the CURRENT save: totalLevel = 399 (payload_json is a nested object, as production returns it).
        await store.UpsertRecordAsync("proj", "players", "steam1",
            JsonSerializer.SerializeToElement(new { totalLevel = 399 }), deleted: false, version: 1, CancellationToken.None);

        var ds = CreateDataSource(store);

        var result = await ds.ReadRecordAsync("proj", "players", "steam1", CancellationToken.None);
        Assert.NotNull(result);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(399d, dict["totalLevel"]);
    }

    /// <summary>
    /// When Scylla:Primary=true and the record is ABSENT from the store, the read must return null.
    /// </summary>
    [Fact]
    public async Task ReadRecordAsync_PrimaryTrue_ScyllaAbsent_DoesNotResurrectBunnyStaleSave()
    {
        var store = new InMemoryNetworkStorageStore();

        var ds = CreateDataSource(store);

        var result = await ds.ReadRecordAsync("proj", "players", "steam1", CancellationToken.None);
        Assert.Null(result);
    }
}
