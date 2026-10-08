using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class DemoReadOnlyTests
{
    [Fact]
    public async Task AnonymousVisitorsBrowseStoredRowsButCannotMutateOrAccessCredentials()
    {
        using var fixture = new SqliteHostFactory();
        await using var app = ServerHost.Build(DemoConfig(fixture.Config), configureBuilder: builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var scope = app.Services.CreateScope();
        var project = Assert.Single(await scope.ServiceProvider.GetRequiredService<IBunnyWorkspaceClient>().GetUserProjectsAsync(1, default));
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var before = (await store.ReadRecordAsync(project.Id, "players", "demo-player-001", default))!.Value.GetRawText();
        Assert.Contains("Read-only demo", await client.GetStringAsync("/dashboard"));
        Assert.Contains("Demo Scout", await client.GetStringAsync($"/dashboard/projects/{project.Id}/data/players/records/demo-player-001"));
        foreach (var path in new[] { "/setup", "/dashboard/security", "/v3/endpoints/example/save" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { "/dashboard/projects", $"/dashboard/projects/{project.Id}/delete", $"/dashboard/projects/{project.Id}/data/players/records/demo-player-001", "/logout" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, new StringContent("{}"))).StatusCode);
        Assert.Equal(before, (await store.ReadRecordAsync(project.Id, "players", "demo-player-001", default))!.Value.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task EnablingPublicDemoRefusesAnExistingNonDemoProjectDatabase()
    {
        using var fixture = new SqliteHostFactory();
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().CreateProjectAsync(1, "Private project", "Must stay private", true, true, "player", "", default);
        await using var app = ServerHost.Build(DemoConfig(fixture.Config), configureBuilder: builder => builder.WebHost.UseTestServer());
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
    }

    private static EffectiveConfig DemoConfig(EffectiveConfig config)
    {
        var values = config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["adminpanel.demo_read_only"] = values["adminpanel.demo_read_only"] with { Value = true };
        return new EffectiveConfig { ConfigDirectory = config.ConfigDirectory, DataDirectory = config.DataDirectory,
            Values = values, LoadedFiles = config.LoadedFiles, Issues = [] };
    }
}
