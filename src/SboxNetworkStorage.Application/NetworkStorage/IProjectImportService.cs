using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// How a conflicting resource (one whose id already exists in the target
/// project) is handled during import.
/// </summary>
public enum ImportResolution
{
    /// <summary>Replace the existing resource with the incoming one.</summary>
    Overwrite,

    /// <summary>Keep the existing resource; do not write the incoming one.</summary>
    Skip,

    /// <summary>Import the incoming resource under a freshly generated id.</summary>
    Copy,
}

/// <summary>
/// Per-table import resolution: a default applied to every conflicting resource
/// in the table, plus optional per-resource overrides keyed by resource id.
/// Resolution only applies to <em>conflicting</em> ids; brand-new ids are always
/// written.
/// </summary>
public sealed record ImportResolutionPlan(
    ImportResolution Default = ImportResolution.Overwrite,
    IReadOnlyDictionary<string, ImportResolution>? PerResource = null)
{
    public ImportResolution Resolve(string resourceId)
        => PerResource is not null && PerResource.TryGetValue(resourceId, out var r) ? r : Default;
}

/// <summary>
/// The full plan for an import apply: whether to land into a fresh project, the
/// optional pre-import safety backup, and per-table resolutions. Records and
/// global records carry no resolution — they are always written (overwrite by
/// key); the pre-import backup and the preview conflict counts are their rail.
/// </summary>
public sealed record ImportApplyPlan(
    bool CreateAsNewProject,
    string? NewProjectName,
    bool PreImportBackup,
    ImportResolutionPlan Collections,
    ImportResolutionPlan Endpoints,
    ImportResolutionPlan Workflows,
    ImportResolutionPlan Queries,
    ImportResolutionPlan ApiKeys)
{
    /// <summary>A plan that overwrites everything (used for new-project imports
    /// where nothing can collide).</summary>
    public static ImportApplyPlan OverwriteAll(bool createAsNewProject = false, string? newProjectName = null, bool preImportBackup = false)
        => new(createAsNewProject, newProjectName, preImportBackup,
            new ImportResolutionPlan(), new ImportResolutionPlan(), new ImportResolutionPlan(),
            new ImportResolutionPlan(), new ImportResolutionPlan());
}

/// <summary>A single conflicting resource surfaced in the preview.</summary>
public sealed record ImportConflict(
    string Id,
    string Name,
    string? ExistingUpdatedAt,
    string? IncomingUpdatedAt);

/// <summary>Conflict report for one table.</summary>
public sealed record ImportTableReport(
    int IncomingCount,
    int ExistingCount,
    int ConflictCount,
    int NewCount,
    IReadOnlyList<ImportConflict> Conflicts);

/// <summary>
/// Full preview: source metadata, incoming row counts, and per-table conflict
/// reports. Resource tables (collections/endpoints/workflows/queries/api_keys)
/// carry per-resource conflict detail; records/global_records carry counts only
/// (a collection may hold millions of rows).
/// </summary>
public sealed record ImportPreviewReport(
    string SourceProjectId,
    string? ExportedAt,
    int ExportVersion,
    IReadOnlyDictionary<string, int> IncomingCounts,
    IReadOnlyDictionary<string, ImportTableReport> Tables)
{
    public int TotalIncomingRows
    {
        get
        {
            var total = 0;
            foreach (var v in IncomingCounts.Values) total += v;
            return total;
        }
    }
}

/// <summary>Result of an import apply.</summary>
public sealed record ImportApplyResult(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> IdRemappings,
    int TotalRows,
    long DurationMs,
    int? PreImportBackupId);

/// <summary>
/// Thrown when an uploaded file is not a structurally valid project export
/// (not JSON, truncated, or missing the expected export envelope). The message
/// names what is wrong so the caller can surface a clear 400 instead of a
/// generic failure or a partial write.
/// </summary>
public sealed class InvalidProjectExportException(string message) : Exception(message);

/// <summary>
/// Imports a project export (produced by the project backup/export) into a
/// target project, with collision detection, per-resource resolution, an
/// optional pre-import backup, and optimized bulk writes.
/// </summary>
public interface IProjectImportService
{
    /// <summary>
    /// Reads the export and compares it against the target project without
    /// writing anything. Throws <see cref="InvalidProjectExportException"/> for a
    /// malformed file.
    /// </summary>
    Task<ImportPreviewReport> PreviewAsync(string targetProjectId, Stream exportJson, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the export to the target project per <paramref name="plan"/>.
    /// When <see cref="ImportApplyPlan.PreImportBackup"/> is set, a full export of
    /// the target is taken and confirmed before any write; if it fails the import
    /// aborts and nothing is written. Throws
    /// <see cref="InvalidProjectExportException"/> for a malformed file.
    /// </summary>
    Task<ImportApplyResult> ApplyAsync(string targetProjectId, Stream exportJson, ImportApplyPlan plan, CancellationToken cancellationToken);
}
