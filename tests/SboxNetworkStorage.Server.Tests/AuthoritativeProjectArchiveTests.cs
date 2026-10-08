using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Owns temporary SQLite stores only; never starts Program or reads configuration/secrets.</summary>
public sealed class AuthoritativeProjectArchiveTests
{
    private const string Project = "hosted_fixture";
    private static readonly string Secrets = new('a', 64);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task FullHostedLikeSnapshotAndReverseReplacementPreserveAcceptedWritesAndDeleteExtras()
    {
        var root = Path.Combine(Path.GetTempPath(), "snapshot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var hosted = await NewStoreAsync(Path.Combine(root, "hosted.db"));
            await using var oss = await NewStoreAsync(Path.Combine(root, "oss.db"));
            await hosted.ReplaceProjectRowsAsync(Project, FixtureRows(), Ct);
            await oss.UpsertRecordAsync(Project, "extra", "extra", Element(new { stale = true }), false, 1, Ct);
            using var initial = new MemoryStream();
            var exported = await AuthoritativeProjectArchive.ExportAsync(Project, 1, Secrets, hosted.ExportProjectRowsAsync(Project, Ct), initial, Ct);
            Assert.Equal(28, exported.Tables.Count);
            Assert.All(exported.Tables, table => Assert.Equal(1, table.Value.Rows));
            initial.Position = 0;
            await AuthoritativeProjectArchive.ImportAsync(initial, oss, Project, 1, Secrets, Ct);
            Assert.Null(await oss.ReadRecordAsync(Project, "extra", "extra", Ct));
            var initialAudit = await AuthoritativeProjectArchive.AuditAsync(oss.ExportProjectRowsAsync(Project, Ct), Project, Ct);
            Assert.True(AuthoritativeProjectArchive.Equal(exported.Tables, initialAudit));
            await oss.UpsertRecordAsync(Project, "inventory", "accepted_after_cutover", Element(new { gold = 19 }), false, 4, Ct);
            await oss.DeleteRecordAsync(Project, "inventory", "deleted_after_cutover", Ct);
            using var reverse = new MemoryStream();
            var reverseManifest = await AuthoritativeProjectArchive.ExportAsync(Project, 2, Secrets, oss.ExportProjectRowsAsync(Project, Ct), reverse, Ct);
            reverse.Position = 0;
            await AuthoritativeProjectArchive.ImportAsync(reverse, hosted, Project, 2, Secrets, Ct);
            Assert.Equal(19, (await hosted.ReadRecordAsync(Project, "inventory", "accepted_after_cutover", Ct))!.Value.GetProperty("payload_json").GetProperty("gold").GetInt32());
            Assert.True(AuthoritativeProjectArchive.Equal(reverseManifest.Tables,
                await AuthoritativeProjectArchive.AuditAsync(hosted.ExportProjectRowsAsync(Project, Ct), Project, Ct)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("secrets")]
    [InlineData("generation")]
    public async Task MissingOrChangedDataAndIncorrectBindingsNeverModifyDestination(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "snapshot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await NewStoreAsync(Path.Combine(root, "source.db"));
            await using var target = await NewStoreAsync(Path.Combine(root, "target.db"));
            await source.ReplaceProjectRowsAsync(Project, FixtureRows(), Ct);
            await target.UpsertProjectAsync(Project, Element(new { name = "keep" }), 8, Ct);
            var before = await AuthoritativeProjectArchive.AuditAsync(target.ExportProjectRowsAsync(Project, Ct), Project, Ct);
            using var good = new MemoryStream();
            await AuthoritativeProjectArchive.ExportAsync(Project, 1, Secrets, source.ExportProjectRowsAsync(Project, Ct), good, Ct);
            good.Position = 0;
            using var bad = new MemoryStream();
            using (var decompress = new GZipStream(good, CompressionMode.Decompress, true))
            using (var reader = new TarReader(decompress, true))
            using (var compress = new GZipStream(bad, CompressionLevel.Optimal, true))
            using (var writer = new TarWriter(compress, TarEntryFormat.Pax, true))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    if (failure == "missing" && entry.Name.EndsWith("/record_idempotency.jsonl", StringComparison.Ordinal)) continue;
                    using var content = new MemoryStream();
                    entry.DataStream?.CopyTo(content);
                    if (failure == "changed" && entry.Name.EndsWith("/records.jsonl", StringComparison.Ordinal))
                    {
                        content.SetLength(0);
                        var row = Row("records"); row["version"] = 999L;
                        JsonSerializer.Serialize(content, row); content.WriteByte((byte)'\n');
                    }
                    content.Position = 0;
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = content });
                }
            }
            bad.Position = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => AuthoritativeProjectArchive.ImportAsync(bad, target, Project,
                failure == "generation" ? 2 : 1, failure == "secrets" ? new string('b', 64) : Secrets, Ct));
            Assert.True(AuthoritativeProjectArchive.Equal(before, await AuthoritativeProjectArchive.AuditAsync(target.ExportProjectRowsAsync(Project, Ct), Project, Ct)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<SqliteNetworkStorageStore> NewStoreAsync(string path)
    {
        var store = new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = path });
        await store.MigrateAsync(Ct);
        return store;
    }
    private static Dictionary<string, object?> Row(string table) => ProjectSnapshotSchema.Tables[table].ToDictionary(
        column => column.Key, column => column.Key == "project_id" ? (object?)Project : column.Value switch
        {
            JsonValueKind.String => column.Key.EndsWith("_json", StringComparison.Ordinal) ? "{\"preserved\":true}" : "value",
            JsonValueKind.True => true,
            _ => 1L
        }, StringComparer.Ordinal);
    private static async IAsyncEnumerable<ProjectSnapshotRow> FixtureRows([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var table in ProjectSnapshotSchema.Tables.Keys)
        {
            ct.ThrowIfCancellationRequested();
            yield return new(table, Element(Row(table)));
        }
        await Task.CompletedTask;
    }
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
}
