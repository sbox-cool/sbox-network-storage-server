using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NetworkStorageAuthSessionCandidateTests
{
    [Fact]
    public void CanHandle_ReturnsTrue_ForPostAuthSessionRoutes()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));

        foreach (var (template, action) in GetRouteTemplates())
        {
            var route = NetworkStorageRouteClassifier.Classify("POST", template);
            Assert.True(handler.CanHandle(route),
                $"Expected CanHandle=true for {template}");
        }
    }

    [Fact]
    public void CanHandle_ReturnsFalse_ForNonPostVerb()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));
        foreach (var verb in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var route = NetworkStorageRouteClassifier.Classify(verb, "/v3/auth-sessions/proj1/create");
            Assert.False(handler.CanHandle(route),
                $"Expected CanHandle=false for {verb} /v3/auth-sessions/...");
        }
    }

    [Fact]
    public void CanHandle_ReturnsFalse_ForNonAuthSessionFamily()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));
        var route = NetworkStorageRouteClassifier.Classify("GET", "/v3/values/proj1");
        Assert.False(handler.CanHandle(route));
    }

    [Fact]
    public void Family_ReturnsAuthSession()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));
        Assert.Equal(NetworkStorageRouteFamily.AuthSession, handler.Family);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_ReturnsUnauthorized()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));

        foreach (var template in GetTemplatePaths())
        {
            var result = await handler.ExecuteAsync(BuildRequest(template, apiKey: null));

            Assert.Equal(401, result.StatusCode);
            Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
            Assert.Equal("denied", result.AuthDecision);
            Assert.Empty(result.IntendedWritePaths);

            var json = JsonSerializer.SerializeToElement(result.Body);
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Equal("UNAUTHORIZED", json.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidApiKey_ReturnsUnauthorized()
    {
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(null));

        var result = await handler.ExecuteAsync(BuildRequest("/v3/auth-sessions/proj1/create", apiKey: "bad-key"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("UNAUTHORIZED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
        Assert.Empty(result.IntendedWritePaths);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledProject_ReturnsProjectDisabled()
    {
        var disabledAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: false, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(disabledAuth));

        var result = await handler.ExecuteAsync(BuildRequest("/v3/auth-sessions/proj1/create", apiKey: "valid-key"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("PROJECT_DISABLED", result.PublicErrorCode);
        Assert.Equal("denied", result.AuthDecision);
        Assert.Empty(result.IntendedWritePaths);
    }

    [Fact]
    public async Task CreateAction_MissingSteamId_ReturnsInvalidSteamId()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/create", apiKey: "valid-key"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("INVALID_STEAMID", result.PublicErrorCode);
        Assert.Empty(result.IntendedWritePaths);
    }

    [Fact]
    public async Task CreateAction_InvalidSteamId_ReturnsInvalidSteamId()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/create", apiKey: "valid-key", steamId: "not-a-number"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("INVALID_STEAMID", result.PublicErrorCode);
    }

    [Fact]
    public async Task CreateAction_ValidRequest_ReturnsDryRunWithIntendedWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/create", apiKey: "valid-key", steamId: "76561197960287930"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("allowed", result.AuthDecision);

        // Must declare intended write path but NOT actually write
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("auth-sessions/proj1/session__create"));
        Assert.Empty(result.StoragePathsRead);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("candidate", json.GetProperty("source").GetString());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());
        Assert.Equal("create", json.GetProperty("action").GetString());

        // Must NOT contain a real session token
        Assert.False(json.TryGetProperty("sessionToken", out _));
    }

    [Fact]
    public async Task RefreshAction_MissingToken_ReturnsAuthSessionRequired()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/refresh", apiKey: "valid-key"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("AUTH_SESSION_REQUIRED", result.PublicErrorCode);
        Assert.Empty(result.IntendedWritePaths);
    }

    [Fact]
    public async Task RefreshAction_ValidRequest_ReturnsDryRunWithIntendedWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/sessions/proj1/refresh",
                apiKey: "valid-key",
                authSessionToken: "sbox_sess_test-token-value"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("session__refresh"));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("refresh", json.GetProperty("action").GetString());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReauthAction_MissingToken_ReturnsAuthSessionRequired()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/sessions/proj1/reauth", apiKey: "valid-key"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("AUTH_SESSION_REQUIRED", result.PublicErrorCode);
    }

    [Fact]
    public async Task ReauthAction_ValidRequest_ReturnsDryRunWithIntendedWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/reauth",
                apiKey: "valid-key",
                authSessionToken: "sbox_sess_test-token"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("session__reauth"));

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("reauth", json.GetProperty("action").GetString());
    }

    [Fact]
    public async Task RevokeAction_MissingToken_ReturnsAuthSessionRequired()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/revoke", apiKey: "valid-key"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("AUTH_SESSION_REQUIRED", result.PublicErrorCode);
    }

    [Fact]
    public async Task RevokeAction_ValidRequest_ReturnsDryRunWithIntendedWritePath()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        var result = await handler.ExecuteAsync(
            BuildRequest("/v3/auth-sessions/proj1/revoke",
                apiKey: "valid-key",
                authSessionToken: "sbox_sess_test-token"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Contains(result.IntendedWritePaths, p => p.Contains("session__revoke"));
        Assert.Empty(result.StoragePathsRead);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("revoke", json.GetProperty("action").GetString());
        Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task LegacyAlias_ClassifiedAsAuthSession()
    {
        // The legacy /v3/sessions/:projectId/... alias routes classify as AuthSession family.
        var route = NetworkStorageRouteClassifier.Classify("POST", "/v3/sessions/proj1/refresh");
        Assert.Equal(NetworkStorageRouteFamily.AuthSession, route.Family);
    }

    [Fact]
    public async Task AllFourActions_ClassifyCorrectly_AndSuppressWrites()
    {
        var validAuth = new StorageApiKeyAuthResult(
            UserId: 42, ProjectId: "proj1", Enabled: true, KeyType: "secret");
        var handler = new AuthSessionCandidateHandler(new FakeApiKeyResolver(validAuth));

        foreach (var action in new[] { "create", "refresh", "reauth", "revoke" })
        {
            var template = $"/v3/auth-sessions/proj1/{action}";
            var apiKey = action == "create" ? "valid-key" : "valid-key";
            var steamId = action == "create" ? "76561197960287930" : null;
            var token = action != "create" ? "sbox_sess_dry-run-token" : null;

            var result = await handler.ExecuteAsync(
                BuildRequest(template, apiKey: apiKey, steamId: steamId, authSessionToken: token));

            // All valid requests return 200 dry-run
            Assert.Equal(200, result.StatusCode);
            Assert.Contains(result.IntendedWritePaths, p => p.Contains($"session__{action}"));

            var json = JsonSerializer.SerializeToElement(result.Body);
            Assert.Equal(action, json.GetProperty("action").GetString());
            Assert.Equal("write_suppressed_dry_run", json.GetProperty("reason").GetString());

            // No real session token
            Assert.False(json.TryGetProperty("sessionToken", out _));
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns all AuthSession route template paths for full coverage.</summary>
    private static IEnumerable<string> GetTemplatePaths()
    {
        foreach (var prefix in new[] { "/v1/auth-sessions", "/v3/auth-sessions", "/v1/sessions", "/v3/sessions" })
        foreach (var action in new[] { "create", "refresh", "reauth", "revoke" })
            yield return $"{prefix}/proj1/{action}";
    }

    /// <summary>Returns (template, action) tuples for CanHandle iteration.</summary>
    private static IEnumerable<(string template, string action)> GetRouteTemplates()
    {
        foreach (var prefix in new[] { "/v1/auth-sessions", "/v3/auth-sessions", "/v1/sessions", "/v3/sessions" })
        foreach (var action in new[] { "create", "refresh", "reauth", "revoke" })
            yield return ($"{prefix}/proj1/{action}", action);
    }

    private static NetworkStorageCandidateRequest BuildRequest(
        string path,
        string? apiKey = null,
        string? steamId = null,
        string? authSessionToken = null,
        string? body = null)
    {
        var method = "POST";
        // Extract projectId from the path — it's the first path segment that looks like a project
        // after the version+namespace prefix.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Segments: e.g. ["v3", "auth-sessions", "proj1", "create"]
        // Project ID is at index 2 (after version and namespace).
        var projectId = segments.Length >= 3 ? segments[2] : "proj1";

        var route = NetworkStorageRouteClassifier.Classify(method, path);

        var query = new Dictionary<string, string>();
        var creds = new NetworkStorageCredentials(
            ApiKey: apiKey,
            SteamId: steamId,
            AuthSessionToken: authSessionToken,
            SessionToken: null,
            EncryptedRequestId: null);

        return new NetworkStorageCandidateRequest(
            route,
            query,
            ContentType: body is not null ? "application/json" : null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: creds,
            Body: body,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Minimal fake for <see cref="IStorageApiKeyResolver"/>. Returns the configured result or null.
    /// </summary>
    private sealed class FakeApiKeyResolver : IStorageApiKeyResolver
    {
        private readonly StorageApiKeyAuthResult? _result;

        public FakeApiKeyResolver(StorageApiKeyAuthResult? result) => _result = result;

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(
            string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }
}
