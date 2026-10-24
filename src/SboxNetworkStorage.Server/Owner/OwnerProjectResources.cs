using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// A project's stored resources, loaded once per owner request. Feeds save-time
/// cross-reference checks and the builder pickers (collections and schema
/// fields, workflows and params, game value groups and tables).
/// </summary>
internal sealed class OwnerProjectResources
{
    private OwnerProjectResources(IReadOnlyList<JsonElement> collections, IReadOnlyList<JsonElement> endpoints,
        IReadOnlyList<JsonElement> workflows, IReadOnlyList<JsonElement> queries, JsonElement? gameValues)
    {
        Collections = collections;
        Endpoints = endpoints;
        Workflows = workflows;
        Queries = queries;
        GameValues = gameValues;
    }

    public IReadOnlyList<JsonElement> Collections { get; }
    public IReadOnlyList<JsonElement> Endpoints { get; }
    public IReadOnlyList<JsonElement> Workflows { get; }
    public IReadOnlyList<JsonElement> Queries { get; }
    /// <summary>The stored game values payload, or null when none is saved.</summary>
    public JsonElement? GameValues { get; }

    public static async Task<OwnerProjectResources> LoadAsync(INetworkStorageStore store, string projectId, CancellationToken ct)
    {
        var values = await store.ReadGameValuesAsync(projectId, ct);
        return new OwnerProjectResources(
            await store.ListCollectionsAsync(projectId, ct),
            await store.ListEndpointsAsync(projectId, ct),
            await store.ListWorkflowsAsync(projectId, ct),
            await store.ListQueriesAsync(projectId, ct),
            values is { ValueKind: JsonValueKind.Object } row ? Column(row, "payload_json") : null);
    }

    public IReadOnlyList<JsonElement> Rows(string kind) => kind switch
    {
        "collection" => Collections,
        "endpoint" => Endpoints,
        "workflow" => Workflows,
        "query" => Queries,
        _ => [],
    };

    /// <summary>Ids already saved for a kind; for game values, the ids of stored groups and tables.</summary>
    public HashSet<string> ExistingIds(string kind) => kind == "game-values"
        ? ValueItems().Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
        : Rows(kind).Select(row => Text(row, kind + "_id")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The resources a deployment batch would leave behind: these stored rows with the batch's writes applied and its
    /// removals dropped. Planning validates each proposed definition against this set, so same-batch references resolve
    /// and references to removed definitions do not.
    /// </summary>
    public OwnerProjectResources WithChanges(IReadOnlyList<(string Kind, string Id, JsonElement Definition)> writes,
        IReadOnlyList<(string Kind, string Id)> removals, JsonElement? gameValues)
    {
        var rows = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal)
        {
            ["collection"] = [.. Collections],
            ["endpoint"] = [.. Endpoints],
            ["workflow"] = [.. Workflows],
            ["query"] = [.. Queries],
        };
        foreach (var (kind, id) in removals)
            rows[kind].RemoveAll(row => Text(row, kind + "_id") == id);
        foreach (var (kind, id, definition) in writes)
        {
            rows[kind].RemoveAll(row => Text(row, kind + "_id") == id);
            rows[kind].Add(StoredRow(kind, id, definition));
        }
        return new OwnerProjectResources(rows["collection"], rows["endpoint"], rows["workflow"], rows["query"], gameValues);
    }

    private static JsonElement StoredRow(string kind, string id, JsonElement definition)
    {
        var nameField = kind == "endpoint" ? "slug" : "name";
        var row = new Dictionary<string, object?>
        {
            [kind + "_id"] = id,
            [nameField] = Text(definition, nameField) ?? id,
            ["definition_json"] = definition,
        };
        return JsonSerializer.SerializeToElement(row);
    }

    /// <summary>Sorted collection ids, for the endpoint script typings.</summary>
    public IReadOnlyList<string> CollectionIds() => ExistingIds("collection").Order(StringComparer.Ordinal).ToList();

