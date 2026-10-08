using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

/// <summary>
/// Unified parsing of a ScyllaDB <c>records</c> row (<c>{record_key, payload_json,
/// deleted, version, updated_at_unix_ms}</c>) or <c>global_records</c> row
/// (<c>{record_id, payload_json, version, created_at_unix_ms}</c>).
///
/// Every read path — the game-client endpoint executor
/// (<see cref="ScyllaEndpointShadowDataSource"/>), the direct record-CRUD API
/// (<see cref="ScyllaNetworkStorageDataPlane"/>), and the website dashboard
/// browse API (<c>NetworkStorageController.BrowseCollectionDataApi</c>) — MUST
/// go through this helper so they never serve divergent views of the same
/// record. Before this class existed, each path had its own inline
/// <c>payload_json</c> extraction, and a change to one (e.g. the string-vs-object
/// deserialization branch) could silently diverge from the others — exactly the
/// class of bug that stranded players in the 2026-06-19 incident (the dashboard
/// showed a different <c>totalLevel</c> than the game client loaded).
/// </summary>
public static class RecordRow
{
    /// <summary>
    /// Extract the stored payload from a <c>records</c> or <c>global_records</c>
    /// row, returning <c>null</c> for a soft-deleted tombstone or an absent
    /// payload. The returned <see cref="JsonElement"/> is the stored payload
    /// object itself — NOT the row wrapper — so callers operate on the same
    /// data regardless of whether they read via the executor, the CRUD API, or
    /// the browse API.
    /// </summary>
    /// <remarks>
    /// <see cref="ScyllaDbResourceStore.BuildRecordRow"/> already parses
    /// <c>payload_json</c> via <see cref="ScyllaDbResourceStore.ParseJsonOrNull"/>
    /// into a <see cref="JsonElement"/>, so the <c>ValueKind</c> is never
    /// <c>String</c> at this point. The legacy string-deserialization branch is
    /// kept defensively but is dead code for rows from the real store.
    /// </remarks>
    public static JsonElement? ExtractPayload(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;

        // Soft-deleted tombstones read as missing — matching Bun's storage layer.
        if (row.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True)
            return null;

        if (!row.TryGetProperty("payload_json", out var payload))
            return null;

        return payload.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // BuildRecordRow already parses payload_json into a JsonElement,
            // so this is the normal path. Return the element directly.
            JsonValueKind.Object or JsonValueKind.Array
                or JsonValueKind.True or JsonValueKind.False
                or JsonValueKind.Number => payload,
            // Legacy defensive branch: if payload_json was stored as a raw JSON
            // string (e.g. by a migration tool that bypassed BuildRecordRow),
            // deserialize it so the caller gets the object, not a quoted string.
            JsonValueKind.String => TryDeserialize(payload.GetString()!),
            _ => null,
        };
    }

    /// <summary>
    /// Extract the <c>record_key</c> (per-player records) or <c>record_id</c>
    /// (global records) from a row. Returns <c>null</c> if neither is present.
    /// </summary>
    public static string? ExtractKey(JsonElement row)
    {
        if (row.TryGetProperty("record_key", out var rk) && rk.ValueKind == JsonValueKind.String)
            return rk.GetString();
        if (row.TryGetProperty("record_id", out var ri) && ri.ValueKind == JsonValueKind.String)
            return ri.GetString();
        return null;
    }

    /// <summary>
    /// Extract the <c>updated_at_unix_ms</c> (per-player records) or
    /// <c>created_at_unix_ms</c> (global records) from a row. Returns 0 if
    /// neither is present or not a number.
    /// </summary>
    public static long ExtractUpdatedAt(JsonElement row)
    {
        if (row.TryGetProperty("updated_at_unix_ms", out var ts) && ts.ValueKind == JsonValueKind.Number)
            return ts.GetInt64();
        if (row.TryGetProperty("created_at_unix_ms", out var ct) && ct.ValueKind == JsonValueKind.Number)
            return ct.GetInt64();
        return 0;
    }

    /// <summary>
    /// Extract the stored payload as the endpoint/query executor's value model
    /// (a <see cref="Dictionary{TKey,TValue}"/> for objects, <see cref="List{T}"/>
    /// for arrays, or a boxed scalar) via <c>EndpointExpression.FromJson</c>.
    /// Returns <c>null</c> for a tombstone or absent payload — the SAME null
    /// semantics as <see cref="ExtractPayload"/>. The endpoint executor
    /// (<see cref="ScyllaEndpointShadowDataSource"/>) and the query executor
    /// (<see cref="NativeQueryExecutor"/>) MUST both use this so a record reads
    /// identically whether it is loaded by the game client, projected by a query,
    /// browsed on the website, or fetched via the record API.
    /// </summary>
    public static object? ExtractValueModel(JsonElement row)
    {
        var payload = ExtractPayload(row);
        return payload is null
            ? null
            : SboxNetworkStorage.Application.NetworkStorage.Endpoints.EndpointExpression.FromJson(payload.Value);
    }

    private static JsonElement? TryDeserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Clone because the underlying buffer is disposed with doc.
            return JsonSerializer.SerializeToElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }
}
