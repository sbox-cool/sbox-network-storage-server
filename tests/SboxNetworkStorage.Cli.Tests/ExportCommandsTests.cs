using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>Moving a whole server with two commands: <c>sbox-ns export</c> on one folder, <c>sbox-ns import --config</c> on another.</summary>
public sealed class ExportCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-export-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pooled SQLite handles can briefly keep the database file open on Windows.
        }
    }

    [Fact]
    public async Task Export_then_import_moves_projects_keys_and_secrets_to_another_server()
    {
        var source = CreateServerFolders("old");
        var target = CreateServerFolders("new");
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context(source, "project", "create", "Moved Game")));
        var projectId = (await ProjectsAsync(source)).Single().Id;
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunKeyAsync(Context(source, "key", "create", projectId, "--type", "secret")));

        var archive = Path.Combine(_root, "moved.tar.gz");
        Assert.Equal(CliApp.Ok, await ExportCommands.ExportAsync(Context(source, "export", "--out", archive)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive));
        }

        var exists = await Assert.ThrowsAsync<CliException>(() => ExportCommands.ExportAsync(Context(source, "export", "--out", archive)));
        Assert.Contains("already exists", exists.Message);

        Assert.Equal(CliApp.Ok, await ExportCommands.ImportAsync(Context(target, "import", archive, "--config")));

        var moved = Assert.Single(await ProjectsAsync(target));
        Assert.Equal(projectId, moved.Id);
        Assert.Equal("Moved Game", moved.Name);
        var sourceKeys = await KeyCountAsync(source, projectId);
        Assert.NotEqual(0, sourceKeys);
        Assert.Equal(sourceKeys, await KeyCountAsync(target, projectId));
        // Generated secrets live in the state folder, not the config folder, and travel with the export.
        Assert.Equal(File.ReadAllText(Path.Combine(source.Data, "state", "secrets", "storage_encryption_key")),
            File.ReadAllText(Path.Combine(target.Data, "state", "secrets", "storage_encryption_key")));
        Assert.False(Directory.Exists(Path.Combine(target.Config, "secrets")));
        Assert.True(File.Exists(Path.Combine(target.Config, "database.toml.from-export")));

        var refused = await Assert.ThrowsAsync<CliException>(() => ExportCommands.ImportAsync(Context(target, "import", archive)));
        Assert.Contains("--force", refused.Message);
        Assert.Equal(CliApp.Ok, await ExportCommands.ImportAsync(Context(target, "import", archive, "--force")));
        Assert.Single(await ProjectsAsync(target));
    }

    [Fact]
    public async Task Import_rejects_a_file_that_is_not_an_export()
    {
        var target = CreateServerFolders("new");
        var bogus = Path.Combine(_root, "bogus.tar.gz");
        File.WriteAllText(bogus, "not an archive");

        var error = await Assert.ThrowsAsync<CliException>(() => ExportCommands.ImportAsync(Context(target, "import", bogus)));

        Assert.Contains("archive", error.Message);
    }

    [Fact]
    public async Task Verify_only_accepts_an_sbox_ns_export_without_touching_the_target()
    {
        var source = CreateServerFolders("old");
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context(source, "project", "create", "Checked Game")));
        var projectId = (await ProjectsAsync(source)).Single().Id;
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunKeyAsync(Context(source, "key", "create", projectId, "--type", "secret")));
        var archive = Path.Combine(_root, "checked.tar.gz");
        Assert.Equal(CliApp.Ok, await ExportCommands.ExportAsync(Context(source, "export", "--out", archive)));

        var target = (Config: Path.Combine(_root, "absent", "config"), Data: Path.Combine(_root, "absent", "data"));
        Assert.Equal(CliApp.Ok, await ExportCommands.ImportAsync(Context(target, "import", archive, "--verify-only")));

        Assert.False(Directory.Exists(Path.Combine(_root, "absent")));
    }

    [Fact]
    public async Task Verify_only_accepts_a_hosted_style_archive_that_follows_the_contract()
    {
        var archive = WriteExternalArchive("1", "network-storage/users/1/proj1/collections.json", "1");

        Assert.Equal(CliApp.Ok, await ExportCommands.ImportAsync(Context(CreateServerFolders("unused"), "import", archive, "--verify-only")));
    }

    [Theory]
    [InlineData("42", "network-storage/users/1/proj1/collections.json", "1", "memberships must have user_id")]
    [InlineData("1", "network-storage/users/42/proj1/collections.json", "1", "network-storage/users/1/")]
    [InlineData("1", "network-storage/users/1/other/collections.json", "1", "not in the manifest")]
    [InlineData("1", "elsewhere/collections.json", "1", "under network-storage/")]
    [InlineData("1", "network-storage/users/1/proj1/collections.json", "42", "API keys must have user_id")]
    public async Task Verify_only_rejects_archives_that_break_the_single_owner_contract(string memberUser, string workspacePath, string keyUser, string expected)
    {
        var archive = WriteExternalArchive(memberUser, workspacePath, keyUser);

        var error = await Assert.ThrowsAsync<CliException>(() => ExportCommands.ImportAsync(Context(CreateServerFolders("unused"), "import", archive, "--verify-only")));

        Assert.Contains(expected, error.Message);
    }

    /// <summary>A minimal archive as an external exporter would write it (docs/export.md, "Archive contract").</summary>
    private string WriteExternalArchive(string memberUser, string workspacePath, string keyUser)
    {
        var path = Path.Combine(_root, $"external-{Guid.NewGuid():N}.tar.gz");
        var entries = new (string Name, string Content)[]
        {
            ("manifest.json", """
                {"format":"sbox-ns-export","formatVersion":1,"sboxNsVersion":"sboxcool-hosted test","schemaVersion":1,"provider":"scylladb",
                 "createdAt":"2026-10-09T00:00:00Z","includesConfig":false,"includesSecrets":false,"workspaceObjects":1,"memberships":1,
                 "projects":[{"id":"proj1","counts":{"project":1,"api-keys":1}}]}
                """),
            ("data/workspace-objects.jsonl", $$"""{"path":"{{workspacePath}}","content":"[]"}""" + "\n"),
            ("data/memberships.jsonl", $$"""{"user_id":"{{memberUser}}","project_id":"proj1","role":"owner","created_at_unix_ms":1760000000000}""" + "\n"),
            ("data/projects/proj1/project.jsonl", """{"payload":{"id":"proj1","name":"Migrated","storageOwnerUserId":"1"}}""" + "\n"),
            ("data/projects/proj1/api-keys.jsonl", $$"""{"api_key":"sbox_ns_pub_test","user_id":"{{keyUser}}","key_type":"public","key_hash":"","key_identifier":"","label":"game","enabled":true,"permissions_json":null,"version":1}""" + "\n"),
        };
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }

        return path;
    }

    private (string Config, string Data) CreateServerFolders(string name)
    {
        var config = Path.Combine(_root, name, "config");
        ConfigFiles.WriteAll(config, new Dictionary<string, object> { ["updates.check"] = false });
        return (config, Path.Combine(_root, name, "data"));
    }

    private static CliContext Context((string Config, string Data) server, params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", server.Config, "--data-dir", server.Data]));

    private static async Task<IReadOnlyList<WorkspaceProject>> ProjectsAsync((string Config, string Data) server)
    {
        await using var services = CliServices.Build(Load(server));
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>()
            .GetUserProjectsAsync(NetworkStorageServices.LocalOwnerUserId, CancellationToken.None);
    }

    private static async Task<int> KeyCountAsync((string Config, string Data) server, string projectId)
    {
        await using var services = CliServices.Build(Load(server));
        return (await services.GetRequiredService<INetworkStorageStore>().ListApiKeysAsync(projectId, CancellationToken.None)).Count;
    }

    private static EffectiveConfig Load((string Config, string Data) server)
        => ConfigLoader.Load(server.Config, server.Data, null, _ => null);
}
