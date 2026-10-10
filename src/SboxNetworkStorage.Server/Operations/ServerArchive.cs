using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Whole-server export/import: one <c>.tar.gz</c> holding a manifest, a driver-neutral
/// data dump and the config folder. Exports read and imports write only through
/// <see cref="INetworkStorageStore"/>, so an archive moves between SQLite and PostgreSQL.
/// </summary>
public static class ServerArchive
{
    private const string StagingDirectory = "tmp";
    private const string StagingPrefix = "export-";
    private const int MaxConfigFileBytes = 1024 * 1024;
    private const string LocalOwnerUserId = "1";

    /// <summary>
    /// Dumps the store and config into a private staging folder under the data
    /// directory. Nothing is sent anywhere until <see cref="StagedExport.WriteToAsync"/>,
    /// so callers can fail cleanly before committing to an output.
    /// </summary>
    public static async Task<StagedExport> PrepareExportAsync(INetworkStorageStore store, INetworkStorageStoreAdmin admin,
        EffectiveConfig config, bool includeSecrets, CancellationToken ct)
    {
        var staging = CreateStagingDirectory(config);
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var dump = new StoreDumpWriter(store, staging);
            var (workspaceObjects, memberships, projects) = await dump.WriteAsync(ct);
            var stagedConfig = ConfigArchive.Stage(config, includeSecrets, staging);
            var manifest = new ExportManifest(ExportFormat.FormatName, ExportFormat.CurrentVersion, BuildInfo.Version,
                await admin.GetSchemaVersionAsync(ct), admin.ProviderName, createdAt, IncludesConfig: true, includeSecrets,
                workspaceObjects, memberships, projects);
            await File.WriteAllBytesAsync(Path.Combine(staging, ExportFormat.ManifestEntry),
                JsonSerializer.SerializeToUtf8Bytes(manifest, ExportFormat.ManifestJson), ct);

            IReadOnlyList<string> entries = [ExportFormat.ManifestEntry, .. dump.Entries, .. stagedConfig.Entries];
            return new StagedExport(staging, manifest, entries, stagedConfig.SecretFilesOutsideConfig);
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }
    }

    /// <summary>Produces a portable project archive, excluding server configuration and other projects.</summary>
    public static async Task<StagedExport> PrepareProjectExportAsync(INetworkStorageStore store, INetworkStorageStoreAdmin admin,
        EffectiveConfig config, string projectId, CancellationToken ct)
    {
        var staging = CreateStagingDirectory(config);
        try
        {
            var dump = new StoreDumpWriter(store, staging);
            var (objects, memberships, projects) = await dump.WriteProjectAsync(projectId, ct);
            var manifest = new ExportManifest(ExportFormat.FormatName, ExportFormat.CurrentVersion, BuildInfo.Version,
                await admin.GetSchemaVersionAsync(ct), admin.ProviderName, DateTimeOffset.UtcNow,
                IncludesConfig: false, IncludesSecrets: false, objects, memberships, projects);
            await File.WriteAllBytesAsync(Path.Combine(staging, ExportFormat.ManifestEntry),
                JsonSerializer.SerializeToUtf8Bytes(manifest, ExportFormat.ManifestJson), ct);
            return new StagedExport(staging, manifest, [ExportFormat.ManifestEntry, .. dump.Entries], []);
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }
    }

    /// <summary>
    /// Restores an archive into <paramref name="store"/> (the currently configured
    /// database). Refuses a newer archive format, and a target that already holds
    /// projects unless <see cref="ImportOptions.Force"/>. Rows are upserted, so a
    /// repeated import converges to the same state.
    /// </summary>
    public static Task<ImportResult> ImportAsync(Stream archive, INetworkStorageStore store, EffectiveConfig config,
        ImportOptions options, CancellationToken ct)
        => ImportCoreAsync(archive, store, config, options, projectId: null, ct);

    internal static Task<ImportResult> ImportProjectAsync(Stream archive, INetworkStorageStore store,
        EffectiveConfig config, string projectId, CancellationToken ct)
        => ImportCoreAsync(archive, store, config, new ImportOptions(Force: true, RestoreConfig: false), projectId, ct);

    private static async Task<ImportResult> ImportCoreAsync(Stream archive, INetworkStorageStore store,
        EffectiveConfig config, ImportOptions options, string? projectId, CancellationToken ct)
    {
        var staging = CreateStagingDirectory(config);
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gzip, leaveOpen: true);
            ExportManifest? manifest = null;
            Dictionary<string, long>? expectedCounts = null;
            HashSet<string>? requiredEntries = null;
            var dataEntries = new List<(string Name, string Path)>();
            var configFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
                {
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        continue;
                    }

                    var name = ValidateEntryName(entry);
                    if (!seen.Add(name))
                    {
                        throw new ExportArchiveException($"Archive contains '{name}' twice.");
                    }

                    if (manifest is null)
                    {
                        if (name != ExportFormat.ManifestEntry)
                        {
                            throw new ExportArchiveException($"Not an sbox-ns export: the first entry must be {ExportFormat.ManifestEntry}.");
                        }

                        manifest = await ReadManifestAsync(entry.DataStream, ct);
                        expectedCounts = ExpectedDataCounts(manifest, out requiredEntries);
                        await EnsureTargetAcceptsAsync(store, manifest, options.Force, ct);
                        continue;
                    }

                    if (name.StartsWith(ExportFormat.DataPrefix, StringComparison.Ordinal))
                    {
                        if (!expectedCounts!.TryGetValue(name, out var expected))
                        {
                            throw new ExportArchiveException($"Unexpected archive entry '{name}': it is not declared in the manifest.");
                        }

                        var path = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
                        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                            64 * 1024, useAsync: true);
                        if (entry.DataStream is { } content)
                        {
                            await content.CopyToAsync(file, ct);
                        }

                        file.Position = 0;
                        var actual = await CountDataRowsAsync(name, file, ct);
                        if (actual != expected)
                        {
                            throw new ExportArchiveException($"Archive entry '{name}' contains {actual} rows; the manifest declares {expected}.");
                        }

                        dataEntries.Add((name, path));
                    }
                    else if (name.StartsWith(ExportFormat.ConfigPrefix, StringComparison.Ordinal) && name.Length > ExportFormat.ConfigPrefix.Length)
                    {
                        configFiles[name[ExportFormat.ConfigPrefix.Length..]] = await ReadConfigFileAsync(name, entry, ct);
                    }
                    else
                    {
                        throw new ExportArchiveException($"Unexpected archive entry '{name}'.");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
            {
                throw new ExportArchiveException($"The archive is damaged or not a .tar.gz file ({ex.Message}).");
            }

            if (manifest is null)
            {
                throw new ExportArchiveException("Not an sbox-ns export: the archive is empty.");
            }

            foreach (var name in requiredEntries!)
            {
                if (!seen.Contains(name))
                {
                    throw new ExportArchiveException($"Archive is missing manifest-declared data entry '{name}'.");
                }
            }

            // Only replay after the entire dump matches every manifest-declared entry and row count.
            long rows = 0;
            async Task ReplayAsync(INetworkStorageStore target, CancellationToken replayCt)
            {
                var reader = new StoreDumpReader(target);
                foreach (var (name, path) in dataEntries)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, useAsync: true);
                    await reader.ApplyEntryAsync(name, file, replayCt);
                }
                rows = reader.Rows;
            }

            if (projectId is not null)
            {
                if (store is not IProjectImportStore importer)
                    throw new ExportArchiveException("This store does not support atomic project imports.");
                if (!await importer.TryImportProjectAsync(projectId, ReplayAsync, ct))
                    throw new ExportArchiveException("That project ID already exists. Import on a server where it does not exist; existing projects are never overwritten.");
            }
            else
            {
                await ReplayAsync(store, ct);
            }

            IReadOnlyList<string> restoredConfig = options.RestoreConfig ? ConfigArchive.Restore(config, configFiles) : [];
            return new ImportResult(manifest, rows, configFiles.Count, restoredConfig);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>
    /// Reads and validates <c>manifest.json</c>; newer format versions are refused
    /// with an upgrade hint because their dump files may not mean what this build expects.
    /// </summary>
    public static async Task<ExportManifest> ReadManifestAsync(Stream? content, CancellationToken ct)
    {
        if (content is null)
        {
            throw new ExportArchiveException($"Not an sbox-ns export: {ExportFormat.ManifestEntry} is empty.");
        }

        JsonElement root;
        try
        {
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ExportArchiveException($"Not an sbox-ns export: {ExportFormat.ManifestEntry} is not valid JSON ({ex.Message}).");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ExportFormat.FormatName
            || !root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var formatVersion) || formatVersion < 1)
        {
            throw new ExportArchiveException($"Not an sbox-ns export: {ExportFormat.ManifestEntry} has no valid format/formatVersion.");
        }

        if (formatVersion > ExportFormat.CurrentVersion)
        {
            var producer = root.TryGetProperty("sboxNsVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "a newer sbox-ns";
            throw new ExportArchiveException(
                $"This archive uses export format version {formatVersion} (written by sbox-ns {producer}), but sbox-ns {BuildInfo.Version} "
                + $"reads format version {ExportFormat.CurrentVersion} at most. Upgrade sbox-ns on this server (sbox-ns update), then import again.");
        }

        try
        {
            return root.Deserialize<ExportManifest>(ExportFormat.ManifestJson)
                ?? throw new ExportArchiveException($"{ExportFormat.ManifestEntry} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ExportArchiveException($"{ExportFormat.ManifestEntry} is invalid ({ex.Message}).");
        }
    }

    internal static Dictionary<string, long> ExpectedDataCounts(ExportManifest manifest, out HashSet<string> requiredEntries)
    {
        if (manifest.WorkspaceObjects < 0 || manifest.Memberships < 0 || manifest.Projects is null)
        {
            throw new ExportArchiveException("The manifest has invalid data counts or projects.");
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [ExportFormat.WorkspaceObjectsEntry] = manifest.WorkspaceObjects,
            [ExportFormat.MembershipsEntry] = manifest.Memberships,
        };
        requiredEntries = new HashSet<string>(counts.Where(entry => entry.Value > 0).Select(entry => entry.Key), StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in manifest.Projects)
        {
            if (project is null || !ExportFormat.IsValidProjectId(project.Id) || !projects.Add(project.Id) || project.Counts is null)
            {
                throw new ExportArchiveException("The manifest has an invalid or duplicate project.");
            }

            // Empty resources have no export entry or count; accept explicit empty files too.
            foreach (var resource in ExportFormat.ProjectResources)
            {
                counts.Add(ExportFormat.ProjectEntry(project.Id, resource), 0);
            }

            foreach (var (resource, count) in project.Counts)
            {
                if (!ExportFormat.ProjectResources.Contains(resource) || count < 0)
                {
                    throw new ExportArchiveException($"The manifest has an invalid resource count for project '{project.Id}': '{resource}'.");
                }

                var name = ExportFormat.ProjectEntry(project.Id, resource);
                counts[name] = count;
                if (count > 0) requiredEntries.Add(name);
            }
        }

        return counts;
    }

    private static async Task<long> CountDataRowsAsync(string name, Stream content, CancellationToken ct)
    {
        long count = 0;
        long lineNumber = 0;
        await foreach (var line in ArchiveLines.ReadAsync(content, name, ct))
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ExportArchiveException($"{name}:{lineNumber}: invalid row (expected a JSON object).");
                }
            }
            catch (JsonException ex)
            {
                throw new ExportArchiveException($"{name}:{lineNumber}: invalid row ({ex.Message}).");
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// A target is non-empty when the local owner already has projects, any project
    /// workspace object exists, or any archived project id is already present. The
    /// owner login alone does not count, so a freshly set-up server accepts an import.
    /// </summary>
    private static async Task EnsureTargetAcceptsAsync(INetworkStorageStore store, ExportManifest manifest, bool force, CancellationToken ct)
    {
        if (force)
        {
            return;
        }

        var existing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var membership in await store.ListProjectsForUserAsync(LocalOwnerUserId, ct))
        {
            existing.Add(StoreDumpWriter.Text(membership, "project_id") ?? "?");
        }

        foreach (var project in manifest.Projects.Where(p => ExportFormat.IsValidProjectId(p.Id)))
        {
            if (await store.ReadProjectAsync(project.Id, ct) is not null)
            {
                existing.Add(project.Id);
            }
        }

        var hasWorkspaceData = (await store.ListWorkspaceObjectsAsync("network-storage", ct)).Count > 0;
        if (existing.Count > 0 || hasWorkspaceData)
        {
            var detail = existing.Count > 0 ? $"projects {string.Join(", ", existing)}" : "project data";
            throw new ExportArchiveException(
                $"The target database already contains {detail}. Import into an empty database, or pass --force to merge: "
                + "rows with the same keys are overwritten by the archive and every other row is kept.");
        }
    }

    private static string ValidateEntryName(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw new ExportArchiveException($"Archive entry '{entry.Name}' is a {entry.EntryType}; only regular files are allowed.");
        }

        var name = entry.Name;
        var segments = name.Split('/');
        if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/')
            || segments.Any(s => s.Length == 0 || s is "." or ".."))
        {
            throw new ExportArchiveException($"Archive entry '{name}' has an unsafe path.");
        }

        return name;
    }

    private static async Task<byte[]> ReadConfigFileAsync(string name, TarEntry entry, CancellationToken ct)
    {
        if (entry.Length > MaxConfigFileBytes)
        {
            throw new ExportArchiveException($"Archive config file '{name}' is larger than {MaxConfigFileBytes} bytes.");
        }

        if (entry.DataStream is null)
        {
            return [];
        }

        using var buffer = new MemoryStream((int)entry.Length);
        await entry.DataStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static string CreateStagingDirectory(EffectiveConfig config)
    {
        var parent = Path.Combine(config.DataDirectory, StagingDirectory);
        CreatePrivateDirectory(parent);
        RemoveStaleStaging(parent);
        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(staging);
        return staging;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Staging folders left by a crashed export still hold player data; remove them after a day.</summary>
    private static void RemoveStaleStaging(string parent)
    {
        foreach (var directory in Directory.EnumerateDirectories(parent, StagingPrefix + "*"))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
            {
                DeleteQuietly(directory);
            }
        }
    }

    internal static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record ImportOptions(bool Force, bool RestoreConfig);

/// <summary>Outcome of an import: the archive manifest, rows applied, config files present/written.</summary>
public sealed record ImportResult(ExportManifest Manifest, long RowsApplied, int ConfigFilesInArchive, IReadOnlyList<string> ConfigFilesWritten);

/// <summary>A prepared export in a private staging folder; disposing deletes the folder.</summary>
public sealed class StagedExport(string staging, ExportManifest manifest, IReadOnlyList<string> entries,
    IReadOnlyList<string> secretFilesOutsideConfig) : IAsyncDisposable
{
    public ExportManifest Manifest { get; } = manifest;

    /// <summary>Secret files referenced by the config but stored outside the config folder (not included).</summary>
    public IReadOnlyList<string> SecretFilesOutsideConfig { get; } = secretFilesOutsideConfig;

    /// <summary>Writes the archive (manifest, data, config) as gzip-compressed PAX tar. Uses only async writes, so it can stream to an HTTP response.</summary>
    public async Task WriteToAsync(Stream destination, CancellationToken ct)
    {
        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                await using var data = new FileStream(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)),
                    FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = data,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    ModificationTime = Manifest.CreatedAt,
                };
                await tar.WriteEntryAsync(entry, ct);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        ServerArchive.DeleteQuietly(staging);
        return ValueTask.CompletedTask;
    }
}
