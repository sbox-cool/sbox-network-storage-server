using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Import;

/// <summary>
/// Pure (no-I/O) core of project import. Given a parsed export and a snapshot of
/// what already exists in the target, it (a) produces the preview conflict report
/// and (b) resolves the export into an ordered list of write actions, applying
/// per-resource resolution and id remapping. All decision-making lives here so it
/// is unit-testable without a ScyllaDB cluster; the Scylla service only does I/O.
/// </summary>
internal static class ProjectImportPlanner
{
    // table, id column, display-name column
    private static readonly (string Table, string IdCol, string NameCol)[] ResourceTables =
    [
        ("collections", "collection_id", "name"),
        ("endpoints", "endpoint_id", "slug"),
        ("workflows", "workflow_id", "name"),
        ("queries", "query_id", "name"),
        ("api_keys", "api_key", "label"),
    ];

    private static readonly string[] ScalarTables = ["game_values", "rate_limit_rules", "checkpoint_cursor"];
    private static readonly IReadOnlyList<ImportConflict> NoConflicts = [];

    // ── Preview ───────────────────────────────────────────────────────

    public static ImportPreviewReport Preview(ProjectExportDocument doc, ExistingProjectSnapshot existing)
    {
        var tables = doc.Tables;
        var incomingCounts = new Dictionary<string, int>();
        var reports = new Dictionary<string, ImportTableReport>();

        foreach (var (table, idCol, nameCol) in ResourceTables)
            reports[table] = PreviewResource(tables, table, idCol, nameCol, existing.Resource(table), incomingCounts);

        reports["records"] = PreviewRecords(tables, "records", "record_key", existing.RecordKeysByCollection, incomingCounts);
        reports["global_records"] = PreviewRecords(tables, "global_records", "record_id", existing.GlobalRecordIdsByCollection, incomingCounts);

        foreach (var scalar in ScalarTables)
        {
            if (tables.TryGetProperty(scalar, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var count = arr.GetArrayLength();
                incomingCounts[scalar] = count;
                // Scalars are single-row per project: if the target already has the
                // row, the import overwrites it — report a conflict, not "new".
                var exists = existing.ExistingScalarTables.Contains(scalar);
                reports[scalar] = new ImportTableReport(count, exists ? count : 0, exists ? count : 0, exists ? 0 : count, NoConflicts);
            }
        }

        return new ImportPreviewReport(doc.ProjectId, doc.ExportedAt, doc.ExportVersion, incomingCounts, reports);
    }

    private static ImportTableReport PreviewResource(
        JsonElement tables, string table, string idCol, string nameCol,
        ExistingResourceTable existing, Dictionary<string, int> incomingCounts)
    {
        if (!tables.TryGetProperty(table, out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            incomingCounts[table] = 0;
            return new ImportTableReport(0, existing.Count, 0, 0, NoConflicts);
        }

        var conflicts = new List<ImportConflict>();
        var incoming = 0;
        var newCount = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Str(row, idCol);
            if (string.IsNullOrEmpty(id)) continue;
            incoming++;
            if (existing.Contains(id))
                conflicts.Add(new ImportConflict(id, Str(row, nameCol) ?? id, FormatTs(existing.UpdatedAt(id)), Str(row, "updated_at_unix_ms")));
            else
                newCount++;
        }

        incomingCounts[table] = incoming;
        return new ImportTableReport(incoming, existing.Count, conflicts.Count, newCount, conflicts);
    }

    private static ImportTableReport PreviewRecords(
        JsonElement tables, string table, string keyCol,
        IReadOnlyDictionary<string, IReadOnlySet<string>> existingByCollection,
        Dictionary<string, int> incomingCounts)
    {
        if (!tables.TryGetProperty(table, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            incomingCounts[table] = 0;
            return new ImportTableReport(0, 0, 0, 0, NoConflicts);
        }

        var incoming = 0;
        var conflict = 0;
        var existingTotal = 0;
        var countedCollections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var coll in obj.EnumerateObject())
        {
            if (coll.Value.ValueKind != JsonValueKind.Array) continue;
            var existingKeys = existingByCollection.TryGetValue(coll.Name, out var s) ? s : null;
            if (existingKeys is not null && countedCollections.Add(coll.Name))
                existingTotal += existingKeys.Count;
            foreach (var row in coll.Value.EnumerateArray())
            {
                var key = Str(row, keyCol);
                if (string.IsNullOrEmpty(key)) continue;
                incoming++;
                if (existingKeys is not null && existingKeys.Contains(key)) conflict++;
            }
        }

        incomingCounts[table] = incoming;
        return new ImportTableReport(incoming, existingTotal, conflict, incoming - conflict, NoConflicts);
    }

    // ── Plan ──────────────────────────────────────────────────────────

    public static ProjectImportPlan Plan(
        ProjectExportDocument doc, ExistingProjectSnapshot existing, ImportApplyPlan plan, Func<string>? newId = null)
    {
        var generate = newId ?? GenerateShortId;
        var tables = doc.Tables;
        var actions = new List<ImportWriteAction>();
        var remappings = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        // Collections first: their remap drives records and query references.
        var collectionRemap = new Dictionary<string, string>(StringComparer.Ordinal);
        PlanResource(tables, "collections", "collection_id", plan.Collections, existing.Collections,
            plan.CreateAsNewProject, generate, actions, collectionRemap, collectionRemapForDefs: null);
        AddRemap(remappings, "collections", collectionRemap);

        AddRemap(remappings, "endpoints",
            PlanResource(tables, "endpoints", "endpoint_id", plan.Endpoints, existing.Endpoints,
                plan.CreateAsNewProject, generate, actions, sink: null, collectionRemapForDefs: null));
        AddRemap(remappings, "workflows",
            PlanResource(tables, "workflows", "workflow_id", plan.Workflows, existing.Workflows,
                plan.CreateAsNewProject, generate, actions, sink: null, collectionRemapForDefs: null));
        AddRemap(remappings, "queries",
            PlanResource(tables, "queries", "query_id", plan.Queries, existing.Queries,
                plan.CreateAsNewProject, generate, actions, sink: null, collectionRemapForDefs: collectionRemap));
        AddRemap(remappings, "api_keys",
            PlanResource(tables, "api_keys", "api_key", plan.ApiKeys, existing.ApiKeys,
                plan.CreateAsNewProject, generate, actions, sink: null, collectionRemapForDefs: null));

        foreach (var scalar in ScalarTables)
            if (tables.TryGetProperty(scalar, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                actions.Add(new ImportWriteAction(scalar, string.Empty, null, null, arr[0]));

        PlanRecords(tables, "records", "record_key", collectionRemap, actions);
        PlanRecords(tables, "global_records", "record_id", collectionRemap, actions);

        return new ProjectImportPlan(actions, remappings);
    }

    /// <summary>
    /// Plans one resource table. Returns the remap (old id → new id) this table
    /// produced. When <paramref name="sink"/> is supplied (collections), generated
    /// ids are written there too so records/queries can follow them.
    /// </summary>
    private static Dictionary<string, string> PlanResource(
        JsonElement tables, string table, string idCol, ImportResolutionPlan resolution,
        ExistingResourceTable existing, bool newProject, Func<string> generate,
        List<ImportWriteAction> actions, Dictionary<string, string>? sink,
        Dictionary<string, string>? collectionRemapForDefs)
    {
        var remap = sink ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!tables.TryGetProperty(table, out var rows) || rows.ValueKind != JsonValueKind.Array)
            return remap;

        foreach (var row in rows.EnumerateArray())
        {
            var id = Str(row, idCol);
            if (string.IsNullOrEmpty(id)) continue;

            string writeId;
            if (newProject)
            {
                writeId = generate();
                remap[id] = writeId;
            }
            else if (!existing.Contains(id))
            {
                writeId = id; // brand-new id: always written
            }
            else
            {
                switch (resolution.Resolve(id))
                {
                    case ImportResolution.Skip:
                        continue;
                    case ImportResolution.Copy:
                        writeId = generate();
                        remap[id] = writeId;
                        break;
                    default: // Overwrite
                        writeId = id;
                        break;
                }
            }

            string? defOverride = null;
            if (table == "queries" && collectionRemapForDefs is { Count: > 0 })
                defOverride = RemapQueryCollectionRefs(Str(row, "definition_json") ?? "{}", collectionRemapForDefs);

            actions.Add(new ImportWriteAction(table, writeId, null, defOverride, row));
        }

        return remap;
    }

    private static void PlanRecords(
        JsonElement tables, string table, string keyCol,
        Dictionary<string, string> collectionRemap, List<ImportWriteAction> actions)
    {
        if (!tables.TryGetProperty(table, out var obj) || obj.ValueKind != JsonValueKind.Object) return;

        foreach (var coll in obj.EnumerateObject())
        {
            if (coll.Value.ValueKind != JsonValueKind.Array) continue;
            var writeCollection = collectionRemap.TryGetValue(coll.Name, out var remapped) ? remapped : coll.Name;
            foreach (var row in coll.Value.EnumerateArray())
            {
                var key = Str(row, keyCol);
                if (string.IsNullOrEmpty(key)) continue;
                actions.Add(new ImportWriteAction(table, key, writeCollection, null, row));
            }
        }
    }

    /// <summary>
    /// Structurally rewrites collection-id references inside a query
    /// <c>definition_json</c>: walks the JSON tree and substitutes only string
    /// fields named <c>collectionId</c>/<c>collection_id</c> whose value is in the
    /// remap. Far safer than a blind string replace, which could corrupt unrelated
    /// substrings. Returns the input unchanged if it is not parseable JSON.
    /// </summary>
    internal static string RemapQueryCollectionRefs(string definitionJson, IReadOnlyDictionary<string, string> remap)
    {
        if (remap.Count == 0 || string.IsNullOrEmpty(definitionJson)) return definitionJson;
        JsonNode? node;
        try { node = JsonNode.Parse(definitionJson); }
        catch (JsonException) { return definitionJson; }
        if (node is null) return definitionJson;
        RemapNode(node, remap);
        return node.ToJsonString();
    }

    private static void RemapNode(JsonNode node, IReadOnlyDictionary<string, string> remap)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if ((kv.Key is "collectionId" or "collection_id")
                        && kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                        && remap.TryGetValue(s, out var replacement))
                    {
                        obj[kv.Key] = replacement;
                    }
                    else if (kv.Value is not null)
                    {
                        RemapNode(kv.Value, remap);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item is not null) RemapNode(item, remap);
                break;
        }
    }

    private static void AddRemap(
        Dictionary<string, IReadOnlyDictionary<string, string>> remappings, string table, Dictionary<string, string> remap)
    {
        if (remap.Count > 0) remappings[table] = remap;
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FormatTs(long? ms) => ms?.ToString(CultureInfo.InvariantCulture);

    internal static string GenerateShortId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
