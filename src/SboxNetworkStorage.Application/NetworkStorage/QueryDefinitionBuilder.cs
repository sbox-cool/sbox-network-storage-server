using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>Form-derived inputs for a single query source row.</summary>
public sealed record QuerySourceInput(string CollectionId, string? Alias);
/// <summary>Form-derived inputs for a single foreign-key join row.</summary>
public sealed record QueryJoinInput(string CollectionId, string Alias, string Type, string LocalKey, string ForeignKey);
/// <summary>
/// Normalized inputs parsed from the query new/settings form. Kept HTTP-free so
/// <see cref="QueryDefinitionBuilder.Build"/> is unit-testable.
/// </summary>
public sealed record QueryFormInput(
    string Id,
    string Name,
    string Type,
    string? Field,
    string Order,
    int Limit,
    int CacheTtlSeconds,
    bool RequiresSecretKey,
    IReadOnlyList<QuerySourceInput> Sources,
    IReadOnlyList<string> Fields,
    IReadOnlyList<QueryJoinInput> Joins);

/// <summary>
/// Builds the canonical query JSON object that BOTH query engines read:
/// the live legacy server engine (<c>tools/sbox/queries.js</c>) and the .NET
/// <c>NativeQueryExecutor</c>. The metric field/order/limit live under
/// <c>config</c>, the cache TTL under <c>cache.ttlSeconds</c>, and
/// <c>requiresSecretKey</c>/<c>sources</c> at the top level. Existing config
/// (<c>joins</c>, <c>computedFields</c>, output <c>fields</c>, …) is preserved.
/// </summary>
public static class QueryDefinitionBuilder
{
    public static Dictionary<string, object> Build(QueryFormInput input, Dictionary<string, object>? existing = null)
    {
        var query = existing is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(existing, StringComparer.Ordinal);

        query["id"] = input.Id;
        query["name"] = input.Name;
        query["type"] = input.Type;
        query["requiresSecretKey"] = input.RequiresSecretKey;

        // config: merge onto any existing config so joins/computedFields/output
        // fields and other advanced keys survive a settings save.
        var config = CloneChildObject(query, "config");
        if (string.IsNullOrEmpty(input.Field)) config.Remove("field");
        else config["field"] = input.Field;
        config["order"] = input.Order;
        config["limit"] = input.Limit;
        if (input.Fields.Count > 0) config["fields"] = input.Fields;
        else config.Remove("fields");
        if (input.Joins.Count > 0)
        {
            var joins = new List<object>();
            foreach (var join in input.Joins)
            {
                if (string.IsNullOrWhiteSpace(join.CollectionId)) continue;
                var j = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sourceCollectionId"] = join.CollectionId,
                    ["alias"] = join.Alias,
                    ["type"] = join.Type,
                    ["localKey"] = join.LocalKey,
                    ["foreignKey"] = join.ForeignKey
                };
                joins.Add(j);
            }
            config["joins"] = joins;
        }
        // else: preserve existing config.joins (same pattern as computedFields)
        // Player-profile enrichment is auto-detected by the executor from
        // playerProfile.* entries in config.fields — no separate config block
        // needed. If a stale config.enrichment exists from a previous save,
        // leave it; the executor reads both paths for backward compat.

        query["config"] = config;

        // cache.ttlSeconds is where both the engine and the query-output layer read TTL.
        var cache = CloneChildObject(query, "cache");
        cache["ttlSeconds"] = input.CacheTtlSeconds;
        query["cache"] = cache;

        var sources = new List<object>();
        foreach (var source in input.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.CollectionId)) continue;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["collectionId"] = source.CollectionId
            };
            if (!string.IsNullOrWhiteSpace(source.Alias)) row["alias"] = source.Alias!;
            sources.Add(row);
        }
        query["sources"] = sources;

        // Drop the mis-placed top-level keys a previous (buggy) .NET save may have
        // written; the engines ignore them, so they are dead weight / a desync trap.
        query.Remove("field");
        query.Remove("order");
        query.Remove("limit");
        query.Remove("cacheTtlSeconds");

        var now = DateTimeOffset.UtcNow.ToString("O");
        query["updatedAt"] = now;
        if (!query.ContainsKey("createdAt")) query["createdAt"] = now;
        return query;
    }

    private static Dictionary<string, object> CloneChildObject(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var value))
        {
            if (value is Dictionary<string, object> dictionary)
                return new Dictionary<string, object>(dictionary, StringComparer.Ordinal);

            if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    copy[property.Name] = property.Value;
                return copy;
            }
        }
        return new Dictionary<string, object>(StringComparer.Ordinal);
    }
}
