using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Import;

/// <summary>
/// A parsed and validated project export envelope. Wraps the underlying
/// <see cref="JsonDocument"/> (which owns the memory the table
/// <see cref="JsonElement"/>s point at) so callers dispose it after use.
/// Construction is the single validation seam: a malformed file throws
/// <see cref="InvalidProjectExportException"/> before any work is done.
/// </summary>
internal sealed class ProjectExportDocument : IDisposable
{
    private readonly JsonDocument _doc;

    public string ProjectId { get; }
    public string? ExportedAt { get; }
    public int ExportVersion { get; }

    /// <summary>The export's <c>tables</c> object.</summary>
    public JsonElement Tables { get; }

    private ProjectExportDocument(JsonDocument doc, string projectId, string? exportedAt, int exportVersion, JsonElement tables)
    {
        _doc = doc;
        ProjectId = projectId;
        ExportedAt = exportedAt;
        ExportVersion = exportVersion;
        Tables = tables;
    }

    public static ProjectExportDocument Parse(Stream json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidProjectExportException($"File is not valid JSON: {ex.Message}");
        }
        return Validate(doc);
    }

    public static ProjectExportDocument Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidProjectExportException($"File is not valid JSON: {ex.Message}");
        }
        return Validate(doc);
    }

    private static ProjectExportDocument Validate(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new InvalidProjectExportException("Backup root must be a JSON object - this does not look like a project export.");
        }

        var projectId = root.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;
        if (string.IsNullOrEmpty(projectId))
        {
            doc.Dispose();
            throw new InvalidProjectExportException("Backup is missing a 'project_id' - this does not look like a project export.");
        }

        var exportVersion = root.TryGetProperty("export_version", out var ev) && ev.ValueKind == JsonValueKind.Number
            ? ev.GetInt32() : 0;
        if (exportVersion <= 0)
        {
            doc.Dispose();
            throw new InvalidProjectExportException("Backup is missing a recognized 'export_version' - this does not look like a project export.");
        }

        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new InvalidProjectExportException("Backup is missing its 'tables' object - this does not look like a project export.");
        }

        var exportedAt = root.TryGetProperty("exported_at", out var ea) && ea.ValueKind == JsonValueKind.String
            ? ea.GetString() : null;

        return new ProjectExportDocument(doc, projectId, exportedAt, exportVersion, tables);
    }

    public void Dispose() => _doc.Dispose();
}

/// <summary>Existing ids (→ last update time) for one resource table in the target project.</summary>
internal sealed class ExistingResourceTable(IReadOnlyDictionary<string, long?> updatedAtById)
{
    public static readonly ExistingResourceTable Empty = new(new Dictionary<string, long?>());

    public int Count => updatedAtById.Count;
    public bool Contains(string id) => updatedAtById.ContainsKey(id);
    public long? UpdatedAt(string id) => updatedAtById.TryGetValue(id, out var v) ? v : null;
}

/// <summary>
/// A snapshot of what already exists in the target project, used by the planner
/// to classify conflicts. Resource tables carry id→update-time maps; records and
/// global records carry per-collection key sets (preview only - apply always
/// overwrites records by key).
/// </summary>
internal sealed class ExistingProjectSnapshot
{
    public static readonly ExistingProjectSnapshot Empty = new();

    public ExistingResourceTable Collections { get; init; } = ExistingResourceTable.Empty;
    public ExistingResourceTable Endpoints { get; init; } = ExistingResourceTable.Empty;
    public ExistingResourceTable Workflows { get; init; } = ExistingResourceTable.Empty;
    public ExistingResourceTable Queries { get; init; } = ExistingResourceTable.Empty;
    public ExistingResourceTable ApiKeys { get; init; } = ExistingResourceTable.Empty;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> RecordKeysByCollection { get; init; }
        = new Dictionary<string, IReadOnlySet<string>>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> GlobalRecordIdsByCollection { get; init; }
        = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>Singleton scalar tables (game_values, rate_limit_rules,
    /// checkpoint_cursor) that already have a row in the target project - so the
    /// preview reports them as "will be replaced" instead of "new".</summary>
    public IReadOnlySet<string> ExistingScalarTables { get; init; } = new HashSet<string>();

    public ExistingResourceTable Resource(string table) => table switch
    {
        "collections" => Collections,
        "endpoints" => Endpoints,
        "workflows" => Workflows,
        "queries" => Queries,
        "api_keys" => ApiKeys,
        _ => ExistingResourceTable.Empty,
    };
}

/// <summary>
/// A single resolved write the import will perform. Pure data: the store service
/// binds the matching prepared statement from <see cref="Row"/> using
/// <see cref="WriteId"/> (id/record key), <see cref="CollectionId"/> (remapped
/// collection for records), and <see cref="DefinitionOverride"/> (remapped query
/// <c>definition_json</c>, when collection refs changed).
/// </summary>
internal sealed record ImportWriteAction(
    string Table,
    string WriteId,
    string? CollectionId,
    string? DefinitionOverride,
    JsonElement Row);

/// <summary>
/// The output of planning: the ordered write actions and the id remappings that
/// were applied (table → old id → new id), surfaced to the user.
/// </summary>
internal sealed record ProjectImportPlan(
    IReadOnlyList<ImportWriteAction> Actions,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Remappings);
