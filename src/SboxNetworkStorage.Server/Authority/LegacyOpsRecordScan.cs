using System.Text.Json;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Authority;

/// <summary>
/// Finds records damaged by sbox-ns 0.4.0, whose <c>update-ops</c> stored the request body itself
/// (<c>{"ops":[...]}</c>) as the whole document instead of applying the operations. Such a record has
/// exactly one field, <c>ops</c>, holding operation objects. The data it replaced is gone; restore it
/// from a backup or have the player save again. Read-only and on demand: scans every live record.
/// </summary>
public static class LegacyOpsRecordScan
{
    private const int PageSize = 500;
    private const int SampleKeys = 5;

    public sealed record CollectionResult(string Collection, long Records, IReadOnlyList<string> SampleKeys);

    public static async Task<IReadOnlyList<CollectionResult>> RunAsync(INetworkStorageStore store, string projectId, CancellationToken ct)
    {
        var results = new List<CollectionResult>();
        foreach (var row in await store.ListCollectionsAsync(projectId, ct))
        {
            if (OwnerDataRecords.Describe(row) is not { } collection) continue;
            long damaged = 0;
            var samples = new List<string>();
            var total = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct);
            for (var offset = 0; offset < total; offset += PageSize)
            {
                var page = await OwnerDataRecords.PageAsync(store, projectId, collection, null, offset, PageSize, ct);
                foreach (var record in page.Where(r => IsOpsOnly(r.Payload)))
                {
                    damaged++;
                    if (samples.Count < SampleKeys) samples.Add(record.Key);
                }
            }
            if (damaged > 0) results.Add(new CollectionResult(collection.Id, damaged, samples));
        }
        return results;
    }

    /// <summary>True for <c>{"ops":[{"op":...}, ...]}</c> and nothing else.</summary>
    public static bool IsOpsOnly(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        var fields = payload.EnumerateObject().ToList();
        if (fields.Count != 1 || fields[0].Name != "ops" || fields[0].Value.ValueKind != JsonValueKind.Array) return false;
        var ops = fields[0].Value.EnumerateArray().ToList();
        return ops.Count > 0 && ops.All(op => op.ValueKind == JsonValueKind.Object && op.TryGetProperty("op", out var name) && name.ValueKind == JsonValueKind.String);
    }
}
