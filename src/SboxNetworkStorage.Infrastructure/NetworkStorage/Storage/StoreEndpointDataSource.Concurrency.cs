using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

public sealed partial class StoreEndpointDataSource
{
    public async Task<EndpointRecordSnapshot> ReadRecordSnapshotAsync(string projectId, string collectionId, string key, CancellationToken ct)
        => Snapshot(await ReadRowAsync(_store, projectId, collectionId, key, ct));

    public async Task<IReadOnlyList<EndpointRecordRead>> ScanCollectionSnapshotsAsync(string projectId, string collectionId, CancellationToken ct)
        => await ScanSnapshotsAsync(_store, projectId, collectionId, ct);

    public async Task<IEndpointWriteTransaction> BeginCheckedWriteTransactionAsync(string projectId, EndpointReadSet readSet, CancellationToken ct)
    {
        if (_store is not ISerializableStoreTransactions serializable)
            throw new NotSupportedException("This store cannot validate endpoint read sets atomically.");
        IStoreTransaction? transaction = null;
        try
        {
            transaction = await serializable.BeginSerializableTransactionAsync(ct);
            foreach (var (target, expected) in readSet.Records)
            {
                if (expected.InvalidTarget) continue;
                var current = Snapshot(await ReadRowAsync(transaction.Store, projectId, target.Collection, target.Key, ct));
                if (!SameSnapshot(expected, current)) throw new EndpointWriteConflictException();
            }
            foreach (var (collection, expected) in readSet.Scans)
            {
                var current = await ScanSnapshotsAsync(transaction.Store, projectId, collection, ct);
                if (expected.Count != current.Count) throw new EndpointWriteConflictException();
                var byKey = current.ToDictionary(row => row.Key, StringComparer.Ordinal);
                foreach (var row in expected)
                    if (!byKey.TryGetValue(row.Key, out var actual) || !SameSnapshot(row.Snapshot, actual.Snapshot))
                        throw new EndpointWriteConflictException();
            }
            return new WriteTransaction(this, transaction, readSet, serializable);
        }
        catch (Exception ex)
        {
            if (transaction is not null) await transaction.DisposeAsync();
            if (serializable.IsSerializationConflict(ex)) throw new EndpointWriteConflictException(ex);
            throw;
        }
    }

    private async Task<JsonElement?> ReadRowAsync(INetworkStorageStore store, string projectId, string collectionId, string key, CancellationToken ct)
        => await IsGlobalCollectionAsync(projectId, collectionId, ct)
            ? await store.ReadGlobalRecordAsync(projectId, collectionId, key, ct)
            : await store.ReadRecordAsync(projectId, collectionId, key, ct);

    private async Task<IReadOnlyList<EndpointRecordRead>> ScanSnapshotsAsync(INetworkStorageStore store, string projectId, string collectionId, CancellationToken ct)
    {
        var global = await IsGlobalCollectionAsync(projectId, collectionId, ct);
        var rows = global ? await store.ListGlobalRecordsAsync(projectId, collectionId, ct) : await store.ListRecordsAsync(projectId, collectionId, ct);
        var result = new List<EndpointRecordRead>(rows.Count);
        foreach (var row in rows)
            result.Add(new EndpointRecordRead(row.GetProperty(global ? "record_id" : "record_key").GetString()!, Snapshot(row)));
        return result;
    }

    private static EndpointRecordSnapshot Snapshot(JsonElement? row) => new(row.HasValue ? ExtractRecordPayload(row.Value) : null, row);

    private static bool SameSnapshot(EndpointRecordSnapshot expected, EndpointRecordSnapshot actual) =>
        expected.Row.HasValue == actual.Row.HasValue && (!expected.Row.HasValue || JsonElement.DeepEquals(expected.Row.Value, actual.Row!.Value));

    private async Task WriteCheckedAsync(INetworkStorageStore store, string projectId, string collectionId, string key,
        IReadOnlyDictionary<string, object?>? payload, bool delete, EndpointReadSet readSet, CancellationToken ct)
    {
        if (!readSet.Records.TryGetValue((collectionId, key), out var expected))
        {
            // A scan can supply an existing write target. A computed, unobserved target
            // must not silently become an unconditional write on a protected collection.
            var scanned = readSet.Scans.GetValueOrDefault(collectionId)?.FirstOrDefault(row => row.Key == key);
            if (scanned is null) throw new EndpointWriteConflictException();
            expected = scanned.Snapshot;
        }
        var row = expected.Row;
        var deleted = row is { } value && value.TryGetProperty("deleted", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (delete && (row is null || deleted)) return;
        var global = await IsGlobalCollectionAsync(projectId, collectionId, ct);
        var version = row is { } existing && !deleted ? existing.GetProperty("version").GetInt64() : (long?)null;
        RecordMutationSnapshot? token = null;
        if (version.HasValue && row is { } observed)
        {
            long? changedAt = observed.TryGetProperty(global ? "created_at_unix_ms" : "updated_at_unix_ms", out var time)
                && time.TryGetInt64(out var timestamp) ? timestamp : null;
            token = new RecordMutationSnapshot(observed.GetProperty("payload_json").GetRawText(), changedAt);
        }
        var element = JsonSerializer.SerializeToElement(payload);
        if (!await store.TryMutateRecordAsync(projectId, collectionId, key, global, element, delete, version, ct, token))
            throw new EndpointWriteConflictException();
        readSet.Records[(collectionId, key)] = Snapshot(await ReadRowAsync(store, projectId, collectionId, key, ct));
    }
}
