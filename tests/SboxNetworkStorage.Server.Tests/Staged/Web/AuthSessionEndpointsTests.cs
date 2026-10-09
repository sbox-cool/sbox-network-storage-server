using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Live-route tests for the .NET-native Network Storage auth-session family
/// (<c>POST /v{1,3}/{auth-sessions,sessions}/{projectId}/{create,refresh,reauth,revoke}</c>).
/// Proves the Bun→.NET cutover: stateless HMAC tokens are minted/validated natively,
/// s&amp;box auth is checked via the injected verifier, and the Bun wire contract is
/// preserved (HTTP 200 with logical status in the body, <c>X-Request-Id</c> header).
/// </summary>
public abstract class AuthSessionEndpointsTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ProjectId = "proj-1";
    private const string ApiKey = "test-key";
    private const string SteamId = "76561198000000001";

    private readonly SelfHostFactory _factory;

    protected AuthSessionEndpointsTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private HttpClient CreateClient(bool projectEnabled = true, bool authSessionsEnabled = true, SboxAuthResult? sboxResult = null)
    {
        var sbox = sboxResult ?? new SboxAuthResult(true, SteamId, null);
        return _factory.WithWebHostBuilder(builder =>
        {
            // A dead Bun storage-api port: a 502 would prove the request proxied to Bun.
            
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "integration-test-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService(projectEnabled, authSessionsEnabled));
                services.RemoveAll<ISboxAuthVerifier>();
                services.AddScoped<ISboxAuthVerifier>(_ => new FakeSboxAuthVerifier(sbox));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static HttpRequestMessage CreateRequest(string path, string? apiKey = ApiKey, string? steamId = SteamId, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        if (steamId is not null) request.Headers.Add("x-steam-id", steamId);
        if (token is not null) request.Headers.Add("x-auth-session", token);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> MintTokenAsync(HttpClient client)
    {
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));
        var body = await BodyAsync(response);
        return body.GetProperty("sessionToken").GetString()!;
    }

    [SkippableFact]
    public async Task Create_WithValidKeyAndSboxAuth_MintsNativeToken()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));

        // Wire-contract parity: HTTP 200 always; logical status lives in the body.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out _));

        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(200, body.GetProperty("status").GetInt32());
        Assert.StartsWith("sbox_sess_", body.GetProperty("sessionToken").GetString());
        Assert.Equal(SteamId, body.GetProperty("session").GetProperty("steamId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").GetProperty("revokedAt").ValueKind);
    }

    [SkippableFact]
    public async Task Create_MissingApiKey_ReturnsNativeUnauthorized_NotBunProxy()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create", apiKey: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // 502 would mean it hit the dead Bun proxy
        var body = await BodyAsync(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Create_ProjectDisabled_ReturnsProjectDisabled()
    {
        using var client = CreateClient(projectEnabled: false);
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));

        var body = await BodyAsync(response);
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("PROJECT_DISABLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Create_AuthSessionsDisabled_ReturnsAuthSessionDisabled()
    {
        using var client = CreateClient(authSessionsEnabled: false);
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));

        var body = await BodyAsync(response);
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("AUTH_SESSION_DISABLED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Create_ImplausibleSteamId_ReturnsInvalidSteamId()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create", steamId: "abc"));

        var body = await BodyAsync(response);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("INVALID_STEAMID", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Create_SboxAuthFails_ReturnsSboxAuthFailed()
    {
        using var client = CreateClient(sboxResult: new SboxAuthResult(false, null, "token rejected"));
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));

        var body = await BodyAsync(response);
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("SBOX_AUTH_FAILED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Create_SboxSteamIdMismatch_ReturnsMismatch()
    {
        using var client = CreateClient(sboxResult: new SboxAuthResult(true, "76561198000009999", null));
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/create"));

        var body = await BodyAsync(response);
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("AUTH_SESSION_STEAMID_MISMATCH", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task SessionsAlias_And_V1Alias_AlsoServeNatively()
    {
        using var client = CreateClient();
        foreach (var path in new[] { $"/v3/sessions/{ProjectId}/create", $"/v1/auth-sessions/{ProjectId}/create", $"/v1/sessions/{ProjectId}/create" })
        {
            using var response = await client.SendAsync(CreateRequest(path));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await BodyAsync(response);
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.StartsWith("sbox_sess_", body.GetProperty("sessionToken").GetString());
        }
    }

    [SkippableFact]
    public async Task Refresh_WithMintedToken_IssuesNewToken()
    {
        using var client = CreateClient();
        var token = await MintTokenAsync(client);

        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/refresh", steamId: null, token: token));
        var body = await BodyAsync(response);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.StartsWith("sbox_sess_", body.GetProperty("sessionToken").GetString());
        Assert.Equal(SteamId, body.GetProperty("session").GetProperty("steamId").GetString());
    }

    [SkippableFact]
    public async Task Refresh_MissingToken_ReturnsAuthSessionRequired()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/refresh", steamId: null));

        var body = await BodyAsync(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("AUTH_SESSION_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Reauth_WithMintedToken_RevalidatesAndRefreshes()
    {
        using var client = CreateClient();
        var token = await MintTokenAsync(client);

        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/reauth", token: token));
        var body = await BodyAsync(response);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.StartsWith("sbox_sess_", body.GetProperty("sessionToken").GetString());
    }

    [SkippableFact]
    public async Task Revoke_WithMintedToken_ReturnsRevokedSession()
    {
        using var client = CreateClient();
        var token = await MintTokenAsync(client);

        using var response = await client.SendAsync(CreateRequest($"/v3/auth-sessions/{ProjectId}/revoke", steamId: null, token: token));
        var body = await BodyAsync(response);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.String, body.GetProperty("session").GetProperty("revokedAt").ValueKind);
    }

    private sealed class FakeSboxAuthVerifier(SboxAuthResult result) : ISboxAuthVerifier
    {
        public Task<SboxAuthResult> CheckAsync(SboxAuthCheck check, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class FakeKeyResolver(string validKey, string projectId) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult(string.Equals(apiKey, validKey, StringComparison.Ordinal) && string.Equals(project, projectId, StringComparison.Ordinal)
                ? new StorageApiKeyAuthResult(42, project, true, "secret")
                : null);
    }

    private sealed class FakeProjectService(bool enabled, bool authSessions) : INetworkStorageProjectService
    {
        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageProjectAccessResult?>(new NetworkStorageProjectAccessResult(
                new WorkspaceProject(projectId, "Test Project", null, Enabled: enabled, null, null, null, EnableAuthSessions: authSessions, AuthSessionTtlSeconds: 3600),
                Organization: null, StorageOwnerUserId: userId,
                RequireSboxAuth: false, PlayerKeyMode: null, CanManage: true));

        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<NetworkStorageProjectResources?> GetProjectResourcesAsync(long userId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<NetworkStorageProjectResources?> GetProjectResourcesForOwnerAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<NetworkStorageTeamData?> GetProjectTeamAsync(long storageOwnerUserId, string projectId, string? organizationId, string callerRole, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApiKeyInfo>> GetProjectKeysAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<(ApiKeyInfo Key, string RawKey)> CreateProjectKeyAsync(long storageOwnerUserId, string projectId, string label, string keyType, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task ToggleProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task RemoveProjectKeyAsync(long storageOwnerUserId, string projectId, string key, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UpdateProjectKeyPermissionsAsync(long storageOwnerUserId, string projectId, string keyIdentifier, Dictionary<string, string> permissions, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task DeleteProjectAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UpdateProjectSettingsAsync(long storageOwnerUserId, string projectId, string settingsTab, Dictionary<string, string> formValues, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectUsageData> GetProjectUsageAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectRateLimits> GetProjectRateLimitsAsync(long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task SaveEndpointRateLimitsAsync(long storageOwnerUserId, string projectId, Dictionary<string, object> endpointRateLimits, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task SaveRateLimitRulesAsync(long storageOwnerUserId, string projectId, IReadOnlyList<RateLimitRule> rules, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<ProjectAuditLogResult> BrowseProjectLogsAsync(long storageOwnerUserId, string projectId, string? search, string? action, string? date, string sort, int page, int pageSize, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}

public sealed class AuthSessionEndpointsTests_Sqlite(SqliteHostFactory factory) : AuthSessionEndpointsTests<SqliteHostFactory>(factory);

public sealed class AuthSessionEndpointsTests_Postgres(PostgresHostFactory factory) : AuthSessionEndpointsTests<PostgresHostFactory>(factory);
