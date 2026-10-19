using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// Opt-in library enforcement over HTTP: legacy clients are never refused, the release-feed overlay is
/// report-only, and only a per-project minimum or an embedded blocklist range returns HTTP 426.
/// </summary>
public abstract class LibraryPolicyHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected LibraryPolicyHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private SelfHostFactory WithAdvertisedPolicy()
    {
        var matrix = new LibraryMatrix(
            Minimum: null,
            Recommended: "1.2.0",
            Libraries: [],
            Deprecated: [new MatrixRule("<1.1.0", "2020-10-01", null, null)],
            Blocked: [new MatrixRule("1.2.1", null, "Test security incident", "https://github.com/sbox-cool/sbox-network-storage-server/issues/63")],
            Tested: []);
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(new LibraryCompatProvider(matrix))));
    }

    [SkippableFact]
    public async Task Legacy_header_and_body_versions_preserve_handshake_and_are_observed_in_logs_and_analytics()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        foreach (var version in new[] { "1.0", "1.1.0", "garbage" })
        {
            using var response = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { networkStorageVersion = version, revisionId = 42 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(BuildInfo.Version, Header(response, CompatHeaders.ServerVersion));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var log = await scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>()
                .ListStorageRequestLogAsync(project.ProjectId, 20, CancellationToken.None);
            Assert.Contains(log, row => row.GetProperty("library_version").GetString() == "1.1.0");
        }
        await factory.Services.GetRequiredService<ClientVersionWriter>().FlushAsync(CancellationToken.None);
        var report = await factory.Services.GetRequiredService<ClientVersionsQuery>().ReadAsync(project.ProjectId, 1, null, CancellationToken.None);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "legacy" && row.Requests == 2);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "1.1.0" && row.Requests == 1);
    }

    [SkippableFact]
    public async Task Explicit_project_floor_blocks_runtime_but_leaves_management_recovery_and_auth_errors_unchanged()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-ns-library-version", "1.1.0");
        using var blocked = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.UpgradeRequired, blocked.StatusCode);
        Assert.Equal("unsupported", Header(blocked, CompatHeaders.LibraryStatus));
        using var error = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("LIBRARY_UNSUPPORTED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var recovery = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { revisionId = 42 });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        using var authError = await client.GetAsync($"/v3/values/{project.ProjectId}");
        Assert.Equal(HttpStatusCode.Unauthorized, authError.StatusCode);
    }

    [SkippableFact]
    public async Task Legacy_client_with_a_project_minimum_is_never_refused()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        foreach (var version in new string?[] { null, "garbage", "1.0" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
            if (version is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, version);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unknown", Header(response, CompatHeaders.LibraryStatus));
        }
    }

    [SkippableFact]
    public async Task Feed_overlay_block_is_report_only_and_never_refuses()
    {
        var project = await factory.CreateProjectAsync();
        var provider = factory.Services.GetRequiredService<LibraryCompatProvider>();
        provider.SetOverlay(new LibraryCompatOverlay(Blocked: [new MatrixRule("1.1.0", null, "Feed-only block", null)]));
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("x-ns-library-version", "1.1.0");
            using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("blocked", Header(response, CompatHeaders.LibraryStatus));
        }
        finally
        {
            provider.SetOverlay(null);
        }
    }

    [SkippableTheory]
    [InlineData(null, HttpStatusCode.OK, "unknown")]
    [InlineData("1.0.0", HttpStatusCode.OK, "deprecated")]
    [InlineData("1.1.0", HttpStatusCode.OK, "outdated")]
    [InlineData("1.2.0", HttpStatusCode.OK, "supported")]
    [InlineData("1.2.1", HttpStatusCode.UpgradeRequired, "blocked")]
    public async Task Recommendations_and_deprecation_do_not_break_games_but_embedded_blocks_do(string? version, HttpStatusCode status, string libraryStatus)
    {
        using var host = WithAdvertisedPolicy();
        var project = await host.CreateProjectAsync();
        using var client = host.CreateClient();
        if (version is not null) client.DefaultRequestHeaders.Add("x-ns-library-version", version);
        using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(libraryStatus, Header(response, CompatHeaders.LibraryStatus));
        if (status == HttpStatusCode.UpgradeRequired)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("LIBRARY_BLOCKED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Blocked_package_push_retains_missing_public_and_restricted_key_auth_contracts()
    {
        using var host = WithAdvertisedPolicy();
        var project = await host.CreateProjectAsync();
        string restricted;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var (_, key) = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().CreateProjectKeyAsync(
                1, project.ProjectId, "restricted", "secret", new() { ["settings"] = "none" }, CancellationToken.None);
            restricted = key;
        }
        using var client = host.CreateClient();
        foreach (var (key, expected) in new[] { ((string?)null, HttpStatusCode.Unauthorized), (project.PublicKey, HttpStatusCode.Unauthorized), (restricted, HttpStatusCode.Forbidden), (project.SecretKey, HttpStatusCode.UpgradeRequired) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("x-ns-library-version", "1.2.1");
            if (key is not null) request.Headers.Add("x-api-key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Policy_lookup_failure_fails_open_for_versioned_clients()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<INetworkStorageProjectService>();
            services.AddScoped<INetworkStorageProjectService>(_ => new ThrowingPolicyLookupProjectService());
        }));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, "1.1.0");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Legacy_request_skips_key_resolution_and_the_project_lookup()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        var counting = new CountingProjectService();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<INetworkStorageProjectService>();
            services.AddScoped<INetworkStorageProjectService>(_ => counting);
        }));
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, counting.ResolveProjectAccessCalls);
    }

    /// <summary>The values route never calls the project service, so every member throws.</summary>
    private class UnusableProjectService : INetworkStorageProjectService
    {
        public virtual Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken cancellationToken, string? hostingProfile = null) => throw new NotSupportedException();
        public virtual Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<WorkspaceProject?> FindProjectAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<NetworkStorageTeamData?> GetProjectTeamAsync(long storageOwnerUserId, string projectId, string? organizationId, string callerRole, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(long storageOwnerUserId, string projectId, string label, string keyType, Dictionary<string, string>? permissions, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task UpdateProjectKeyPermissionsAsync(long storageOwnerUserId, string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task UpdateProjectSettingsAsync(long storageOwnerUserId, string projectId, string settingsTab, Dictionary<string, string> formValues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task SaveEndpointRateLimitsAsync(long storageOwnerUserId, string projectId, Dictionary<string, object> endpointRateLimits, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task SaveRateLimitRulesAsync(long storageOwnerUserId, string projectId, IReadOnlyList<RateLimitRule> rules, CancellationToken cancellationToken) => throw new NotSupportedException();
        public virtual Task<ProjectAuditLogResult> BrowseProjectLogsAsync(long storageOwnerUserId, string projectId, string? search, string? action, string? date, string sort, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingPolicyLookupProjectService : UnusableProjectService
    {
        public override Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken) => throw new InvalidOperationException("Policy lookup unavailable.");
    }

    private sealed class CountingProjectService : UnusableProjectService
    {
        private int _resolveProjectAccessCalls;
        public int ResolveProjectAccessCalls => _resolveProjectAccessCalls;
        public override Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resolveProjectAccessCalls);
            return Task.FromResult<NetworkStorageProjectAccessResult?>(null);
        }
    }
}

public sealed class SqliteLibraryPolicyHttpTests(SqliteHostFactory factory) : LibraryPolicyHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresLibraryPolicyHttpTests(PostgresHostFactory factory) : LibraryPolicyHttpTests<PostgresHostFactory>(factory);
