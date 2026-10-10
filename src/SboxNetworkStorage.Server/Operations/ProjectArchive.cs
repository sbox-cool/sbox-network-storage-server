using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>Validates the project boundary before replaying an archive into a server with other projects.</summary>
public static class ProjectArchive
{
    public const long MaxUploadBytes = 64 * 1024 * 1024;
    private const long MaxExpandedBytes = 256 * 1024 * 1024;

    public static async Task<ImportResult> ImportAsync(Stream archive, INetworkStorageStore store,
        EffectiveConfig config, CancellationToken ct)
    {
        if (!archive.CanSeek || archive.Length > MaxUploadBytes)
            throw new ExportArchiveException("Project imports require a seekable archive no larger than 64 MiB.");
        var start = archive.Position;
        var manifest = await ValidateAsync(archive, ct);
        var projectId = manifest.Projects[0].Id;
        archive.Position = start;
        return await ServerArchive.ImportProjectAsync(archive, store, config, projectId, ct);
    }

    private static async Task<ExportManifest> ValidateAsync(Stream archive, CancellationToken ct)
    {
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gzip, leaveOpen: true);
            ExportManifest? manifest = null;
            long expanded = 0;
            while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new ExportArchiveException("Project archives may contain regular files only.");
                if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded)
                    throw new ExportArchiveException("Project archive exceeds the 256 MiB expanded-size limit.");
                expanded += entry.Length;
                if (manifest is null)
                {
                    if (entry.Name != ExportFormat.ManifestEntry || entry.Length > 1024 * 1024)
                        throw new ExportArchiveException("The first entry must be a manifest no larger than 1 MiB.");
                    manifest = await ServerArchive.ReadManifestAsync(entry.DataStream, ct);
                    if (manifest.IncludesConfig || manifest.IncludesSecrets || manifest.Projects.Count != 1
                        || !ExportFormat.IsValidProjectId(manifest.Projects[0].Id) || manifest.Memberships != 1
                        || !manifest.Projects[0].Counts.TryGetValue("project", out var projectRows) || projectRows != 1)
                        throw new ExportArchiveException("Choose a single-project export with its project and owner membership, without server configuration.");
                    continue;
                }
                var id = manifest.Projects[0].Id;
                var projectPrefix = ExportFormat.ProjectsPrefix + id + "/";
                var apiKeysEntry = ExportFormat.ProjectEntry(id, "api-keys");
                if (entry.Name == ExportFormat.WorkspaceObjectsEntry || entry.Name == ExportFormat.MembershipsEntry || entry.Name == apiKeysEntry)
                {
                    if (entry.DataStream is null) continue;
                    await foreach (var line in ArchiveLines.ReadAsync(entry.DataStream, entry.Name, ct))
                    {
                        if (line.Length == 0) continue;
                        using var doc = JsonDocument.Parse(line);
                        var row = doc.RootElement;
                        if (entry.Name == ExportFormat.MembershipsEntry)
                        {
                            if (row.GetProperty("project_id").GetString() != id
                                || row.GetProperty("user_id").GetString() != Owner
                                || row.GetProperty("role").GetString() != "owner")
                                throw new ExportArchiveException("Project archive contains an unrelated membership.");
                        }
                        else if (entry.Name == apiKeysEntry)
                        {
                            if (ExportFormat.ApiKeyRowProblem(row, Owner) is { } problem)
                                throw new ExportArchiveException($"Project archive contains an invalid API key: {problem}");
                        }
                        else
                        {
                            var path = row.GetProperty("path").GetString() ?? "";
                            var prefix = $"network-storage/users/{Owner}/{id}/";
                            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Contains('\\')
                                || path.Split('/').Any(segment => segment is ".." or "." or ""))
                                throw new ExportArchiveException("Project archive contains an unrelated workspace object.");
                        }
                    }
                }
                else if (!entry.Name.StartsWith(projectPrefix, StringComparison.Ordinal)
                    || !ExportFormat.ProjectResources.Any(resource => entry.Name == ExportFormat.ProjectEntry(id, resource)))
                    throw new ExportArchiveException("Project archive contains an unrelated entry.");
            }
            return manifest ?? throw new ExportArchiveException("Project archive is empty.");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or System.Text.DecoderFallbackException)
        {
            throw new ExportArchiveException($"Invalid project archive: {ex.Message}");
        }
    }

    private static readonly string Owner = NetworkStorageServices.LocalOwnerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
