using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Operations;

public sealed record SnapshotTableEvidence(long Rows, string Sha256);
public sealed record AuthoritativeProjectManifest(string Format, int FormatVersion, string ProjectId,
    long FenceGeneration, string SharedSecretsFingerprint, IReadOnlyDictionary<string, SnapshotTableEvidence> Tables);

/// <summary>
/// Canonical lossless project archive v2. Every table is explicitly present, including
/// empty tables. Exact row values (JSON columns remain raw text), orphan rows, deleted
/// records and all historical sessions are retained. No config or website session is exported.
/// The caller must hold a drained fence throughout export/import and audit.
/// </summary>
public static class AuthoritativeProjectArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Format = "sbox-ns-export";

    public static async Task<AuthoritativeProjectManifest> ExportAsync(string projectId, long fenceGeneration,
        string sharedSecretsFingerprint, IAsyncEnumerable<ProjectSnapshotRow> rows, Stream destination, CancellationToken ct)
    {
        ValidateBinding(projectId, fenceGeneration, sharedSecretsFingerprint);
        var staging = CreatePrivateDirectory();
        try
        {
            var writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
            var hashes = ProjectSnapshotSchema.Tables.Keys.ToDictionary(t => t, _ => new List<string>(), StringComparer.Ordinal);
            try
            {
                foreach (var table in hashes.Keys)
                    writers.Add(table, new StreamWriter(new FileStream(Path.Combine(staging, table), FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)));
                await foreach (var snapshot in rows.WithCancellation(ct))
                {
                    ProjectSnapshotSchema.Validate(projectId, snapshot.Table, snapshot.Row);
                    var line = CanonicalRow(snapshot.Row);
                    await writers[snapshot.Table].WriteLineAsync(line.AsMemory(), ct);
                    hashes[snapshot.Table].Add(Hash(Encoding.UTF8.GetBytes(line)));
                }
            }
            finally { foreach (var writer in writers.Values) await writer.DisposeAsync(); }
            var evidence = hashes.ToDictionary(p => p.Key, p => Evidence(p.Value), StringComparer.Ordinal);
            var manifest = new AuthoritativeProjectManifest(Format, 2, projectId, fenceGeneration, sharedSecretsFingerprint, evidence);
            await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, true);
            await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, true);
            using var manifestBytes = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = manifestBytes }, ct);
            foreach (var table in evidence.Keys.Order(StringComparer.Ordinal))
            {
                await using var file = File.OpenRead(Path.Combine(staging, table));
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, Entry(projectId, table)) { DataStream = file }, ct);
            }
            return manifest;
        }
        finally { Directory.Delete(staging, true); }
    }

    /// <summary>Validates the whole archive before touching storage; replacement deletes extra destination rows atomically.</summary>
    public static async Task<AuthoritativeProjectManifest> ImportAsync(Stream archive, IAuthoritativeProjectStore target,
        string projectId, long fenceGeneration, string sharedSecretsFingerprint, CancellationToken ct)
    {
        ValidateBinding(projectId, fenceGeneration, sharedSecretsFingerprint);
        var staging = CreatePrivateDirectory();
        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, true);
            await using var tar = new TarReader(gzip, true);
            var first = await tar.GetNextEntryAsync(false, ct);
            if (first is null || first.Name != "manifest.json" || first.EntryType != TarEntryType.RegularFile || first.DataStream is null)
                throw new InvalidOperationException("Manifest must be the first regular archive entry.");
            var manifest = await JsonSerializer.DeserializeAsync<AuthoritativeProjectManifest>(first.DataStream, Json, ct)
                ?? throw new InvalidOperationException("Missing archive manifest.");
            if (manifest.Format != Format || manifest.FormatVersion != 2 || manifest.ProjectId != projectId
                || manifest.FenceGeneration != fenceGeneration || manifest.SharedSecretsFingerprint != sharedSecretsFingerprint
                || manifest.Tables is null || !manifest.Tables.Keys.Order(StringComparer.Ordinal).SequenceEqual(ProjectSnapshotSchema.Tables.Keys.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("Archive binding or full table coverage does not match.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await tar.GetNextEntryAsync(false, ct) is { } entry)
            {
                var table = manifest.Tables.Keys.SingleOrDefault(t => entry.Name == Entry(projectId, t));
                if (table is null || !seen.Add(table) || entry.EntryType != TarEntryType.RegularFile)
                    throw new InvalidOperationException("Unexpected, unsafe or duplicate archive entry.");
                var hashes = new List<string>();
                await using var output = new StreamWriter(new FileStream(Path.Combine(staging, table), FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
                if (entry.DataStream is not null)
                {
                    using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, false, leaveOpen: true);
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        using var document = JsonDocument.Parse(line);
                        ProjectSnapshotSchema.Validate(projectId, table, document.RootElement);
                        var canonical = CanonicalRow(document.RootElement);
                        hashes.Add(Hash(Encoding.UTF8.GetBytes(canonical)));
                        await output.WriteLineAsync(canonical.AsMemory(), ct);
                    }
                }
                if (Evidence(hashes) != manifest.Tables[table]) throw new InvalidOperationException("Archive content checksum mismatch.");
            }
            if (seen.Count != manifest.Tables.Count) throw new InvalidOperationException("Archive is missing a table, including an explicitly empty table.");
            await target.ReplaceProjectRowsAsync(projectId, ReadRowsAsync(staging, ct), ct);
            var audit = await AuditAsync(target.ExportProjectRowsAsync(projectId, ct), projectId, ct);
            if (!Equal(manifest.Tables, audit)) throw new InvalidOperationException("Destination content audit failed; keep both authorities fenced.");
            return manifest;
        }
        finally { Directory.Delete(staging, true); }
    }

    public static async Task<IReadOnlyDictionary<string, SnapshotTableEvidence>> AuditAsync(
        IAsyncEnumerable<ProjectSnapshotRow> rows, string projectId, CancellationToken ct)
    {
        var hashes = ProjectSnapshotSchema.Tables.Keys.ToDictionary(t => t, _ => new List<string>(), StringComparer.Ordinal);
        await foreach (var snapshot in rows.WithCancellation(ct))
        {
            ProjectSnapshotSchema.Validate(projectId, snapshot.Table, snapshot.Row);
            hashes[snapshot.Table].Add(Hash(Encoding.UTF8.GetBytes(CanonicalRow(snapshot.Row))));
        }
        return hashes.ToDictionary(p => p.Key, p => Evidence(p.Value), StringComparer.Ordinal);
    }

    public static bool Equal(IReadOnlyDictionary<string, SnapshotTableEvidence> source, IReadOnlyDictionary<string, SnapshotTableEvidence> target)
        => source.Count == ProjectSnapshotSchema.Tables.Count && target.Count == source.Count
            && source.All(p => target.TryGetValue(p.Key, out var evidence) && p.Value == evidence);

    private static async IAsyncEnumerable<ProjectSnapshotRow> ReadRowsAsync(string directory, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var table in ProjectSnapshotSchema.Tables.Keys)
        {
            using var reader = new StreamReader(Path.Combine(directory, table), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                yield return new ProjectSnapshotRow(table, document.RootElement.Clone());
            }
        }
    }

    private static string CanonicalRow(JsonElement row)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in row.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    }
    private static SnapshotTableEvidence Evidence(List<string> hashes)
    {
        hashes.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in hashes) hash.AppendData(Encoding.ASCII.GetBytes(value));
        return new(hashes.Count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Entry(string projectId, string table) => $"data/projects/{projectId}/{table}.jsonl";
    private static void ValidateBinding(string projectId, long generation, string fingerprint)
    {
        if (string.IsNullOrEmpty(projectId) || projectId.Length > 128 || projectId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            || generation <= 0 || fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Project, fence generation and actual shared-secret SHA256 binding are required.");
    }
    private static string CreatePrivateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sbox-ns-snapshot-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
