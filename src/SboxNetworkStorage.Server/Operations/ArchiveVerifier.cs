using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// <c>sbox-ns import --verify-only</c>: checks an archive, including one produced outside
/// sbox-ns (docs/export.md "Archive contract"), without touching the configured server.
/// It checks the single-owner contract, then replays the archive into a throwaway SQLite
/// database, so every row is parsed and written exactly as a real import would.
/// </summary>
public static class ArchiveVerifier
{
    private static readonly string Owner = NetworkStorageServices.LocalOwnerUserId.ToString(CultureInfo.InvariantCulture);

    public static async Task<ImportResult> VerifyAsync(string archivePath, CancellationToken ct)
    {
        await using (var archive = OpenRead(archivePath))
        {
            await CheckContractAsync(archive, ct);
        }

        var scratch = Directory.CreateTempSubdirectory("sbox-ns-verify-").FullName;
        try
        {
            var config = ConfigLoader.Load(Path.Combine(scratch, "config"), Path.Combine(scratch, "data"), environment: _ => null);
            await using var store = new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = Path.Combine(scratch, "data", "verify.db") });
            await store.MigrateAsync(ct);
            await using var archive = OpenRead(archivePath);
            return await ServerArchive.ImportAsync(archive, store, config, new ImportOptions(Force: false, RestoreConfig: false), ct);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ServerArchive.DeleteQuietly(scratch);
        }
    }

    /// <summary>
    /// Owner and path rules a self-hosted server relies on: every project has its project row and an
    /// owner membership for user 1; memberships and API keys belong to user 1; workspace objects
    /// live under <c>network-storage/</c>, user folders only under <c>network-storage/users/1/</c>,
    /// and project folders only for projects in the manifest.
    /// </summary>
    internal static async Task CheckContractAsync(Stream archive, CancellationToken ct)
    {
        ExportManifest? manifest = null;
        HashSet<string> projects = [];
        HashSet<string> owned = [];
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gzip, leaveOpen: true);
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (manifest is null)
                {
                    if (entry.Name != ExportFormat.ManifestEntry)
                    {
                        throw new ExportArchiveException($"Not an sbox-ns export: the first entry must be {ExportFormat.ManifestEntry}.");
                    }

                    manifest = await ServerArchive.ReadManifestAsync(entry.DataStream, ct);
                    _ = ServerArchive.ExpectedDataCounts(manifest, out _);
                    projects = manifest.Projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var project in manifest.Projects)
                    {
                        if (project.Counts is null || !project.Counts.TryGetValue("project", out var rows) || rows != 1)
                        {
                            throw new ExportArchiveException($"Project '{project.Id}' must have exactly one row in {ExportFormat.ProjectEntry(project.Id, "project")}.");
                        }
                    }

                    continue;
                }

                if (entry.DataStream is null)
                {
                    continue;
                }

                if (entry.Name == ExportFormat.MembershipsEntry)
                {
                    await ForEachRowAsync(entry, ct, row =>
                    {
                        var projectId = Text(row, "project_id");
                        if (Text(row, "user_id") != Owner || projectId is null || !projects.Contains(projectId))
                        {
                            throw new ExportArchiveException($"{entry.Name}: memberships must have user_id \"{Owner}\" and a project_id listed in the manifest (got user {Text(row, "user_id")}, project {projectId}).");
                        }

                        owned.Add(projectId);
                    });
                }
                else if (entry.Name == ExportFormat.WorkspaceObjectsEntry)
                {
                    await ForEachRowAsync(entry, ct, row => CheckWorkspacePath(Text(row, "path"), projects));
                }
                else if (entry.Name.EndsWith("/api-keys.jsonl", StringComparison.Ordinal))
                {
                    await ForEachRowAsync(entry, ct, row =>
                    {
                        if (ExportFormat.ApiKeyRowProblem(row, Owner) is { } problem)
                        {
                            throw new ExportArchiveException($"{entry.Name}: {problem}");
                        }
                    });
                }
                else if (entry.Name.EndsWith("/project.jsonl", StringComparison.Ordinal))
                {
                    await ForEachRowAsync(entry, ct, row =>
                    {
                        var payload = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("payload", out var p) ? p : default;
                        foreach (var name in new[] { "storageOwnerUserId", "storage_owner_user_id" })
                        {
                            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var owner)
                                && owner.ValueKind != JsonValueKind.Null && owner.ToString() != Owner)
                            {
                                throw new ExportArchiveException($"{entry.Name}: payload.{name} must be \"{Owner}\" or absent (got {owner}).");
                            }
                        }
                    });
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or JsonException or System.Text.DecoderFallbackException)
        {
            throw new ExportArchiveException($"The archive is damaged or not a .tar.gz file ({ex.Message}).");
        }

        if (manifest is null)
        {
            throw new ExportArchiveException("Not an sbox-ns export: the archive is empty.");
        }

        var unowned = projects.Where(p => !owned.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (unowned.Count > 0)
        {
            throw new ExportArchiveException($"{ExportFormat.MembershipsEntry} has no owner membership (user_id \"{Owner}\") for: {string.Join(", ", unowned)}.");
        }
    }

    private static void CheckWorkspacePath(string? path, HashSet<string> projects)
    {
        var segments = path?.Split('/') ?? [];
        if (path is null || segments.Length < 2 || segments[0] is not ("network-storage" or "network-storage-api" or "server") || path.Contains('\\')
            || segments.Any(s => s.Length == 0 || s is "." or ".."))
        {
            throw new ExportArchiveException($"{ExportFormat.WorkspaceObjectsEntry}: workspace path '{path}' must be a plain path under network-storage/ (or server/ for the owner login).");
        }

        if (segments[0] == "server")
        {
            return;
        }
        if (segments[0] == "network-storage-api" && (segments.Length < 5 || segments[1] != "keys" || segments[2] != "projects"))
        {
            throw new ExportArchiveException($"{ExportFormat.WorkspaceObjectsEntry}: '{path}' must be a project key file under network-storage-api/keys/projects/.");
        }

        // network-storage/users/<owner>/<projectId>/... and network-storage/keys/projects/<projectId>/...
        var projectId = segments[1] switch
        {
            "users" when segments.Length > 2 && segments[2] != Owner
                => throw new ExportArchiveException($"{ExportFormat.WorkspaceObjectsEntry}: '{path}' is not under network-storage/users/{Owner}/ (rewrite hosted user folders to the local owner {Owner})."),
            "users" when segments.Length > 4 => segments[3],
            "keys" when segments.Length > 3 && segments[2] == "projects" => segments[3],
            _ => null
        };
        if (projectId is not null && !projects.Contains(projectId))
        {
            throw new ExportArchiveException($"{ExportFormat.WorkspaceObjectsEntry}: '{path}' belongs to project '{projectId}', which is not in the manifest.");
        }
    }

    private static async Task ForEachRowAsync(TarEntry entry, CancellationToken ct, Action<JsonElement> check)
    {
        await foreach (var line in ArchiveLines.ReadAsync(entry.DataStream!, entry.Name, ct))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            check(document.RootElement);
        }
    }

    private static string? Text(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
}
