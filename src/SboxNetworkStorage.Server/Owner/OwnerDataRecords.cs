using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Owner-side record reads shared by the dashboard data browser and the coding-agent backend
/// (<c>sbox-ns dev</c>): collection lookup, key validation and the data plane's view of stored rows.
/// </summary>
internal static class OwnerDataRecords
{
    private const int PreviewLength = 160;

    public static async Task<OwnerDataCollection?> CollectionAsync(INetworkStorageStore store, string projectId, string collectionId, CancellationToken ct)
        => StorageIdValidation.IsValidCollectionId(collectionId) && await store.ReadCollectionAsync(projectId, collectionId, ct) is { } row
            ? Describe(row) : null;

    public static bool ValidKey(OwnerDataCollection collection, string key)
        => collection.Global ? StorageIdValidation.IsValidCollectionId(key) : StorageIdValidation.IsValidRecordKey(key);

    /// <summary>Every live record of the collection, sorted by key.</summary>
    public static async Task<List<OwnerDataRecord>> LoadAsync(INetworkStorageStore store, string projectId, OwnerDataCollection collection, CancellationToken ct)
    {
        var rows = collection.Global
            ? await store.ListGlobalRecordsAsync(projectId, collection.Id, ct)
            : await store.ListRecordsAsync(projectId, collection.Id, ct);
        var records = new List<OwnerDataRecord>(rows.Count);
        foreach (var row in rows)
        {
            if (ToRecord(row, collection.Global) is { } record) records.Add(record);
        }
        records.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        return records;
    }

    /// <summary>One live record, or null when the key is invalid, absent or tombstoned.</summary>
    public static async Task<OwnerDataRecord?> ReadAsync(INetworkStorageStore store, string projectId, OwnerDataCollection collection, string key, CancellationToken ct)
    {
        if (!ValidKey(collection, key)) return null;
        var row = collection.Global
            ? await store.ReadGlobalRecordAsync(projectId, collection.Id, key, ct)
            : await store.ReadRecordAsync(projectId, collection.Id, key, ct);
        return row is { } value ? ToRecord(value, collection.Global) : null;
    }

    /// <summary>Maps a stored row; tombstoned or payload-less rows are skipped exactly like the data plane reads them.</summary>
    public static OwnerDataRecord? ToRecord(JsonElement row, bool global)
    {
        if (RecordRow.ExtractPayload(row) is not { } payload) return null;
        if (Text(row, global ? "record_id" : "record_key") is not { } key) return null;
        var raw = payload.GetRawText();
        var preview = raw.Length <= PreviewLength ? raw : raw[..PreviewLength] + "…";
        return new OwnerDataRecord(key, Number(row, "version"), Number(row, global ? "created_at_unix_ms" : "updated_at_unix_ms"),
            Encoding.UTF8.GetByteCount(raw), preview, payload);
    }

    public static OwnerDataCollection? Describe(JsonElement row)
    {
        if (Text(row, "collection_id") is not { } id) return null;
        return new OwnerDataCollection(id, Text(row, "name") is { Length: > 0 } name ? name : id, IsGlobal(row));
    }

    // Same routing rule as the data plane: definition_json.collectionType == "global" (string or parsed column).
    private static bool IsGlobal(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition)) return false;
        if (definition.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(definition.GetString() ?? string.Empty);
                return IsGlobalDefinition(document.RootElement);
            }
            catch (JsonException) { return false; }
        }
        return IsGlobalDefinition(definition);
    }

    private static bool IsGlobalDefinition(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("collectionType", out var type) && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "global", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
