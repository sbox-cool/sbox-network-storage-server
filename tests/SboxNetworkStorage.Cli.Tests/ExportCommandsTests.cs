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
        Assert.Equal(File.ReadAllText(Path.Combine(source.Config, "secrets", "storage_encryption_key")),
            File.ReadAllText(Path.Combine(target.Config, "secrets", "storage_encryption_key")));
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

    private (string Config, string Data) CreateServerFolders(string name)
    {
        var config = Path.Combine(_root, name, "config");
        ConfigFiles.WriteAll(config, new Dictionary<string, object> { ["updates.check"] = false });
        return (config, Path.Combine(_root, name, "data"));
    }

    private static CliContext Context((string Config, string Data) server, params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", server.Config, "--data-dir", server.Data]));

    private static async Task<IReadOnlyList<BunnyProject>> ProjectsAsync((string Config, string Data) server)
    {
        await using var services = CliServices.Build(Load(server));
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IBunnyWorkspaceClient>()
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
