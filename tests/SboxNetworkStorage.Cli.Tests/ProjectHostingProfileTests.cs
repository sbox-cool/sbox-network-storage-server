using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class ProjectHostingProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-hosting-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");
    private string DataDir => Path.Combine(_root, "data");

    public ProjectHostingProfileTests()
        => ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["updates.check"] = false });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // pooled SQLite handles on Windows
    }

    private CliContext Context(params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", ConfigDir, "--data-dir", DataDir]));

    private async Task<IReadOnlyList<string?>> ProfilesAsync()
    {
        await using var services = CliServices.Build(ConfigLoader.Load(ConfigDir, DataDir, null, _ => null));
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var ids = (await store.ListProjectsForUserAsync("1", CancellationToken.None)).Select(p => p.GetProperty("project_id").GetString()!);
        var result = new List<string?>();
        foreach (var id in ids)
            result.Add((await projects.ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, id, CancellationToken.None))!.Project.HostingProfile);
        return result;
    }

    [Fact]
    public async Task Create_defaults_to_unset_and_stores_an_explicit_profile()
    {
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context("project", "create", "Plain")));
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context("project", "create", "Dedicated box", "--hosting", "dedicated")));

        Assert.Equal([null, "dedicated"], (await ProfilesAsync()).Order().ToArray());
    }

    [Fact]
    public async Task Create_rejects_an_unknown_profile_before_creating_anything()
    {
        var ex = await Assert.ThrowsAsync<CliException>(() => ProjectCommands.RunProjectAsync(Context("project", "create", "Bad", "--hosting", "cloud")));

        Assert.Equal(CliApp.Usage, ex.ExitCode);
        Assert.Empty(await ProfilesAsync());
    }

    [Fact]
    public async Task Authority_for_an_unknown_project_fails_instead_of_reporting_clean()
    {
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context("project", "create", "Known")));

        var ex = await Assert.ThrowsAsync<CliException>(() => ProjectCommands.RunProjectAsync(Context("project", "authority", "proj_missing")));

        Assert.Contains("proj_missing", ex.Message);
    }
}
