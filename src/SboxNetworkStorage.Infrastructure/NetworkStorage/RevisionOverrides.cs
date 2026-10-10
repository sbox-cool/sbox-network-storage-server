using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// The staged ("next") revision: the workspace resource <c>revision-overrides.json</c>,
/// <c>{ "endpoints": { slug: endpoint }, "collections": { name: collection } }</c>. Staged items
/// overlay the live definitions until a package sync with a new revision promotes them
/// (<c>PackageSyncHandler</c>) and clears the file.
/// </summary>
public static class RevisionOverrides
{
    public const string ResourcePath = "revision-overrides.json";
    public const string EndpointsSection = "endpoints";
    public const string CollectionsSection = "collections";

    /// <summary>
    /// Staging needs a synced game package with a numeric <c>currentRevisionId</c>: without one there is
    /// no next revision to promote into, so staged writes go live instead.
    /// </summary>
    public static async Task<bool> HasRevisionDataAsync(
        IWorkspaceStore client, long ownerUserId, string projectId, CancellationToken ct)
    {
        var package = await client.GetProjectResourceAsync<Dictionary<string, JsonElement>>(
            ownerUserId, projectId, "game-package.json", ct);
        return package is not null
            && package.TryGetValue("currentRevisionId", out var revision)
            && revision.ValueKind == JsonValueKind.Number;
    }

    public static async Task<JsonObject> ReadAsync(
        IWorkspaceStore client, long ownerUserId, string projectId, CancellationToken ct)
        => await client.GetProjectResourceAsync<JsonObject>(ownerUserId, projectId, ResourcePath, ct) ?? new JsonObject();

    /// <summary>The staged items of one section, keyed as stored (endpoint slug or collection name).</summary>
    public static IReadOnlyList<KeyValuePair<string, JsonObject>> Items(JsonObject overrides, string section)
    {
        if (overrides[section] is not JsonObject items) return [];
        var result = new List<KeyValuePair<string, JsonObject>>(items.Count);
        foreach (var (key, value) in items)
            if (value is JsonObject item) result.Add(new(key, item));
        return result;
    }

    /// <summary>Adds or replaces staged items in one read-modify-write of the overrides resource.</summary>
    public static async Task StageAsync(
        IWorkspaceStore client, long ownerUserId, string projectId, StagedRevisionWrites writes, CancellationToken ct)
    {
        if (writes.IsEmpty) return;
        var overrides = await ReadAsync(client, ownerUserId, projectId, ct);
        Put(overrides, EndpointsSection, writes.Endpoints);
        Put(overrides, CollectionsSection, writes.Collections);
        await client.PutProjectResourceAsync(ownerUserId, projectId, ResourcePath, overrides, ct);
    }

    private static void Put(JsonObject overrides, string section, IReadOnlyDictionary<string, JsonObject> items)
    {
        if (items.Count == 0) return;
        if (overrides[section] is not JsonObject target)
        {
            target = new JsonObject();
            overrides[section] = target;
        }
        foreach (var (key, item) in items)
            target[key] = item.DeepClone();
    }

    /// <summary>
    /// The definition a staged item produces once promoted: the live definition with every staged
    /// property laid over it except <c>id</c>, which stays the live one (matches package-sync promotion).
    /// </summary>
    public static JsonObject MergeOverLive(JsonElement? live, JsonObject staged)
    {
        var merged = live is { ValueKind: JsonValueKind.Object } current
            ? JsonNode.Parse(current.GetRawText())!.AsObject()
            : new JsonObject();
        var keepLiveId = merged.ContainsKey("id");
        foreach (var (name, value) in staged)
        {
            if (keepLiveId && name == "id") continue;
            merged[name] = value?.DeepClone();
        }
        return merged;
    }
}

/// <summary>Endpoints (by slug) and collections (by name) to write into the staged revision.</summary>
public sealed class StagedRevisionWrites
{
    public Dictionary<string, JsonObject> Endpoints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonObject> Collections { get; } = new(StringComparer.Ordinal);
    public bool IsEmpty => Endpoints.Count == 0 && Collections.Count == 0;
}
