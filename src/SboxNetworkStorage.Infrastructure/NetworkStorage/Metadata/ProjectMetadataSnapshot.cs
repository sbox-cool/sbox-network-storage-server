using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

/// <summary>
/// Everything the data plane reads about one project's configuration on every request: its collections,
/// endpoint definitions (already parsed to JSON) and game values. Immutable; replaced as a whole on change.
/// </summary>
public sealed class ProjectMetadataSnapshot
{
    private readonly Dictionary<string, JsonElement> _collectionsById;
    private readonly Dictionary<string, JsonElement> _endpointsById;
    private readonly Dictionary<string, Lazy<Dictionary<string, object?>?>> _definitionsById;
    private readonly Dictionary<string, Lazy<Dictionary<string, object?>?>> _definitionsBySlug;

    public ProjectMetadataSnapshot(IReadOnlyList<JsonElement> collections, IReadOnlyList<JsonElement> endpoints, JsonElement? gameValues)
    {
        Collections = collections;
        Endpoints = endpoints;
        GameValues = gameValues;
        _collectionsById = Index(collections, "collection_id");
        _endpointsById = Index(endpoints, "endpoint_id");
        _definitionsById = new(StringComparer.Ordinal);
        _definitionsBySlug = new(StringComparer.Ordinal);
        foreach (var row in endpoints)
        {
            var definition = new Lazy<Dictionary<string, object?>?>(() => ParseDefinition(row));
            if (row.TryGetProperty("endpoint_id", out var id) && id.ValueKind == JsonValueKind.String)
                _definitionsById[id.GetString()!] = definition;
            if (row.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
                _definitionsBySlug.TryAdd(slug.GetString()!, definition);
        }
    }

    /// <summary>Collection rows ordered by <c>collection_id</c>, as returned by the store.</summary>
    public IReadOnlyList<JsonElement> Collections { get; }

    /// <summary>Endpoint rows ordered by <c>endpoint_id</c>, as returned by the store.</summary>
    public IReadOnlyList<JsonElement> Endpoints { get; }

    /// <summary>The <c>game_values</c> row, or null when the project has none.</summary>
    public JsonElement? GameValues { get; }

    public JsonElement? Collection(string collectionId)
        => _collectionsById.TryGetValue(collectionId, out var row) ? row : null;

    public JsonElement? Endpoint(string endpointId)
        => _endpointsById.TryGetValue(endpointId, out var row) ? row : null;

    public Dictionary<string, object?>? EndpointDefinition(string slug)
        => _definitionsById.TryGetValue(slug, out var direct) ? direct.Value
            : _definitionsBySlug.TryGetValue(slug, out var definition) ? definition.Value : null;

    private static Dictionary<string, object?>? ParseDefinition(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition)) return null;
        if (definition.ValueKind == JsonValueKind.String)
        {
            var json = definition.GetString();
            if (string.IsNullOrEmpty(json)) return null;
            using var document = JsonDocument.Parse(json);
            return EndpointExpression.FromJson(document.RootElement) as Dictionary<string, object?>;
        }
        return definition.ValueKind == JsonValueKind.Object
            ? EndpointExpression.FromJson(definition) as Dictionary<string, object?> : null;
    }

    private static Dictionary<string, JsonElement> Index(IReadOnlyList<JsonElement> rows, string idColumn)
    {
        var index = new Dictionary<string, JsonElement>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty(idColumn, out var id) && id.ValueKind == JsonValueKind.String)
                index[id.GetString()!] = row;
        }
        return index;
    }
}
