using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Whole-server export/import (<c>sbox-ns export</c>/<c>import</c>, <c>POST /dashboard/export</c>):
/// a round trip into a fresh database reproduces every exported resource, non-empty targets and
/// newer archive formats are refused, and secrets stay out unless requested. Each test owns its host.
/// </summary>
public abstract class ServerExportImportTests<TFactory> : IDisposable
    where TFactory : SelfHostFactory, new()
{
    private const string PlayerA = "76561198000000001";
    private const string PlayerB = "76561198000000002";
    private const string Collection = "inventory";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly TFactory factory = new();

    protected ServerExportImportTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);

    public void Dispose() => factory.Dispose();

    private INetworkStorageStore Source => factory.Services.GetRequiredService<INetworkStorageStore>();

    [SkippableFact]
    public async Task RoundTripIntoFreshDatabaseReproducesEveryResource()
    {
        var projectId = await SeedAsync();
        var archive = await ExportAsync(Source, factory.Config, includeSecrets: false);
        var target = await factory.NewStoreAsync();

        var result = await ImportAsync(archive, target, factory.Config, new ImportOptions(Force: false, RestoreConfig: false));

        Assert.Equal(ExportFormat.CurrentVersion, result.Manifest.FormatVersion);
        Assert.Contains(result.Manifest.Projects, p => p.Id == projectId && p.Counts["records"] == 2);
        Assert.Equal(result.Manifest.TotalRows, result.RowsApplied);
        await AssertSameDataAsync(Source, target, projectId);
    }

    [SkippableFact]
    public async Task RoundTripCarriesTheLegacyPlayerProjectionsFlag()
    {
        var projectId = await SeedAsync();
        var payload = JsonNode.Parse((await Source.ReadProjectAsync(projectId, Ct))!.Value.GetRawText())!.AsObject();
        payload["legacyPlayerProjections"] = true;
        await Source.UpsertProjectAsync(projectId, Json(payload), 1, Ct);
        var archive = await ExportAsync(Source, factory.Config, includeSecrets: false);
        var target = await factory.NewStoreAsync();

        await ImportAsync(archive, target, factory.Config, new ImportOptions(Force: false, RestoreConfig: false));

        Assert.True((await target.ReadProjectAsync(projectId, Ct))!.Value.GetProperty("legacyPlayerProjections").GetBoolean());
    }

    [SkippableFact]
    public async Task ProjectArchivePreservesOtherProjectsAndRejectsOverwrite()
    {
        var id = await SeedAsync();
        var other = await factory.CreateProjectAsync("Unrelated source");
        await Source.PutWorkspaceObjectAsync($"network-storage/users/1/{id}/authoring.yml", "collections: {}", Ct);
        await Source.PutWorkspaceObjectAsync($"network-storage/users/1/{other.ProjectId}/private.json", "private", Ct);
        var admin = factory.Services.GetRequiredService<INetworkStorageStoreAdmin>();
        await using var staged = await ServerArchive.PrepareProjectExportAsync(Source, admin, factory.Config, id, Ct);
        using var archive = new MemoryStream();
        await staged.WriteToAsync(archive, Ct);
        var target = await factory.NewStoreAsync();
        await target.UpsertProjectAsync("existing", Json(new { name = "Keep me" }), 1, Ct);
        archive.Position = 0;
        await ProjectArchive.ImportAsync(archive, target, factory.Config, Ct);
        Assert.NotNull(await target.ReadRecordAsync(id, Collection, PlayerA, Ct));
        Assert.Null(await target.ReadProjectAsync(other.ProjectId, Ct));
        Assert.Equal("collections: {}", await target.ReadWorkspaceObjectAsync($"network-storage/users/1/{id}/authoring.yml", Ct));
        Assert.NotNull(await target.ReadProjectAsync("existing", Ct));
        archive.Position = 0;
        await Assert.ThrowsAsync<ExportArchiveException>(() => ProjectArchive.ImportAsync(archive, target, factory.Config, Ct));
    }

    [SkippableFact]
    public async Task ProjectArchiveRejectsCrossProjectWorkspaceBeforeWriting()
    {
        var id = await SeedAsync();
        var admin = factory.Services.GetRequiredService<INetworkStorageStoreAdmin>();
        await using var staged = await ServerArchive.PrepareProjectExportAsync(Source, admin, factory.Config, id, Ct);
        using var buffer = new MemoryStream();
        await staged.WriteToAsync(buffer, Ct);
        var entries = ReadArchive(buffer.ToArray());
        entries[ExportFormat.WorkspaceObjectsEntry] = Encoding.UTF8.GetBytes(
            "{\"path\":\"network-storage/users/1/projects.json\",\"content\":\"[]\"}\n");
        using var archive = new MemoryStream(BuildArchive(entries.Select(entry => (entry.Key, entry.Value)).ToArray()));
        var target = await factory.NewStoreAsync();
        await Assert.ThrowsAsync<ExportArchiveException>(() => ProjectArchive.ImportAsync(archive, target, factory.Config, Ct));
        Assert.Null(await target.ReadProjectAsync(id, Ct));
        Assert.Empty(await target.ListWorkspaceObjectsAsync("", Ct));
    }

    [SkippableTheory]
    [InlineData("""{"api_key":"known-plain-key","user_id":"1","key_type":"secret","key_hash":"","key_identifier":"","label":"x","enabled":true,"permissions_json":null,"version":1}""", "secret API key must be stored masked")]
    [InlineData("""{"api_key":"known-plain-key","user_id":"1","key_type":"public","key_hash":"","key_identifier":"","label":"x","enabled":true,"permissions_json":null,"version":1}""", "must start with sbox_ns_")]
    [InlineData("""{"api_key":"sbox_ns_abc","user_id":"1","key_type":"admin","key_hash":"","key_identifier":"","label":"x","enabled":true,"permissions_json":null,"version":1}""", "must be \"public\" or \"secret\"")]
    [InlineData("""{"api_key":"sbox_ns_abc","user_id":"7","key_type":"public","key_hash":"","key_identifier":"","label":"x","enabled":true,"permissions_json":null,"version":1}""", "user_id \"1\"")]
    public async Task ProjectArchiveRejectsApiKeyRowsSboxNsWouldNeverWrite(string row, string expected)
    {
        var id = await SeedAsync();
        var entries = ReadArchive(await ProjectExportAsync(id));
        var keysEntry = ExportFormat.ProjectEntry(id, "api-keys");
        entries[keysEntry] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries[keysEntry]) + row + "\n");
        using var archive = new MemoryStream(BuildArchive(entries.Select(entry => (entry.Key, entry.Value)).ToArray()));
        var target = await factory.NewStoreAsync();

        var error = await Assert.ThrowsAsync<ExportArchiveException>(() => ProjectArchive.ImportAsync(archive, target, factory.Config, Ct));

        Assert.Contains(expected, error.Message);
        Assert.Null(await target.ReadProjectAsync(id, Ct));
    }

    [SkippableFact]
    public async Task ProjectArchiveInvalidLaterRowRollsBackEverythingAndCorrectedRetrySucceeds()
    {
        var id = await SeedAsync();
        var entries = ReadArchive(await ProjectExportAsync(id));
        var recordsEntry = ExportFormat.ProjectEntry(id, "records");
        var goodRecords = entries[recordsEntry];
        var records = Encoding.UTF8.GetString(goodRecords).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var invalid = JsonNode.Parse(records[1])!.AsObject();
        invalid.Remove("record_key");
        entries[recordsEntry] = Encoding.UTF8.GetBytes(records[0] + "\n" + invalid.ToJsonString() + "\n");
        // Fail after every other resource, including restored storage counters, has been written.
        var broken = BuildArchive(entries.Where(entry => entry.Key != recordsEntry)
            .Select(entry => (entry.Key, entry.Value)).Append((recordsEntry, entries[recordsEntry])).ToArray());
        var target = await factory.NewStoreAsync();
        using (var archive = new MemoryStream(broken))
        {
            var error = await Assert.ThrowsAsync<ExportArchiveException>(
                () => ProjectArchive.ImportAsync(archive, target, factory.Config, Ct));
            Assert.Contains("record_key", error.Message);
        }
        await AssertProjectAbsentAsync(target, id);

        entries[recordsEntry] = goodRecords;
        using var corrected = new MemoryStream(BuildArchive(entries.Select(entry => (entry.Key, entry.Value)).ToArray()));
        var result = await ProjectArchive.ImportAsync(corrected, target, factory.Config, Ct);
        Assert.Equal(result.Manifest.TotalRows, result.RowsApplied);
        Assert.NotNull(await target.ReadProjectAsync(id, Ct));
        Assert.Single(await target.ListProjectsForUserAsync("1", Ct));
        Assert.Equal(5, (await target.ReadRecordAsync(id, Collection, PlayerA, Ct))!.Value.GetProperty("version").GetInt64());
        Assert.Equal(4_321, await target.ReadProjectStorageBytesAsync(id, Ct));
    }

    [SkippableFact]
    public async Task ProjectArchiveIndependentSharedDatabaseImportersHaveOneWinnerWithoutLoserOverwrite()
    {
        var id = await SeedAsync();
        var firstProject = Normalize(await Source.ReadProjectAsync(id, Ct));
        var first = await ProjectExportAsync(id);
        await Source.UpsertProjectAsync(id, Json(new { name = "Second importer" }), 1, Ct);
        await Source.UpsertRecordAsync(id, Collection, PlayerA, Json(new { gold = 999 }), false, 1, Ct);
        await Source.PutWorkspaceObjectAsync($"network-storage/users/1/{id}/loser-only.json", "second", Ct);
        await Source.UpsertCollectionAsync(id, "second-only", "Second", "private", Json(new { }), 1, Ct);
        var second = await ProjectExportAsync(id);
        var secondProject = Normalize(await Source.ReadProjectAsync(id, Ct));
        var target = await factory.NewStoreAsync();
        await using var independent = target switch
        {
            SqliteNetworkStorageStore sqlite => (RelationalNetworkStorageStore)new SqliteNetworkStorageStore(
                new SqliteStoreOptions { DatabasePath = sqlite.DatabasePath }),
            PostgresNetworkStorageStore postgres => new PostgresNetworkStorageStore(
                new PostgresStoreOptions { ConnectionString = SelfHostFactory.PostgresConnectionString, Schema = postgres.Schema }),
            _ => throw new InvalidOperationException("Expected a supported relational driver.")
        };
        using var ready = new Barrier(2);
        async Task<bool> ImportContenderAsync(byte[] bytes, INetworkStorageStore store)
        {
            using var archive = new RewindBarrierStream(bytes, ready);
            try
            {
                await ProjectArchive.ImportAsync(archive, store, factory.Config, Ct);
                return true;
            }
            catch (ExportArchiveException ex)
            {
                Assert.Contains("already exists", ex.Message);
                return false;
            }
        }
        var results = await Task.WhenAll(
            Task.Run(() => ImportContenderAsync(first, target)),
            Task.Run(() => ImportContenderAsync(second, independent)));
        Assert.Single(results.Where(won => won));
        var secondWon = results[1];
        Assert.Equal(secondWon ? 999 : 120,
            (await target.ReadRecordAsync(id, Collection, PlayerA, Ct))!.Value.GetProperty("payload_json").GetProperty("gold").GetInt32());
        Assert.Equal(secondWon ? 1 : 5,
            (await target.ReadRecordAsync(id, Collection, PlayerA, Ct))!.Value.GetProperty("version").GetInt64());
        Assert.Equal(secondWon ? secondProject : firstProject, Normalize(await target.ReadProjectAsync(id, Ct)));
        Assert.Equal(secondWon, await target.ReadCollectionAsync(id, "second-only", Ct) is not null);
        Assert.Equal(secondWon ? "second" : null,
            await target.ReadWorkspaceObjectAsync($"network-storage/users/1/{id}/loser-only.json", Ct));
        Assert.Single(await target.ListProjectsForUserAsync("1", Ct));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectImportReplayFailureOrCancellationRollsBackClaimAndRows(bool cancel)
    {
        var target = await factory.NewStoreAsync();
        var importer = Assert.IsAssignableFrom<IProjectImportStore>(target);
        using var cancellation = new CancellationTokenSource();
        async Task ReplayAsync(INetworkStorageStore store, CancellationToken ct)
        {
            await store.UpsertProjectAsync("rollback", Json(new { name = "Rollback" }), 1, ct);
            await store.UpsertCollectionAsync("rollback", Collection, "Inventory", "private", Json(new { }), 1, ct);
            await store.UpsertProjectMembershipAsync("1", "rollback", "owner", 0, ct);
            await store.PutWorkspaceObjectAsync("network-storage/users/1/rollback/package.json", "{}", ct);
            await store.IncrementProjectUsageAsync("rollback", "2026-01", "2026-01-01", null,
                new UsageDelta(0, 0, 0, 0, 0, 0, 0, 0, 0, 12), ct);
            if (cancel) cancellation.Cancel();
            else await store.IncrementProjectUsageAsync("rollback", "2026-01", "2026-01-01", null,
                new UsageDelta(0, 0, 0, 0, 0, 0, 0, 0, 0, long.MaxValue), ct);
        }
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => importer.TryImportProjectAsync("rollback", ReplayAsync, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(
                () => importer.TryImportProjectAsync("rollback", ReplayAsync, cancellation.Token));
        await AssertProjectAbsentAsync(target, "rollback");
        Assert.True(await importer.TryImportProjectAsync("rollback",
            (store, ct) => store.UpsertProjectAsync("rollback", Json(new { name = "Retry" }), 1, ct), Ct));
    }

    private static async Task AssertProjectAbsentAsync(INetworkStorageStore store, string id)
    {
        Assert.Null(await store.ReadProjectAsync(id, Ct));
        Assert.Empty(await store.ListCollectionsAsync(id, Ct));
        Assert.Empty(await store.ListRecordsAsync(id, Collection, Ct));
        Assert.Empty(await store.ListGlobalRecordsAsync(id, Collection, Ct));
        Assert.Empty(await store.ListLedgerEntriesAsync(id, Collection, PlayerA, Ct));
        Assert.Empty(await store.ListEndpointsAsync(id, Ct));
        Assert.Empty(await store.ListWorkflowsAsync(id, Ct));
        Assert.Empty(await store.ListQueriesAsync(id, Ct));
        Assert.Empty(await store.ListApiKeysAsync(id, Ct));
        Assert.Empty(await store.ListPagesAsync(id, Ct));
        Assert.Null(await store.ReadGameValuesAsync(id, Ct));
        Assert.Null(await store.ReadRateLimitRulesAsync(id, Ct));
        Assert.Null(await store.ReadCheckpointCursorAsync(id, Ct));
        Assert.Empty(await store.ReadProjectProfilesAsync(id, Ct));
        Assert.Null(await store.ReadPlayerSessionAsync(id, PlayerA, "sess-1", Ct));
        Assert.Empty(await store.ListPlayerEventsAsync(id, PlayerA, 0, long.MaxValue, 100, Ct));
        Assert.Empty(await store.ListAuditLogsAsync(id, 100, Ct));
        Assert.Equal(0, await store.ReadProjectStorageBytesAsync(id, Ct));
        Assert.Empty(await store.ListProjectsForUserAsync("1", Ct));
        Assert.Empty(await WorkspaceAsync(store));
    }

    private sealed class RewindBarrierStream(byte[] bytes, Barrier barrier) : MemoryStream(bytes)
    {
        private bool _rewound;
        public override long Position
        {
            get => base.Position;
            set
            {
                if (!_rewound && value == 0)
                {
                    _rewound = true;
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("Both importers must finish preflight before either can replay.");
                }
                base.Position = value;
            }
        }
    }

    [SkippableFact]
    public async Task RoundTripAcceptsUncountedEmptyListsAndAbsentSingletons()
    {
        const string projectId = "empty-import";
        await Source.UpsertProjectAsync(projectId, Json(new { name = "Empty project" }), 1, Ct);
        await Source.UpsertProjectMembershipAsync("1", projectId, "owner", 0, Ct);
        var archive = await ExportAsync(Source, factory.Config, includeSecrets: false);
        var target = await factory.NewStoreAsync();

        var result = await ImportAsync(archive, target, factory.Config, new ImportOptions(false, false));

        Assert.Equal(result.Manifest.TotalRows, result.RowsApplied);
        Assert.Equal(Normalize(await Source.ReadProjectAsync(projectId, Ct)), Normalize(await target.ReadProjectAsync(projectId, Ct)));
        Assert.Empty(await target.ListRecordsAsync(projectId, Collection, Ct));
        Assert.Null(await target.ReadGameValuesAsync(projectId, Ct));
    }

    [SkippableFact]
    public async Task ImportRefusesNonEmptyTargetUnlessForcedAndForcedImportIsIdempotent()
    {
        var projectId = await SeedAsync();
        var archive = await ExportAsync(Source, factory.Config, includeSecrets: false);
        var target = await factory.NewStoreAsync();
        await ImportAsync(archive, target, factory.Config, new ImportOptions(false, false));

        var refused = await Assert.ThrowsAsync<ExportArchiveException>(
            () => ImportAsync(archive, target, factory.Config, new ImportOptions(Force: false, RestoreConfig: false)));
        Assert.Contains(projectId, refused.Message);
        Assert.Contains("--force", refused.Message);

        await ImportAsync(archive, target, factory.Config, new ImportOptions(Force: true, RestoreConfig: false));
        await AssertSameDataAsync(Source, target, projectId);
    }

    [SkippableFact]
    public async Task ImportRefusesNewerFormatVersionBeforeWritingAnything()
    {
        var target = await factory.NewStoreAsync();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = ExportFormat.FormatName,
            formatVersion = ExportFormat.CurrentVersion + 1,
            sboxNsVersion = "99.0.0",
        });
        var archive = BuildArchive(
            (ExportFormat.ManifestEntry, manifest),
            (ExportFormat.WorkspaceObjectsEntry, Encoding.UTF8.GetBytes("{\"path\":\"network-storage/users/1/projects.json\",\"content\":\"[]\"}\n")));

        var error = await Assert.ThrowsAsync<ExportArchiveException>(
            () => ImportAsync(archive, target, factory.Config, new ImportOptions(Force: true, RestoreConfig: false)));

        Assert.Contains($"format version {ExportFormat.CurrentVersion + 1}", error.Message);
        Assert.Contains("Upgrade sbox-ns", error.Message);
        Assert.Empty(await target.ListWorkspaceObjectsAsync(string.Empty, Ct));
    }

    [SkippableFact]
    public async Task ImportRejectsArchivesWithoutLeadingManifestOrWithUnsafePaths()
    {
        var target = await factory.NewStoreAsync();
        var noManifest = BuildArchive((ExportFormat.MembershipsEntry, Encoding.UTF8.GetBytes("\n")));
        var traversal = BuildArchive(
            (ExportFormat.ManifestEntry, await ManifestBytesAsync()),
            ("config/../../escape.toml", Encoding.UTF8.GetBytes("x = 1\n")));

        Assert.Contains("first entry", (await Assert.ThrowsAsync<ExportArchiveException>(
            () => ImportAsync(noManifest, target, factory.Config, new ImportOptions(false, true)))).Message);
        Assert.Contains("unsafe path", (await Assert.ThrowsAsync<ExportArchiveException>(
            () => ImportAsync(traversal, target, factory.Config, new ImportOptions(false, true)))).Message);
    }

    [SkippableTheory]
    [InlineData("records", true)]
    [InlineData("records", false)]
    [InlineData("workspace-objects", true)]
    [InlineData("workspace-objects", false)]
    [InlineData("memberships", true)]
    [InlineData("memberships", false)]
    public async Task ImportRejectsMissingDataBeforeChangingStoreOrConfig(string resource, bool omitEntry)
    {
        var projectId = await SeedAsync();
        var sourceConfig = CreateConfigFolder("source");
        var entries = ReadArchive(await ExportAsync(Source, sourceConfig, includeSecrets: true));
        var name = resource switch
        {
            "workspace-objects" => ExportFormat.WorkspaceObjectsEntry,
            "memberships" => ExportFormat.MembershipsEntry,
            _ => ExportFormat.ProjectEntry(projectId, resource),
        };
        if (omitEntry)
        {
            Assert.True(entries.Remove(name));
        }
        else
        {
            var rows = Encoding.UTF8.GetString(entries[name]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(rows);
            entries[name] = Encoding.UTF8.GetBytes(string.Join('\n', rows.Skip(1)) + "\n");
        }

        var archive = BuildArchive(entries.Select(entry => (entry.Key, entry.Value)).ToArray());
        var targetConfig = CreateConfigFolder("target");
        ConfigFiles.SetValue(targetConfig.ConfigDirectory, SettingDefinitions.Find("server.public_url")!, "https://keep.example.com");
        var configBefore = ConfigSnapshot(targetConfig.ConfigDirectory);
        var target = await factory.NewStoreAsync();
        await target.PutWorkspaceObjectAsync("import-regression/keep.json", "{\"keep\":true}", Ct);
        var workspaceBefore = await WorkspaceAsync(target);
        var membershipsBefore = (await target.ListProjectsForUserAsync("1", Ct)).Select(row => Normalize(row)).ToArray();

        var error = await Assert.ThrowsAsync<ExportArchiveException>(() =>
            ImportAsync(archive, target, targetConfig, new ImportOptions(Force: true, RestoreConfig: true)));

        Assert.Contains(name, error.Message);
        Assert.Equal(workspaceBefore, await WorkspaceAsync(target));
        Assert.Equal(membershipsBefore, (await target.ListProjectsForUserAsync("1", Ct)).Select(row => Normalize(row)).ToArray());
        Assert.Null(await target.ReadProjectAsync(projectId, Ct));
        Assert.Empty(await target.ListRecordsAsync(projectId, Collection, Ct));
        Assert.Equal(configBefore, ConfigSnapshot(targetConfig.ConfigDirectory));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigRestoreRejectsSymlinkAncestorsWithoutOutsideMutation(bool linkRoot)
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating symbolic links requires elevated permissions on Windows.");
        var source = CreateConfigFolder("source");
        var entries = ReadArchive(await ExportAsync(Source, source, includeSecrets: true));
        entries["config/secrets/escape"] = Encoding.UTF8.GetBytes("archived secret");
        var archive = BuildArchive(entries.Select(entry => (entry.Key, entry.Value)).ToArray());
        var outside = CreateConfigFolder("outside");
        ConfigFiles.SetValue(outside.ConfigDirectory, SettingDefinitions.Find("server.public_url")!, "https://outside.example.com");
        File.WriteAllText(Path.Combine(outside.ConfigDirectory, "escape"), "keep outside file");
        var outsideBefore = ConfigSnapshot(outside.ConfigDirectory);
        var targetConfig = CreateConfigFolder("target");
        ConfigFiles.SetValue(targetConfig.ConfigDirectory, SettingDefinitions.Find("server.public_url")!, "https://keep.example.com");
        string link;
        if (linkRoot)
        {
            link = Path.Combine(factory.Config.DataDirectory, "linked-config");
            Directory.CreateSymbolicLink(link, outside.ConfigDirectory);
            targetConfig = ConfigLoader.Load(link, targetConfig.DataDirectory, environment: _ => null);
        }
        else
        {
            // Operator-provided secret files (not generated state) restore into the config folder's secrets folder.
            link = Path.Combine(targetConfig.ConfigDirectory, "secrets");
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: true);
            }

            Directory.CreateSymbolicLink(link, outside.ConfigDirectory);
        }

        var serverBefore = File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.ServerFile));
        try
        {
            var target = await factory.NewStoreAsync();
            var error = await Assert.ThrowsAsync<ExportArchiveException>(() =>
                ImportAsync(archive, target, targetConfig, new ImportOptions(false, RestoreConfig: true)));

            Assert.Equal(outsideBefore, ConfigSnapshot(outside.ConfigDirectory));
            Assert.Equal(serverBefore, File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.ServerFile)));
            Assert.False(File.Exists(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.ServerFile) + ConfigArchive.BackupSuffix));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [SkippableFact]
    public async Task ConfigRestoreRejectsASymlinkedStateSecretsFolderWithoutOutsideMutation()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating symbolic links requires elevated permissions on Windows.");
        var source = CreateConfigFolder("source");
        var archive = await ExportAsync(Source, source, includeSecrets: true);
        var outside = CreateConfigFolder("outside");
        var outsideBefore = ConfigSnapshot(outside.ConfigDirectory);
        var targetConfig = CreateConfigFolder("target");
        var link = Path.Combine(targetConfig.StateDirectory, "secrets");
        Directory.Delete(link, recursive: true);
        Directory.CreateSymbolicLink(link, outside.ConfigDirectory);
        try
        {
            var target = await factory.NewStoreAsync();
            await Assert.ThrowsAsync<ExportArchiveException>(() =>
                ImportAsync(archive, target, targetConfig, new ImportOptions(false, RestoreConfig: true)));

            Assert.Equal(outsideBefore, ConfigSnapshot(outside.ConfigDirectory));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [SkippableFact]
    public async Task NoSecretsExcludesSecretsFolderAndBlanksInlineSecrets()
    {
        var config = CreateConfigFolder("source");
        var withoutSecrets = ReadArchive(await ExportAsync(Source, config, includeSecrets: false));
        var withSecrets = ReadArchive(await ExportAsync(Source, config, includeSecrets: true));

        Assert.Contains("config/server.toml", withoutSecrets.Keys);
        Assert.Contains("config/conf.d/10-local.toml", withoutSecrets.Keys);
        Assert.DoesNotContain(withoutSecrets.Keys, name => name.StartsWith("config/secrets/", StringComparison.Ordinal));
        var database = Encoding.UTF8.GetString(withoutSecrets["config/database.toml"]);
        Assert.DoesNotContain("hunter2-database-password", database);
        Assert.Contains("password = \"\"", database);
        Assert.False(JsonDocument.Parse(withoutSecrets[ExportFormat.ManifestEntry]).RootElement.GetProperty("includesSecrets").GetBoolean());

        Assert.Contains("config/secrets/storage_encryption_key", withSecrets.Keys);
        Assert.Contains("config/secrets/auth_session_secret", withSecrets.Keys);
        Assert.Contains("hunter2-database-password", Encoding.UTF8.GetString(withSecrets["config/database.toml"]));
        Assert.True(JsonDocument.Parse(withSecrets[ExportFormat.ManifestEntry]).RootElement.GetProperty("includesSecrets").GetBoolean());
    }

    [SkippableFact]
    public async Task ConfigRestoreKeepsTargetDatabaseSettingsAndWritesSecretsOwnerOnly()
    {
        var source = CreateConfigFolder("source");
        var archive = await ExportAsync(Source, source, includeSecrets: true);
        var targetConfig = CreateConfigFolder("target");
        var targetDatabaseToml = File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.DatabaseFile));
        ConfigFiles.SetValue(targetConfig.ConfigDirectory, SettingDefinitions.Find("server.public_url")!, "https://new.example.com");

        var result = await ImportAsync(archive, await factory.NewStoreAsync(), targetConfig, new ImportOptions(false, RestoreConfig: true));

        Assert.NotEmpty(result.ConfigFilesWritten);
        // Generated secrets are restored into the state folder, never the config folder.
        var key = Path.Combine(targetConfig.StateDirectory, "secrets", "storage_encryption_key");
        Assert.Equal(File.ReadAllText(Path.Combine(source.StateDirectory, "secrets", "storage_encryption_key")), File.ReadAllText(key));
        Assert.False(Directory.Exists(Path.Combine(targetConfig.ConfigDirectory, "secrets")));
        Assert.True(File.Exists(key + ConfigArchive.BackupSuffix));
        Assert.Equal(File.ReadAllText(Path.Combine(source.ConfigDirectory, SettingDefinitions.ServerFile)),
            File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.ServerFile)));
        Assert.Equal(targetDatabaseToml, File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, SettingDefinitions.DatabaseFile)));
        Assert.Contains("hunter2-database-password", File.ReadAllText(Path.Combine(targetConfig.ConfigDirectory, ConfigArchive.ImportedDatabaseFile)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }
    }

    [SkippableFact]
    public async Task DashboardExportIsOwnerOnlyAntiforgeryProtectedAndAudited()
    {
        var projectId = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OwnerAccountService>().CreateAsync("owner", "initial-owner-password", Ct);
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonymous = await client.PostAsync("/dashboard/export", Form(("includeSecrets", "false")));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("http://localhost/login", anonymous.Headers.Location!.ToString());

        var loginPage = await client.GetStringAsync("/login");
        var login = await client.PostAsync("/login", Form(("username", "owner"), ("password", "initial-owner-password"), ("__RequestVerificationToken", Csrf(loginPage))));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var dashboard = await client.GetStringAsync("/dashboard");
        Assert.Contains("action=\"/dashboard/export\"", dashboard);

        var noCsrf = await client.PostAsync("/dashboard/export", Form(("includeSecrets", "false")));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        var download = await client.PostAsync("/dashboard/export", Form(("__RequestVerificationToken", Csrf(dashboard))));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/gzip", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition!.ToString());
        var entries = ReadArchive(await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(ExportFormat.ManifestEntry, entries.Keys.First());
        var manifest = JsonDocument.Parse(entries[ExportFormat.ManifestEntry]).RootElement;
        Assert.False(manifest.GetProperty("includesSecrets").GetBoolean());
        Assert.Contains(ExportFormat.ProjectEntry(projectId, "records"), entries.Keys);

        var audit = await Source.ListAuditLogsAsync(projectId, 50, Ct);
        Assert.Contains(audit, row => row.GetProperty("action").GetString() == "server.export");

        var target = await factory.NewStoreAsync();
        using var stream = new MemoryStream(await download.Content.ReadAsByteArrayAsync());
        await ServerArchive.ImportAsync(stream, target, factory.Config, new ImportOptions(false, false), Ct);
        await AssertSameDataAsync(Source, target, projectId, ignoreAudit: true);
    }

    [SkippableFact]
    public async Task DashboardSecretExportNeedsThePassword()
    {
        await SeedAsync();
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        var csrf = Csrf(await client.GetStringAsync("/dashboard"));

        Task<HttpResponseMessage> ExportAsync(params (string Key, string Value)[] fields)
            => client.PostAsync("/dashboard/export", Form([("__RequestVerificationToken", csrf), ("includeSecrets", "true"), .. fields]));

        // A session cookie alone (the stolen-cookie case) or a wrong password gets no archive.
        foreach (var attempt in new[] { await ExportAsync(), await ExportAsync(("password", "not-the-owner-password")) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
            Assert.NotEqual("application/gzip", attempt.Content.Headers.ContentType?.MediaType);
            Assert.Contains("enter your password", await attempt.Content.ReadAsStringAsync());
        }

        using var confirmed = await ExportAsync(("password", OwnerHttp.Password));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var manifest = JsonDocument.Parse(ReadArchive(await confirmed.Content.ReadAsByteArrayAsync())[ExportFormat.ManifestEntry]).RootElement;
        Assert.True(manifest.GetProperty("includesSecrets").GetBoolean());
    }

    [SkippableFact]
    public async Task DashboardSecretExportNeedsAuthenticatorCodeWhenEnrolled()
    {
        await SeedAsync();
        await OwnerHttp.CreateOwnerAsync(factory);
        string[] recovery;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<OwnerAccountService>();
            var secret = OwnerTotp.NewSecret();
            var owner = (await accounts.GetAsync(Ct))!;
            recovery = await accounts.EnrollAsync(owner.SecurityStamp, accounts.ProtectEnrollment(secret),
                OwnerTotp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), Ct);
        }

        using var client = OwnerHttp.Client(factory);
        using (var login = await client.PostAsync("/login", Form(("username", OwnerHttp.Username), ("password", OwnerHttp.Password),
            ("secondFactor", recovery[0]), ("__RequestVerificationToken", Csrf(await client.GetStringAsync("/login"))))))
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var csrf = Csrf(await client.GetStringAsync("/dashboard"));

        Task<HttpResponseMessage> ExportAsync(params (string Key, string Value)[] fields)
            => client.PostAsync("/dashboard/export", Form([("__RequestVerificationToken", csrf), ("includeSecrets", "true"), ("password", OwnerHttp.Password), .. fields]));

        using var passwordOnly = await ExportAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
        using var usedCode = await ExportAsync(("secondFactor", recovery[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, usedCode.StatusCode);
        using var confirmed = await ExportAsync(("secondFactor", recovery[1]));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("application/gzip", confirmed.Content.Headers.ContentType!.MediaType);
    }

    private async Task<string> SeedAsync()
    {
        var project = await factory.CreateProjectAsync("Export Game");
        var p = project.ProjectId;
        var store = Source;
        await store.UpsertCollectionAsync(p, Collection, "Inventory", "private", Json(new { fields = new[] { "gold" } }), 3, Ct);
        await store.UpsertRecordAsync(p, Collection, PlayerA, Json(new { gold = 120, name = "Ålice ✓" }), false, 5, Ct);
        await store.UpsertRecordAsync(p, Collection, PlayerB, Json(new { gold = 0 }), true, 2, Ct);
        await store.InsertLedgerEntryAsync(p, Collection, PlayerA, 1, Json(new { delta = 100 }), Ct);
        await store.InsertLedgerEntryAsync(p, Collection, PlayerA, 2, Json(new { delta = 20 }), Ct);
        await store.UpsertGlobalRecordAsync(p, Collection, "season-1", Json(new { top = PlayerA }), 4, Ct);
        await store.UpsertEndpointAsync(p, "ep-1", "buy-item", "POST", true, Json(new { steps = 2 }), "hash-ep", 7, Ct);
        await store.UpsertWorkflowAsync(p, "wf-1", "Grant", Json(new { steps = 1 }), "hash-wf", 2, Ct);
        await store.UpsertQueryAsync(p, "q-1", "Top players", true, Json(new { limit = 10 }), 3, Ct);
        await store.UpsertGameValuesAsync(p, Json(new[] { new { name = "speed", value = 10 } }), "hash-gv", 9, Ct);
        await store.UpsertRateLimitRulesAsync(p, Json(new { perMinute = 60 }), 2, Ct);
        await store.UpsertCheckpointCursorAsync(p, 42, "manifests/42.json", 3, Ct);
        await store.UpsertPageAsync(p, "news", "News", "{\"blocks\":[]}", 1_000, 2_000, Ct);
        await store.UpsertPlayerProfileAsync(p, PlayerA, "Alice", false, null, 5_000, 4_900, "sess-1", 30, 120, 3, "heartbeat",
            "buy-item", "{\"kills\":2}", 5_000, Ct);
        await store.InsertPlayerSessionAsync(p, PlayerA, "sess-1", 4_000, 4_900, null, "{\"fps\":60}", null, Ct);
        await store.InsertPlayerAnalyticsEventV2Async(p, PlayerA, 4_100, "ev-1", "purchase", "economy", "Bought", "buy-item", Collection,
            Json(new { item = "sword" }), Ct);
        await store.InsertPlayerAnalyticsEventV2Async(p, PlayerA, 4_200, "ev-2", "purchase", "economy", "Bought", "buy-item", Collection,
            Json(new { item = "shield" }), Ct);
        await store.InsertAuditLogAsync(p, 3_000, "log-seed", "1", "test.seed", "{}", "{}", "{\"message\":\"seeded\"}", "", Ct);
        await store.IncrementProjectUsageAsync(p, "2026-01", "2026-01-02", null, new UsageDelta(1, 1, 0, 0, 10, 20, 0, 5, 1, 4_321), Ct);
        await store.PutWorkspaceObjectAsync($"network-storage/users/1/{p}/game-package.json", "{\"currentRevisionId\":3}", Ct);
        return p;
    }

    private static async Task AssertSameDataAsync(INetworkStorageStore source, INetworkStorageStore target, string p, bool ignoreAudit = false)
    {
        const string Updated = "updated_at_unix_ms";
        const string Created = "created_at_unix_ms";
        Assert.Equal(await WorkspaceAsync(source), await WorkspaceAsync(target));
        AssertRows(await source.ListProjectsForUserAsync("1", Ct), await target.ListProjectsForUserAsync("1", Ct));
        Assert.Equal(Normalize(await source.ReadProjectAsync(p, Ct)), Normalize(await target.ReadProjectAsync(p, Ct)));
        AssertRows(await source.ListCollectionsAsync(p, Ct), await target.ListCollectionsAsync(p, Ct), Updated);
        AssertRows(await source.ListRecordsAsync(p, Collection, Ct), await target.ListRecordsAsync(p, Collection, Ct), Updated);
        Assert.Equal(2, (await target.ListRecordsAsync(p, Collection, Ct)).Count);
        AssertRows(await source.ListLedgerEntriesAsync(p, Collection, PlayerA, Ct), await target.ListLedgerEntriesAsync(p, Collection, PlayerA, Ct), Created);
        AssertRows(await source.ListGlobalRecordsAsync(p, Collection, Ct), await target.ListGlobalRecordsAsync(p, Collection, Ct), Created);
        AssertRows(await source.ListEndpointsAsync(p, Ct), await target.ListEndpointsAsync(p, Ct), Updated);
        AssertRows(await source.ListWorkflowsAsync(p, Ct), await target.ListWorkflowsAsync(p, Ct), Updated);
        AssertRows(await source.ListQueriesAsync(p, Ct), await target.ListQueriesAsync(p, Ct), Updated);
        AssertRows(await source.ListApiKeysAsync(p, Ct), await target.ListApiKeysAsync(p, Ct), Updated);
        Assert.Equal(2, (await target.ListApiKeysAsync(p, Ct)).Count);
        AssertRows(await source.ListPagesAsync(p, Ct), await target.ListPagesAsync(p, Ct));
        Assert.Equal(Normalize(await source.ReadGameValuesAsync(p, Ct), Updated), Normalize(await target.ReadGameValuesAsync(p, Ct), Updated));
        Assert.Equal(Normalize(await source.ReadRateLimitRulesAsync(p, Ct), Updated), Normalize(await target.ReadRateLimitRulesAsync(p, Ct), Updated));
        Assert.Equal(Normalize(await source.ReadCheckpointCursorAsync(p, Ct), Updated), Normalize(await target.ReadCheckpointCursorAsync(p, Ct), Updated));
        AssertRows(await source.ReadProjectProfilesAsync(p, Ct), await target.ReadProjectProfilesAsync(p, Ct));
        Assert.Equal(Normalize(await source.ReadPlayerSessionAsync(p, PlayerA, "sess-1", Ct)), Normalize(await target.ReadPlayerSessionAsync(p, PlayerA, "sess-1", Ct)));
        AssertRows(await source.ListPlayerEventsAsync(p, PlayerA, 0, long.MaxValue, 100, Ct), await target.ListPlayerEventsAsync(p, PlayerA, 0, long.MaxValue, 100, Ct));
        Assert.Equal(await source.ReadProjectStorageBytesAsync(p, Ct), await target.ReadProjectStorageBytesAsync(p, Ct));
        if (!ignoreAudit)
        {
            AssertRows(await source.ListAuditLogsAsync(p, 100, Ct), await target.ListAuditLogsAsync(p, 100, Ct));
        }
    }

    private static void AssertRows(IReadOnlyList<JsonElement> expected, IReadOnlyList<JsonElement> actual, params string[] ignore)
    {
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(row => Normalize(row, ignore)).Order(StringComparer.Ordinal),
            actual.Select(row => Normalize(row, ignore)).Order(StringComparer.Ordinal));
    }

    private static string? Normalize(JsonElement? row, params string[] ignore)
    {
        if (row is not { } value)
        {
            return null;
        }

        var node = JsonNode.Parse(value.GetRawText());
        if (node is JsonObject obj)
        {
            foreach (var name in ignore)
            {
                obj.Remove(name);
            }
        }

        return node?.ToJsonString();
    }

    private static async Task<SortedDictionary<string, string>> WorkspaceAsync(INetworkStorageStore store)
    {
        var objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
        async Task WalkAsync(string directory)
        {
            foreach (var entry in await store.ListWorkspaceObjectsAsync(directory, Ct))
            {
                var path = directory.Length == 0 ? entry.Name : $"{directory}/{entry.Name}";
                if (entry.IsDirectory)
                {
                    await WalkAsync(path);
                }
                else
                {
                    objects[path] = (await store.ReadWorkspaceObjectAsync(path, Ct))!;
                }
            }
        }

        await WalkAsync(string.Empty);
        return objects;
    }

    private async Task<byte[]> ExportAsync(INetworkStorageStore store, EffectiveConfig config, bool includeSecrets)
    {
        var admin = factory.Services.GetRequiredService<INetworkStorageStoreAdmin>();
        await using var staged = await ServerArchive.PrepareExportAsync(store, admin, config, includeSecrets, Ct);
        using var buffer = new MemoryStream();
        await staged.WriteToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private async Task<byte[]> ProjectExportAsync(string projectId)
    {
        var admin = factory.Services.GetRequiredService<INetworkStorageStoreAdmin>();
        await using var staged = await ServerArchive.PrepareProjectExportAsync(Source, admin, factory.Config, projectId, Ct);
        using var buffer = new MemoryStream();
        await staged.WriteToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private static async Task<ImportResult> ImportAsync(byte[] archive, INetworkStorageStore target, EffectiveConfig config, ImportOptions options)
    {
        using var stream = new MemoryStream(archive);
        return await ServerArchive.ImportAsync(stream, target, config, options, Ct);
    }

    private async Task<byte[]> ManifestBytesAsync()
    {
        var entries = ReadArchive(await ExportAsync(Source, factory.Config, includeSecrets: false));
        return entries[ExportFormat.ManifestEntry];
    }

    /// <summary>A config folder with all four TOML files, a conf.d fragment, generated secrets and an inline database password.</summary>
    private EffectiveConfig CreateConfigFolder(string name)
    {
        var root = Path.Combine(factory.Config.DataDirectory, "config-folders", name);
        var configDirectory = Path.Combine(root, "config");
        ConfigFiles.WriteAll(configDirectory, new Dictionary<string, object> { ["updates.check"] = false });
        ConfigFiles.SetValue(configDirectory, SettingDefinitions.Find("database.postgres.password")!, "hunter2-database-password");
        File.WriteAllText(Path.Combine(configDirectory, ConfigLoader.ConfDirectory, "10-local.toml"), "[logging]\nlevel = \"Warning\"\n");
        var config = ConfigLoader.Load(configDirectory, Path.Combine(root, "data"), environment: _ => null);
        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        ServerSecrets.EnsureAndLoad(config);
        return config;
    }

    private static SortedDictionary<string, string> ConfigSnapshot(string directory)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(directory, path)] = Convert.ToBase64String(File.ReadAllBytes(path));
        }

        return files;
    }

    private static byte[] BuildArchive(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, byte[]> ReadArchive(byte[] archive)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            entries.Add(entry.Name, content.ToArray());
        }

        return entries;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        => new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static string Csrf(string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "Expected an antiforgery token in the Razor form.");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }
}

public sealed class SqliteServerExportImportTests : ServerExportImportTests<SqliteHostFactory>
{
}

public sealed class PostgresServerExportImportTests : ServerExportImportTests<PostgresHostFactory>
{
}
