using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Layout of an <c>sbox-ns export</c> archive (a gzip-compressed POSIX tar):
/// <c>manifest.json</c> first, then the driver-neutral data dump under <c>data/</c>
/// (one JSON object per line), then the config folder under <c>config/</c>.
/// </summary>
public static partial class ExportFormat
{
    public const string FormatName = "sbox-ns-export";

    /// <summary>Bump when the archive layout or a dump file shape changes incompatibly.</summary>
    public const int CurrentVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string DataPrefix = "data/";
    public const string ConfigPrefix = "config/";
    public const string WorkspaceObjectsEntry = "data/workspace-objects.jsonl";
    public const string MembershipsEntry = "data/memberships.jsonl";
    public const string ProjectsPrefix = "data/projects/";
    public const string FileExtension = ".tar.gz";

    /// <summary>Per-project dump files (<c>data/projects/&lt;projectId&gt;/&lt;resource&gt;.jsonl</c>).</summary>
    public static readonly IReadOnlyList<string> ProjectResources =
    [
        "project", "collections", "records", "ledger-entries", "global-records", "endpoints", "workflows",
        "queries", "api-keys", "pages", "game-values", "rate-limits", "checkpoint-cursor", "player-profiles",
        "player-sessions", "player-events", "audit-logs", "usage",
    ];

    public static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ProjectEntry(string projectId, string resource) => $"{ProjectsPrefix}{projectId}/{resource}.jsonl";

    /// <summary>Same identifier rule the stores enforce, so a project id is always a safe path segment.</summary>
    public static bool IsValidProjectId(string? value) => value is not null && ProjectIdPattern().IsMatch(value);

    public static string DefaultFileName(DateTimeOffset createdAt) => $"sbox-ns-export-{createdAt:yyyyMMdd-HHmmss}{FileExtension}";

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$")]
    private static partial Regex ProjectIdPattern();
}

/// <summary>Contents of <c>manifest.json</c>.</summary>
public sealed record ExportManifest(
    string Format,
    int FormatVersion,
    string SboxNsVersion,
    int SchemaVersion,
    string Provider,
    DateTimeOffset CreatedAt,
    bool IncludesConfig,
    bool IncludesSecrets,
    long WorkspaceObjects,
    long Memberships,
    IReadOnlyList<ExportedProject> Projects)
{
    [JsonIgnore]
    public long TotalRows => WorkspaceObjects + Memberships + Projects.Sum(p => p.Counts.Values.Sum());
}

/// <summary>One project in the manifest with the number of dumped rows per resource.</summary>
public sealed record ExportedProject(string Id, IReadOnlyDictionary<string, long> Counts);

/// <summary>An expected, user-facing export/import failure (bad archive, newer format, non-empty target).</summary>
public sealed class ExportArchiveException(string message) : Exception(message);
