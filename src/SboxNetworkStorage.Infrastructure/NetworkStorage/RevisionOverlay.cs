using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// The staged revision as one request targeting <c>next</c> sees it: staged endpoints (by slug) and
/// collections (by name) laid over the live definitions exactly as package-sync promotion would merge
/// them. Built per request from <c>revision-overrides.json</c> and never cached, so the shared live
/// metadata snapshot is never mixed with staged definitions and staged pushes are visible at once.
/// </summary>
public sealed class RevisionOverlay
{
    private readonly Dictionary<string, JsonObject> _endpointsBySlug;
    private readonly Dictionary<string, JsonObject> _collectionsByName;

    private RevisionOverlay(Dictionary<string, JsonObject> endpointsBySlug, Dictionary<string, JsonObject> collectionsByName)
    {
        _endpointsBySlug = endpointsBySlug;
        _collectionsByName = collectionsByName;
    }

    /// <summary>Loads the project's staged revision, or null when nothing is staged.</summary>
    public static async Task<RevisionOverlay?> LoadAsync(
        IWorkspaceStore client, long ownerUserId, string projectId, CancellationToken ct)
    {
        var overrides = await RevisionOverrides.ReadAsync(client, ownerUserId, projectId, ct);
        var endpoints = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, item) in RevisionOverrides.Items(overrides, RevisionOverrides.EndpointsSection))
            endpoints[ReadString(item, "slug") ?? key] = item;
        var collections = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, item) in RevisionOverrides.Items(overrides, RevisionOverrides.CollectionsSection))
            collections[ReadString(item, "name") ?? key] = item;
        return endpoints.Count == 0 && collections.Count == 0 ? null : new RevisionOverlay(endpoints, collections);
    }

    public bool StagesEndpoint(string slug) => _endpointsBySlug.ContainsKey(slug);

    public IReadOnlyList<JsonElement> MergeEndpointRows(IReadOnlyList<JsonElement> liveRows)
    {
        var rows = new List<JsonElement>(liveRows.Count + _endpointsBySlug.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var live in liveRows)
        {
            var slug = live.GetProperty("slug").GetString()!;
            seen.Add(slug);
            rows.Add(_endpointsBySlug.TryGetValue(slug, out var staged) ? EndpointRow(live, slug, staged) : live);
        }
        foreach (var (slug, staged) in _endpointsBySlug)
            if (!seen.Contains(slug)) rows.Add(EndpointRow(null, slug, staged));
        return rows;
    }

    private static JsonElement EndpointRow(JsonElement? live, string slug, JsonObject staged)
    {
        var row = live is { } current ? JsonNode.Parse(current.GetRawText())!.AsObject() : new JsonObject();
        var definition = RevisionOverrides.MergeOverLive(live is { } existing ? DefinitionColumn(existing) : null, staged);
        row["endpoint_id"] ??= ReadString(definition, "id") ?? slug;
        row["slug"] = slug;
        row["method"] = ReadString(definition, "method") ?? "POST";
        row["definition_json"] = definition;
        return JsonSerializer.SerializeToElement(row);
    }

    /// <summary>The staged endpoint merged over its live definition (null when there is none).</summary>
    public Dictionary<string, object?>? EndpointDefinition(string slug, JsonElement? liveDefinition)
        => _endpointsBySlug.TryGetValue(slug, out var staged)
            ? EndpointExpression.FromJson(JsonSerializer.SerializeToElement(RevisionOverrides.MergeOverLive(liveDefinition, staged)))
                as Dictionary<string, object?>
            : null;

    /// <summary>
    /// The live collection rows with staged collections merged in: a staged collection replaces the live
    /// row of the same name (keeping its <c>collection_id</c>), and new staged collections are appended.
    /// </summary>
    public IReadOnlyList<JsonElement> MergeCollectionRows(IReadOnlyList<JsonElement> liveRows)
    {
        if (_collectionsByName.Count == 0) return liveRows;
        var rows = new List<JsonElement>(liveRows.Count + _collectionsByName.Count);
        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in liveRows)
        {
            var name = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            if (name is not null && _collectionsByName.TryGetValue(name, out var staged))
            {
                rows.Add(CollectionRow(row, name, staged));
                merged.Add(name);
            }
            else
            {
                rows.Add(row);
            }
        }
        foreach (var (name, staged) in _collectionsByName)
            if (!merged.Contains(name)) rows.Add(CollectionRow(null, name, staged));
        return rows;
    }

    private static JsonElement CollectionRow(JsonElement? liveRow, string name, JsonObject staged)
    {
        var row = liveRow is { } live ? JsonNode.Parse(live.GetRawText())!.AsObject() : new JsonObject();
        var definition = RevisionOverrides.MergeOverLive(liveRow is { } current ? DefinitionColumn(current) : null, staged);
        if (ReadString(row, "collection_id") is null) row["collection_id"] = ReadString(definition, "id") ?? name;
        row["name"] = name;
        row["visibility"] = ReadString(staged, "visibility") ?? ReadString(row, "visibility") ?? "private";
        row["definition_json"] = definition;
        return JsonSerializer.SerializeToElement(row);
    }

    /// <summary>The <c>definition_json</c> column of a store row, which may be stored as text or as an object.</summary>
    public static JsonElement? DefinitionColumn(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("definition_json", out var definition)) return null;
        if (definition.ValueKind == JsonValueKind.Object) return definition;
        if (definition.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(definition.GetString())) return null;
        using var document = JsonDocument.Parse(definition.GetString()!);
        return document.RootElement.Clone();
    }

    private static string? ReadString(JsonObject item, string name)
        => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
