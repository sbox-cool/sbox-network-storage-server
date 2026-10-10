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

    /// <summary>
    /// Why an archived <c>api-keys</c> row is unsafe to import, or null when it has the shape sbox-ns
    /// itself writes. Without this an archive could plant a known key that the server treats as a
    /// secret key: a row with <c>key_type = "secret"</c> and a plain <c>api_key</c> would resolve through
    /// the public-key lookup. Public keys are stored in full (<c>sbox_ns_...</c>); secret keys only as the
    /// masked display form plus a SHA-256 hash, so their raw value never appears in an archive.
    /// </summary>
    public static string? ApiKeyRowProblem(JsonElement row, string ownerUserId)
    {
        string? Text(string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var userId = Text("user_id");
        if (userId != ownerUserId)
            return $"API keys must have user_id \"{ownerUserId}\" (got {userId}).";
        var apiKey = Text("api_key") ?? "";
        return Text("key_type") switch
        {
            "public" when apiKey.StartsWith("sbox_ns_", StringComparison.Ordinal) && apiKey.Length > "sbox_ns_".Length => null,
            "public" => "a public API key must start with sbox_ns_.",
            "secret" when MaskedSecretKeyPattern().IsMatch(apiKey) && Sha256HexPattern().IsMatch(Text("key_hash") ?? "") => null,
            "secret" => "a secret API key must be stored masked (sbox_sk_xxxx...xxxx) with a 64-character hex key_hash.",
            var other => $"API key type must be \"public\" or \"secret\" (got {other ?? "none"}).",
        };
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.None, 100)]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex(@"^sbox_sk_[a-zA-Z0-9]{4}\.\.\.[a-zA-Z0-9]{4}$", RegexOptions.None, 100)]
    private static partial Regex MaskedSecretKeyPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.None, 100)]
    private static partial Regex Sha256HexPattern();
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
