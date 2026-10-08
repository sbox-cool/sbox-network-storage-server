using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

// Real HTTP ingress, project/key services, signed sessions, native executor and
// relational persistence. Only the external Facepunch transport is deterministic.
public abstract class EndpointPlayerIdentityHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Player = "76561198000000001";
    private const string Victim = "76561198000000099";
    private const string Host = "76561198000000002";
    private const string Definition = """
        {"exposure":"public","steps":[{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"marker","value":"executed"}]}],"response":{"status":200,"body":{"ok":true,"caller":"{{steamId}}","dedicated":"{{_isDedicatedServer}}","secret":"{{_hasSecretKey}}"}}}
        """;
    private readonly SelfHostFactory _factory;

    protected EndpointPlayerIdentityHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableTheory]
    [InlineData("/v3", false, "x-steam-id")]
    [InlineData("/v1", false, "x-sbox-steam-id")]
    [InlineData("/v3", true, "steamId")]
    [InlineData("/v1", true, "steamid")]
    [InlineData("/v3", false, "body")]
    public async Task RequiredAuthRejectsForgedIdentityBeforeMutation(string version, bool bodySlug, string identity)
    {
        using var setup = await CreateAsync();
        using var request = Request(setup, identity, Victim, version: version, bodySlug: bodySlug);
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "SBOX_AUTH_FAILED");
        await AssertUntouchedAsync(setup);
        Assert.Equal(0, setup.Facepunch.Calls);
    }

    [SkippableTheory]
    [InlineData("x-steam-id", "x-sbox-token")]
    [InlineData("x-sbox-steam-id", "x-sbox-auth-token")]
    [InlineData("steamId", "query")]
    [InlineData("steamid", "body")]
    [InlineData("body", "body")]
    [InlineData("numeric-body", "query")]
    public async Task FacepunchVerifiedCallerExecutesWithBoundIdentity(string identity, string credential)
    {
        using var setup = await CreateAsync();
        using var request = Request(setup, identity, Player, credential, "player-token");
        using var response = await setup.Client.SendAsync(request);
        await AssertExecutedAsync(setup, response, Player);
        Assert.Equal(1, setup.Facepunch.Calls);
    }

    [SkippableTheory]
    [InlineData("x-auth-session", "none")]
    [InlineData("x-auth-session-token", "x-steam-id")]
    [InlineData("bearer", "x-sbox-steam-id")]
    [InlineData("authSessionToken-query", "steamId")]
    [InlineData("sessionToken-query", "steamid")]
    [InlineData("authSessionToken-body", "body")]
    [InlineData("sessionToken-body", "numeric-body")]
    public async Task SignedSessionDerivesAndBindsPlayerAcrossSupportedLocations(string credential, string identity)
    {
        using var setup = await CreateAsync();
        var token = setup.Sessions.Create(NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, Player, 3600).Token!;
        using var request = Request(setup, identity, Player, credential, token, bodySlug: true);
        using var response = await setup.Client.SendAsync(request);
        await AssertExecutedAsync(setup, response, Player);
        Assert.Equal(0, setup.Facepunch.Calls);
    }

    [SkippableTheory]
    [InlineData("missing-token", "SBOX_AUTH_FAILED")]
    [InlineData("expired-token", "SBOX_AUTH_FAILED")]
    [InlineData("player-token", "SBOX_AUTH_FAILED")]
    [InlineData("invalid-session", "AUTH_SESSION_INVALID")]
    [InlineData("expired-session", "AUTH_SESSION_EXPIRED")]
    [InlineData("other-project-session", "AUTH_SESSION_INVALID")]
    [InlineData("other-owner-session", "AUTH_SESSION_INVALID")]
    [InlineData("player-session", "AUTH_SESSION_STEAMID_MISMATCH")]
    public async Task InvalidExpiredAndMismatchedCredentialsCannotMutate(string kind, string code)
    {
        using var setup = await CreateAsync();
        var session = kind.EndsWith("session", StringComparison.Ordinal);
        var token = kind switch
        {
            "invalid-session" => "sbox_sess_invalid.signature",
            "expired-session" => new NetworkStorageAuthSessionService(setup.Secrets, new PastTimeProvider())
                .Create(NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, Player, 60).Token!,
            "other-project-session" => setup.Sessions.Create(NetworkStorageServices.LocalOwnerUserId, "another-project", Victim, 3600).Token!,
            "other-owner-session" => setup.Sessions.Create(987654, setup.Project.ProjectId, Victim, 3600).Token!,
            "player-session" => setup.Sessions.Create(NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, Player, 3600).Token!,
            _ => kind
        };
        using var request = Request(setup, "x-steam-id", Victim, session ? "bearer" : "x-sbox-token", token);
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, code);
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData("expired-token", Player, "SBOX_AUTH_FAILED")]
    [InlineData("player-token", Victim, "AUTH_SESSION_STEAMID_MISMATCH")]
    public async Task ValidSessionCannotMaskInvalidOrConflictingFacepunchCredentials(string token, string sessionPlayer, string code)
    {
        using var setup = await CreateAsync();
        using var request = Request(setup, "x-steam-id", Player, "x-sbox-token", token);
        request.Headers.Add("x-auth-session",
            setup.Sessions.Create(NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, sessionPlayer, 3600).Token!);
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, code);
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData("x-sbox-steam-id")]
    [InlineData("steamId")]
    [InlineData("steamid")]
    [InlineData("body")]
    public async Task LowerPrecedenceForgedClaimIsNotIgnored(string conflictingIdentity)
    {
        using var setup = await CreateAsync();
        using var request = Request(setup, conflictingIdentity, Victim, "x-sbox-token", "player-token");
        request.Headers.Add("x-steam-id", Player);
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTH_SESSION_STEAMID_MISMATCH");
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SecretKeyDeliberatelyDelegatesPlayerWithoutPlayerCredentials(bool requiredAuth)
    {
        using var setup = await CreateAsync(requiredAuth);
        using var request = Request(setup, "body", Victim);
        request.Headers.Remove("x-api-key");
        request.Headers.Add("x-secret-key", setup.Project.SecretKey);
        request.Headers.Add("x-public-key", setup.Project.PublicKey);
        using var response = await setup.Client.SendAsync(request);
        await AssertExecutedAsync(setup, response, Victim, secret: true);
        Assert.Equal(0, setup.Facepunch.Calls);
    }

    // Regression: host proxies (NetworkStorageHostProxyClient) send their own
    // x-steam-id plus x-on-behalf-of. Ignoring the delegation wrote the guest's
    // data into the host's own record. Bun trusts it for secret keys and
    // auth-disabled projects.
    [SkippableTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task TrustedHostProxyActsForOnBehalfOfPlayerNotHost(bool secret, bool requiredAuth)
    {
        using var setup = await CreateAsync(requiredAuth);
        using var request = Request(setup, "x-steam-id", Host);
        request.Headers.Add("x-on-behalf-of", Player);
        if (secret)
        {
            request.Headers.Remove("x-api-key");
            request.Headers.Add("x-secret-key", setup.Project.SecretKey);
            request.Headers.Add("x-public-key", setup.Project.PublicKey);
        }
        using var response = await setup.Client.SendAsync(request);
        await AssertExecutedAsync(setup, response, Player, secret);
        Assert.Null(await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Host, CancellationToken.None));
    }

    [SkippableFact]
    public async Task TrustedHostProxyRejectsImplausibleOnBehalfOfBeforeMutation()
    {
        using var setup = await CreateAsync(required: false);
        using var request = Request(setup, "x-steam-id", Host);
        request.Headers.Add("x-on-behalf-of", "not-a-steam-id");
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "INVALID_STEAMID");
        await AssertUntouchedAsync(setup);
    }

    [SkippableFact]
    public async Task AuthDisabledProjectIgnoresUnverifiableTokensMatchingLegacyPassthrough()
    {
        // Bun parity: auth-disabled projects ignore s&box tokens entirely, so a
        // placeholder/invalid token must not fail or trigger verification.
        using var setup = await CreateAsync(required: false);
        using var invalid = Request(setup, "body", Victim, "x-sbox-token", "invalid");
        using var response = await setup.Client.SendAsync(invalid);
        await AssertExecutedAsync(setup, response, Victim);
        Assert.Equal(0, setup.Facepunch.Calls);
        using var anonymousAuth = Request(setup, "body", Victim);
        using var second = await setup.Client.SendAsync(anonymousAuth);
        await AssertExecutedAsync(setup, second, Victim);
    }

    [SkippableFact]
    public async Task SessionRequiresProjectOptInEvenWhenSignatureIsValid()
    {
        using var setup = await CreateAsync(sessions: false);
        var token = setup.Sessions.Create(NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, Player, 3600).Token!;
        using var request = Request(setup, "none", Player, "bearer", token);
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "AUTH_SESSION_DISABLED");
        await AssertUntouchedAsync(setup);
    }

    [SkippableTheory]
    [InlineData("player-token", true)]
    [InlineData("expired-token", false)]
    public async Task PublicProxyRequiresVerifiedClientNotJustValidHostAndSignature(string clientToken, bool accepted)
    {
        using var setup = await CreateAsync();
        using var request = Request(setup, "x-steam-id", Host, "x-sbox-token", "host-token");
        request.Headers.Add("x-on-behalf-of", Player);
        request.Headers.Add("x-on-behalf-of-token", clientToken);
        var data = $"{setup.Project.ProjectId}:save:{Player}:{clientToken}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(setup.Project.PublicKey));
        request.Headers.Add("x-proxy-signature", Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant());
        using var response = await setup.Client.SendAsync(request);
        if (accepted) await AssertExecutedAsync(setup, response, Player);
        else
        {
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "SBOX_AUTH_FAILED");
            await AssertUntouchedAsync(setup);
        }
        Assert.Equal(2, setup.Facepunch.Calls);
    }

    [SkippableTheory]
    [InlineData("exposure", "internal", 404, "ENDPOINT_NOT_FOUND", false)]
    [InlineData("enabled", false, 403, "ENDPOINT_DISABLED", false)]
    [InlineData("deprecated", true, 410, "ENDPOINT_DEPRECATED", false)]
    [InlineData("requiresSecretKey", true, 401, "SECRET_KEY_REQUIRED", false)]
    [InlineData("exposure", "internal", 404, "ENDPOINT_NOT_FOUND", true)]
    public async Task AuthenticationDoesNotBypassEndpointExposureGates(string gate, object value, int status, string code, bool secret)
    {
        using var setup = await CreateAsync();
        var definition = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Definition)!;
        definition[gate] = JsonSerializer.SerializeToElement(value);
        await setup.Store.UpsertEndpointAsync(setup.Project.ProjectId, "save", "save", "POST", true,
            JsonSerializer.SerializeToElement(definition), null, 1, CancellationToken.None);
        using var request = Request(setup, "x-steam-id", Player, secret ? null : "x-sbox-token", "player-token");
        if (secret)
        {
            request.Headers.Remove("x-api-key");
            request.Headers.Add("x-secret-key", setup.Project.SecretKey);
        }
        using var response = await setup.Client.SendAsync(request);
        await AssertErrorAsync(response, (HttpStatusCode)status, code);
        await AssertUntouchedAsync(setup);
    }

    [SkippableFact]
    public async Task DisabledProjectStillRejectsSecretDelegation()
    {
        using var setup = await CreateAsync();
        await using var scope = setup.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, "project", new() { ["name"] = "Disabled" }, CancellationToken.None);
        using var request = Request(setup, "x-steam-id", Victim);
        request.Headers.Remove("x-api-key");
        request.Headers.Add("x-secret-key", setup.Project.SecretKey);
        using var response = await setup.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PROJECT_DISABLED", body.RootElement.GetProperty("error").GetString());
        await AssertUntouchedAsync(setup);
    }

    private async Task<Setup> CreateAsync(bool required = true, bool sessions = true)
    {
        var facepunch = new FacepunchTransport();
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<ISboxAuthVerifier, FacepunchSboxAuthVerifier>().ConfigurePrimaryHttpMessageHandler(() => facepunch)));
        var project = await factory.CreateProjectAsync(requireSboxAuth: required);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "security", new()
            {
                ["requireSboxAuth"] = required ? "true" : "false",
                ["enableAuthSessions"] = sessions ? "true" : "false"
            }, CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "players", "private",
            JsonSerializer.SerializeToElement(new { collectionType = "player" }), 1, CancellationToken.None);
        await store.UpsertEndpointAsync(project.ProjectId, "save", "save", "POST", true,
            JsonDocument.Parse(Definition).RootElement, null, 1, CancellationToken.None);
        await store.UpsertRecordAsync(project.ProjectId, "players", Victim,
            JsonSerializer.SerializeToElement(new { marker = "untouched" }), false, 1, CancellationToken.None);
        var before = (await store.ReadRecordAsync(project.ProjectId, "players", Victim, CancellationToken.None))!.Value.GetRawText();
        return new Setup(factory, factory.CreateClient(), project, store,
            factory.Services.GetRequiredService<INetworkStorageAuthSessionService>(),
            factory.Services.GetRequiredService<IAuthSessionSecretProvider>(), facepunch, before);
    }

    private static HttpRequestMessage Request(Setup setup, string identity, string steamId,
        string? credential = null, string token = "", string version = "/v3", bool bodySlug = false)
    {
        var body = new Dictionary<string, object?> { ["endpoint"] = "save" };
        var query = new List<string>();
        var request = new HttpRequestMessage(HttpMethod.Post, "");
        request.Headers.Add("x-api-key", setup.Project.PublicKey);
        if (identity.StartsWith("x-", StringComparison.Ordinal)) request.Headers.Add(identity, steamId);
        else if (identity == "body") body["steamId"] = steamId;
        else if (identity == "numeric-body") body["steamId"] = long.Parse(steamId, System.Globalization.CultureInfo.InvariantCulture);
        else if (identity != "none") query.Add($"{identity}={steamId}");
        if (credential == "bearer") request.Headers.Add("Authorization", "Bearer " + token);
        else if (credential is not null && credential.StartsWith("x-", StringComparison.Ordinal)) request.Headers.Add(credential, token);
        else if (credential == "query") query.Add("token=" + Uri.EscapeDataString(token));
        else if (credential == "body") body["token"] = token;
        else if (credential is not null && credential.EndsWith("-query", StringComparison.Ordinal)) query.Add(credential[..^6] + "=" + Uri.EscapeDataString(token));
        else if (credential is not null && credential.EndsWith("-body", StringComparison.Ordinal)) body[credential[..^5]] = token;
        request.RequestUri = new Uri($"{version}/endpoints/{setup.Project.ProjectId}{(bodySlug ? "" : "/save")}?{string.Join("&", query)}", UriKind.Relative);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task AssertUntouchedAsync(Setup setup)
    {
        var victim = await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Victim, CancellationToken.None);
        Assert.Equal(setup.Before, victim!.Value.GetRawText());
        Assert.Null(await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Player, CancellationToken.None));
        Assert.Null(await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Host, CancellationToken.None));
        Assert.Null(await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", "anonymous", CancellationToken.None));
    }

    private static async Task AssertExecutedAsync(Setup setup, HttpResponseMessage response, string steamId, bool secret = false)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(steamId, body.RootElement.GetProperty("caller").GetString());
        Assert.Equal(secret, body.RootElement.GetProperty("dedicated").GetBoolean());
        Assert.Equal(secret, body.RootElement.GetProperty("secret").GetBoolean());
        var record = await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", steamId, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal("executed", record.Value.GetProperty("payload_json").GetProperty("marker").GetString());
        if (steamId != Victim)
            Assert.Equal(setup.Before, (await setup.Store.ReadRecordAsync(setup.Project.ProjectId, "players", Victim, CancellationToken.None))!.Value.GetRawText());
    }

    private sealed record Setup(SelfHostFactory Factory, HttpClient Client, SelfHostProject Project,
        INetworkStorageStore Store, INetworkStorageAuthSessionService Sessions, IAuthSessionSecretProvider Secrets,
        FacepunchTransport Facepunch, string Before) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }
    }

    private sealed class PastTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1);
    }

    private sealed class FacepunchTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://public.facepunch.com/sbox/auth/token", request.RequestUri!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var verified = body.RootElement.GetProperty("token").GetString() switch
            {
                "player-token" => Player,
                "host-token" => Host,
                _ => null
            };
            return new HttpResponseMessage(verified is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(verified is null ? "{}" : $"{{\"Status\":\"ok\",\"SteamId\":{verified}}}", Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class SqliteEndpointPlayerIdentityHttpTests(SqliteHostFactory factory)
    : EndpointPlayerIdentityHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresEndpointPlayerIdentityHttpTests(PostgresHostFactory factory)
    : EndpointPlayerIdentityHttpTests<PostgresHostFactory>(factory);
