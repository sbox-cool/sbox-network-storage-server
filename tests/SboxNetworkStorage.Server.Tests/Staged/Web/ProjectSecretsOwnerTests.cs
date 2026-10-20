using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Owner dashboard project secrets: the panel shows names and times only, and a saved value is never displayed again.</summary>
public sealed class ProjectSecretsOwnerTests
{
    private const string FirstValue = "first-owner-secret-7731";
    private const string ReplacedValue = "replaced-owner-secret-8842";

    private static async Task<(SqliteHostFactory Factory, HttpClient Owner, SelfHostProject Project, string Url)> SetupAsync()
    {
        var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Project secrets");
        return (factory, owner, project, $"/dashboard/projects/{project.ProjectId}");
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    [Fact]
    public async Task SavedSecretNameIsListedButValueIsNeverShown()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var _ = factory;
        using var __ = owner;

        using (var saved = await PostFormAsync(owner, url, url + "/secrets", ("name", "API_KEY"), ("value", FirstValue)))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var page = await owner.GetStringAsync(url);
        Assert.Contains("API_KEY", page);
        Assert.DoesNotContain(FirstValue, page);

        var store = factory.Services.GetRequiredService<IProjectSecretStore>();
        var listed = await store.ListProjectSecretsAsync(project.ProjectId, CancellationToken.None);
        Assert.Equal("API_KEY", Assert.Single(listed).Name);
    }

    [Fact]
    public async Task ReplacedSecretKeepsOnlyTheNameAndNeverShowsEitherValue()
    {
        var (factory, owner, _, url) = await SetupAsync();
        using var __ = factory;
        using var ___ = owner;

        using (await PostFormAsync(owner, url, url + "/secrets", ("name", "API_KEY"), ("value", FirstValue))) { }
        using (var replaced = await PostFormAsync(owner, url, url + "/secrets", ("name", "API_KEY"), ("value", ReplacedValue)))
            Assert.Equal(HttpStatusCode.Redirect, replaced.StatusCode);

        var page = await owner.GetStringAsync(url);
        Assert.Contains("API_KEY", page);
        Assert.DoesNotContain(FirstValue, page);
        Assert.DoesNotContain(ReplacedValue, page);
    }

    [Fact]
    public async Task MultilineSecret_IsPreservedAndDeletingTheProjectRemovesTheCredential()
    {
        var (factory, owner, project, url) = await SetupAsync();
        using var host = factory;
        using var client = owner;
        const string credential = "-----BEGIN KEY-----\nvalue with spaces  \n-----END KEY-----\n";
        using (var saved = await PostFormAsync(owner, url, url + "/secrets", ("name", "PEM"), ("value", credential)))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var service = factory.Services.GetRequiredService<SboxNetworkStorage.Application.NetworkStorage.Secrets.ProjectSecretsService>();
        Assert.Equal(credential, await service.ReadAsync(project.ProjectId, "PEM", CancellationToken.None));
        var page = await owner.GetStringAsync(url);
        Assert.DoesNotContain("-----BEGIN KEY-----", page);
        using (var deleted = await PostFormAsync(owner, url, url + "/delete", ("confirmation", project.ProjectId)))
            Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<IProjectSecretStore>().ListProjectSecretsAsync(project.ProjectId, CancellationToken.None));
        Assert.Null(await factory.Services.GetRequiredService<INetworkStorageStore>().ReadProjectAsync(project.ProjectId, CancellationToken.None));
    }
}