    /// <summary>Sorted workflow ids, for the endpoint script typings.</summary>
    public IReadOnlyList<string> WorkflowIds() => ExistingIds("workflow").Order(StringComparer.Ordinal).ToList();

    /// <summary>Analysed exports of the project's enabled global endpoints, keyed by endpoint id.</summary>
    public IReadOnlyDictionary<string, ScriptModuleInfo> GlobalExports(IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes) =>
        ProjectGlobalModules(null, scriptRuntimes);

    public DefinitionValidationContext ValidationContext(string? resourceId, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes)
    {
        var collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Collections)
        {
            if (Text(row, "collection_id") is { } id) collections.Add(id);
            if (Text(row, "name") is { } name) collections.Add(name);
        }
        return new DefinitionValidationContext
        {
            HasProjectResources = true,
            Collections = collections,
            Workflows = ExistingIds("workflow"),
            ValueTables = ValueItems().Where(item => item.Table).Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
            ResourceId = resourceId,
            ScriptRuntimes = scriptRuntimes,
            GlobalModules = ProjectGlobalModules(resourceId, scriptRuntimes),
        };
    }

    /// <summary>
    /// The save-time check of a global endpoint's proposed definition against the stored scripted endpoints that import
    /// it: see <see cref="NetworkStorageDefinitionValidator.ValidateGlobalImporters"/>.
    /// </summary>
    public IReadOnlyList<DefinitionDiagnostic> GlobalImporterDiagnostics(string globalId, JsonElement proposed, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes)
    {
        var endpoints = new List<(string Id, JsonElement Definition)>();
        foreach (var row in Endpoints)
        {
            if (Text(row, "endpoint_id") is { } id && Column(row, "definition_json") is { ValueKind: JsonValueKind.Object } definition)
                endpoints.Add((id, definition));
        }
        var context = new DefinitionValidationContext
        {
            HasProjectResources = true,
            ScriptRuntimes = scriptRuntimes,
            GlobalModules = ProjectGlobalModules(globalId, scriptRuntimes),
        };
        return NetworkStorageDefinitionValidator.ValidateGlobalImporters(globalId, proposed, endpoints, context);
    }

    /// <summary>
    /// Ids of the scripted endpoints that import <c>globals/&lt;globalId&gt;</c>, sorted. An endpoint whose source does
    /// not check is skipped: it cannot run, so it is not a usage that the global's owner needs to see.
    /// </summary>
    public IReadOnlyList<string> GlobalImporters(string globalId, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes)
    {
        var importers = new List<string>();
        foreach (var row in Endpoints)
        {
            if (Text(row, "endpoint_id") is not { } id || id == globalId) continue;
            if (Column(row, "definition_json") is not { } definition) continue;
            if (!NetworkStorageDefinitionValidator.TryAnalyzeScript(definition, scriptRuntimes, out var module)) continue;
            if (module.GlobalImports.Any(import => import.EndpointId == globalId)) importers.Add(id);
        }
        importers.Sort(StringComparer.Ordinal);
        return importers;
    }

    /// <summary>
    /// Analysed imports and exports of the project's enabled global endpoints, excluding <paramref name="resourceId"/>.
    /// </summary>
    private Dictionary<string, ScriptModuleInfo> ProjectGlobalModules(string? resourceId, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes)
    {
        var endpoints = new List<(string Id, JsonElement Definition)>();
        foreach (var row in Endpoints)
            if (Text(row, "endpoint_id") is { } id && id != resourceId && Column(row, "definition_json") is { } definition)
                endpoints.Add((id, definition));
        return NetworkStorageDefinitionValidator.ProjectGlobalModules(endpoints, scriptRuntimes);
    }

    /// <summary>Compact JSON for the builder pickers, embedded in the Resources page.</summary>
    public string BuilderJson() => JsonSerializer.Serialize(new
    {
        collections = Collections.Select(row =>
        {
            var definition = Column(row, "definition_json");
            return new
            {
                id = Text(row, "collection_id") ?? "",
                type = definition is { } d ? StepText(d, "collectionType") ?? "per-steamid" : "per-steamid",
                fields = definition is { } def && def.TryGetProperty("schema", out var schema) ? SchemaFields(schema) : [],
            };
        }),
        workflows = Workflows.Select(row =>
        {
            var definition = Column(row, "definition_json");
            return new { id = Text(row, "workflow_id") ?? "", @params = definition is { } d ? ParamNames(d) : [] };
        }),
        endpoints = ExistingIds("endpoint").Order(),
        groups = ValueItems().Where(item => !item.Table).Select(item => new { id = item.Id, keys = item.Keys }),
        tables = ValueItems().Where(item => item.Table).Select(item => new { id = item.Id, columns = item.Keys }),
    });

    internal sealed record ValueItem(string Id, bool Table, IReadOnlyList<string> Keys);

    /// <summary>Game value groups and tables from the stored payload and collection constants/tables.</summary>
    public IReadOnlyList<ValueItem> ValueItems()
    {
        var items = new Dictionary<string, ValueItem>(StringComparer.Ordinal);
        if (GameValues is { ValueKind: JsonValueKind.Object } values)
        {
            if (values.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray()) Add(items, item, StepText(item, "type") == "table");
            else
            {
                foreach (var (name, table) in new[] { ("groups", false), ("tables", true) })
                    if (values.TryGetProperty(name, out var legacy) && legacy.ValueKind == JsonValueKind.Array)
                        foreach (var item in legacy.EnumerateArray()) Add(items, item, table);
            }
        }
        foreach (var row in Collections)
        {
            if (Column(row, "definition_json") is not { } definition) continue;
            foreach (var (name, table) in new[] { ("constants", false), ("tables", true) })
                if (definition.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray()) Add(items, item, table);
        }
        return items.Values.ToList();
    }

    private static void Add(Dictionary<string, ValueItem> items, JsonElement item, bool table)
    {
        if (StepText(item, "id") is not { Length: > 0 } id || items.ContainsKey(id)) return;
        var keys = new List<string>();
        if (table)
        {
            if (item.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
                keys.AddRange(columns.EnumerateArray().Select(column => StepText(column, "key") ?? StepText(column, "name")).OfType<string>());
            else if (item.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0
                && rows[0].ValueKind == JsonValueKind.Object)
                keys.AddRange(rows[0].EnumerateObject().Select(cell => cell.Name));
        }
        else if (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
            keys.AddRange(entries.EnumerateObject().Select(entry => entry.Name));
        items[id] = new ValueItem(id, table, keys);
    }

    private static IReadOnlyList<string> SchemaFields(JsonElement schema)
    {
        var fields = new List<string>();
        void Walk(JsonElement properties, string prefix, int depth)
        {
            if (properties.ValueKind != JsonValueKind.Object || depth > 3) return;
            foreach (var field in properties.EnumerateObject())
            {
                var path = prefix + field.Name;
                fields.Add(path);
                if (field.Value.ValueKind == JsonValueKind.Object && field.Value.TryGetProperty("properties", out var children))
                    Walk(children, path + ".", depth + 1);
            }
        }
        Walk(schema.TryGetProperty("properties", out var standard) ? standard : schema, "", 0);
        return fields;
    }

    private static IReadOnlyList<string> ParamNames(JsonElement definition)
    {
        if (!definition.TryGetProperty("params", out var parameters)) return [];
        return parameters.ValueKind switch
        {
            JsonValueKind.Object => parameters.EnumerateObject().Select(p => p.Name).ToList(),
            JsonValueKind.Array => parameters.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : StepText(p, "name")).OfType<string>().ToList(),
            _ => [],
        };
    }

    internal static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? StepText(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static JsonElement? Column(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return value.Clone();
        try
        {
            using var document = JsonDocument.Parse(value.GetString() ?? "null");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
