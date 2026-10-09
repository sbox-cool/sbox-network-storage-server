using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>One stored snapshot of an endpoint or workflow definition.</summary>
public sealed record ResourceVersionEntry(string Name, long SavedAtUnixMs, string Hash, string Source, JsonElement Definition);

/// <summary>
/// Project-scoped workspace objects for management features that have no dedicated table:
/// saved endpoint tests (<c>tests.json</c>, the file the editor Sync Tool reads and writes)
/// and durable version snapshots of endpoint and workflow definitions. Objects live under
/// <c>network-storage/users/{owner}/{project}/</c>, the same layout the workspace client uses,
/// so they travel with the database backup and project archive.
/// </summary>
public static class ManagementProjectObjects
{
    public const int MaxVersionsPerResource = 50;
    public const int MaxSavedTests = 500;

    private static string ProjectPath(long ownerUserId, string projectId, string relative)
        => $"network-storage/users/{ownerUserId.ToString(CultureInfo.InvariantCulture)}/{projectId}/{relative}";

    private static string VersionDirectory(long ownerUserId, string projectId, string kind, string resourceId)
        => ProjectPath(ownerUserId, projectId, $"versions/{kind}/{Uri.EscapeDataString(resourceId)}");

    // ── Saved tests ──

    public static async Task<IReadOnlyList<JsonElement>> ReadTestsAsync(INetworkStorageStore store, long ownerUserId, string projectId, CancellationToken ct)
    {
        var text = await store.ReadWorkspaceObjectAsync(ProjectPath(ownerUserId, projectId, "tests.json"), ct);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<JsonElement>();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tests", out var nested)) root = nested;
        return root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Where(test => test.ValueKind == JsonValueKind.Object).Select(test => test.Clone()).ToList()
            : Array.Empty<JsonElement>();
    }

    /// <summary>Replaces the saved test list. Each test gets a stable id when it has none.</summary>
    public static async Task<IReadOnlyList<JsonElement>> WriteTestsAsync(INetworkStorageStore store, long ownerUserId, string projectId,
        IEnumerable<JsonElement> tests, CancellationToken ct)
    {
        var normalized = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            if (test.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each test must be an object.");
            var fields = test.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
            var id = test.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idElement.GetString())
                ? idElement.GetString()!
                : "test_" + Guid.NewGuid().ToString("N")[..12];
            while (!ids.Add(id)) id = "test_" + Guid.NewGuid().ToString("N")[..12];
            fields["id"] = id;
            normalized.Add(JsonSerializer.SerializeToElement(fields));
        }
        if (normalized.Count > MaxSavedTests)
            throw new ArgumentException($"A project can keep at most {MaxSavedTests} saved tests.");
        await store.PutWorkspaceObjectAsync(ProjectPath(ownerUserId, projectId, "tests.json"), JsonSerializer.Serialize(normalized), ct);
        return normalized;
    }

    // ── Version history ──

    /// <summary>
    /// Appends a snapshot when the definition differs from the latest stored one, then prunes the
    /// oldest snapshots beyond <see cref="MaxVersionsPerResource"/>.
    /// </summary>
    public static async Task RecordVersionAsync(INetworkStorageStore store, long ownerUserId, string projectId, string kind,
        string resourceId, JsonElement definition, string source, long savedAtUnixMs, CancellationToken ct)
    {
        var raw = definition.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..16];
        var directory = VersionDirectory(ownerUserId, projectId, kind, resourceId);
        var names = await ListVersionNamesAsync(store, directory, ct);
        if (names.Count > 0 && names[^1].EndsWith($"-{hash}.json", StringComparison.Ordinal)) return;
        var name = $"{savedAtUnixMs.ToString("D13", CultureInfo.InvariantCulture)}-{hash}.json";
        var payload = JsonSerializer.Serialize(new { kind, id = resourceId, savedAt = savedAtUnixMs, hash, source, definition });
        await store.PutWorkspaceObjectAsync($"{directory}/{name}", payload, ct);
        names.Add(name);
        foreach (var stale in names.Order(StringComparer.Ordinal).Take(Math.Max(0, names.Count - MaxVersionsPerResource)))
            await store.DeleteWorkspaceObjectAsync($"{directory}/{stale}", ct);
    }

    /// <summary>Snapshots newest first.</summary>
    public static async Task<IReadOnlyList<ResourceVersionEntry>> ListVersionsAsync(INetworkStorageStore store, long ownerUserId, string projectId,
        string kind, string resourceId, CancellationToken ct)
    {
        var directory = VersionDirectory(ownerUserId, projectId, kind, resourceId);
        var entries = new List<ResourceVersionEntry>();
        foreach (var name in (await ListVersionNamesAsync(store, directory, ct)).AsEnumerable().Reverse())
        {
            var entry = await ReadEntryAsync(store, directory, name, ct);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    public static async Task<int> CountVersionsAsync(INetworkStorageStore store, long ownerUserId, string projectId,
        string kind, string resourceId, CancellationToken ct)
        => (await ListVersionNamesAsync(store, VersionDirectory(ownerUserId, projectId, kind, resourceId), ct)).Count;

    private static async Task<List<string>> ListVersionNamesAsync(INetworkStorageStore store, string directory, CancellationToken ct)
        => (await store.ListWorkspaceObjectsAsync(directory, ct))
            .Where(entry => !entry.IsDirectory && IsVersionName(entry.Name))
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsVersionName(string name)
        => name.Length == 35 && name.EndsWith(".json", StringComparison.Ordinal) && name[13] == '-'
           && name[..13].All(char.IsAsciiDigit) && name[14..30].All(char.IsAsciiHexDigitLower);

    private static async Task<ResourceVersionEntry?> ReadEntryAsync(INetworkStorageStore store, string directory, string name, CancellationToken ct)
    {
        var text = await store.ReadWorkspaceObjectAsync($"{directory}/{name}", ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("definition", out var definition)) return null;
        var savedAt = root.TryGetProperty("savedAt", out var saved) && saved.TryGetInt64(out var ms) ? ms : 0;
        var source = root.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String ? sourceElement.GetString() ?? "" : "";
        return new ResourceVersionEntry(name, savedAt, name[14..30], source, definition.Clone());
    }
}
