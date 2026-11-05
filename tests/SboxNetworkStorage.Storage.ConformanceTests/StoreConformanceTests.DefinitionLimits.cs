namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract partial class StoreConformanceTests
{
    // ── size limits: records keep 64 KiB, definitions get 4 MiB ─────

    [SkippableFact]
    public async Task Default_limits_keep_records_at_64_KiB_and_definitions_at_4_MiB()
    {
        var s = await StoreAsync();

        Assert.Equal(64 * 1024, s.MaxPayloadBytes);
        Assert.Equal(4 * 1024 * 1024, s.MaxDefinitionBytes);
    }

    [SkippableFact]
    public async Task Hosted_sized_collection_and_endpoint_definitions_are_stored_and_read_back()
    {
        var s = await StoreAsync();
        var collection = Json.Parse("{\"name\":\"Inventory\",\"notes\":\"" + new string('c', 500 * 1024) + "\"}");
        var endpoint = Json.Parse("{\"steps\":\"" + new string('e', 160 * 1024) + "\"}");

        await s.UpsertCollectionAsync("p1", "inventory", "Inventory", "private", collection, 1, Ct);
        await s.UpsertEndpointAsync("p1", "ep-1", "buy-item", "POST", true, endpoint, "hash-ep", 1, Ct);

        var storedCollection = await s.ReadCollectionAsync("p1", "inventory", Ct);
        Assert.Equal(500 * 1024, storedCollection!.Value.GetProperty("definition_json").GetProperty("notes").GetString()!.Length);
        var storedEndpoint = await s.ReadEndpointAsync("p1", "ep-1", Ct);
        Assert.Equal(160 * 1024, storedEndpoint!.Value.GetProperty("definition_json").GetProperty("steps").GetString()!.Length);
    }

    [SkippableFact]
    public async Task Record_over_64_KiB_is_refused_by_default_and_nothing_is_stored()
    {
        var s = await StoreAsync();
        var record = Json.Parse("\"" + new string('r', 70 * 1024) + "\"");

        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertRecordAsync("p1", "c", "k", record, false, 1, Ct));
        Assert.Null(await s.ReadRecordAsync("p1", "c", "k", Ct));
    }

    [SkippableFact]
    public async Task Definition_over_4_MiB_is_refused_and_nothing_is_stored()
    {
        var s = await StoreAsync();
        var collection = Json.Parse("{\"notes\":\"" + new string('c', 4 * 1024 * 1024) + "\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => s.UpsertCollectionAsync("p1", "big", "Big", "private", collection, 1, Ct));
        Assert.Null(await s.ReadCollectionAsync("p1", "big", Ct));
    }
}
