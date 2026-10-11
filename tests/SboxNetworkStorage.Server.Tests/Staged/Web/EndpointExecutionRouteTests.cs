using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Contracts.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Live-route tests for the .NET-native endpoint execution path
/// (<c>GET /v3/endpoints/{projectId}/{endpointSlug}</c>). These prove the cutover
/// in <c>serve-endpoint-execution-dotnet-native</c>: authenticated GET endpoint
/// execution is served by the native the store executor with NO legacy server proxy in the
/// request path (the dead <c>127.0.0.1:4547</c> that produced the production
/// <c>NetworkStorageGatewayUnavailable</c> 502 is never touched). Identity comes
/// from <c>x-steam-id</c> / <c>steamId</c>, input from query params, and an
/// endpoint the executor cannot run returns a reported 501 — never a legacy server fallback.
/// </summary>
public abstract class EndpointExecutionRouteTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ProjectId = "proj-1";
    private const string ApiKey = "test-key";

    // Public + secret key pair used by the dedicated-server credential tests.
    // The public key resolves to a public-key auth; the secret key resolves to a
    // secret-key auth holding endpoints execute (the permission the user reports
    // on their real key).
    private const string PublicKey = "sbox_ns_test_public";
    private const string SecretKey = "sbox_sk_test_secret";

    // A requiresSecretKey endpoint, mirroring the shape the game's init-player
    // endpoint uses (dedicated-server flows).
    private const string InitPlayerDefinition =
        """{"requiresSecretKey":true,"steps":[{"id":"z","type":"transform","expression":"{{input.zone}}"}],"response":{"status":200,"body":{"ok":true,"zone":"{{z}}"}}}""";

    // The user's SERVER_ONLY gate: an expression-form condition on the documented
    // `_hasSecretKey` builtin. Before the boolean-comparison fix this rejected
    // EVERY request (the token coerced to 1 compared against the string "true" →
    // 1 == NaN → false) with exactly the reported 403 + conditionRejection.
    private const string ServerOnlyDefinition =
        """{"steps":[{"id":"server_only","type":"condition","check":{"expression":"{{_hasSecretKey}} == true"},"onFail":{"action":"reject","status":403,"errorCode":"SERVER_ONLY","errorMessage":"dedicated server only"}},{"id":"z","type":"transform","expression":"{{input.zone}}"}],"response":{"status":200,"body":{"ok":true,"zone":"{{z}}"}}}""";

    // The exact shape the user's YAML compiles to (NetworkStorageSourceResourceCompiler
    // keeps the templated field and the boolean value):
    //   check: { field: "{{_hasSecretKey}}", op: "==", value: true }
    private const string ServerOnlyFieldDefinition =
        """{"steps":[{"id":"server_only","type":"condition","check":{"field":"{{_hasSecretKey}}","op":"==","value":true},"onFail":{"action":"reject","status":403,"errorCode":"SERVER_ONLY","errorMessage":"dedicated server only"}},{"id":"z","type":"transform","expression":"{{input.zone}}"}],"response":{"status":200,"body":{"ok":true,"zone":"{{z}}"}}}""";

    // Definitions compiled BEFORE the compiler boolean fix stored
    // "value": "true" as a STRING — the user's current live shape. The evaluator
    // must tolerate it so existing endpoints work without a re-push.
    private const string ServerOnlyStringifiedValueDefinition =
        """{"steps":[{"id":"server_only","type":"condition","check":{"field":"{{_hasSecretKey}}","op":"==","value":"true"},"onFail":{"action":"reject","status":403,"errorCode":"SERVER_ONLY","errorMessage":"dedicated server only"}},{"id":"z","type":"transform","expression":"{{input.zone}}"}],"response":{"status":200,"body":{"ok":true,"zone":"{{z}}"}}}""";

    private readonly SelfHostFactory _factory;

    protected EndpointExecutionRouteTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    // A read+identity endpoint: reads the player's record from `players` by the
    // resolved steamId and echoes the stored name back.
    private const string GetJoinDefinition =
        """{"steps":[{"id":"player","type":"read","collection":"players","key":"{{steamId}}"}],"response":{"status":200,"body":{"ok":true,"name":"{{player.name}}"}}}""";

    // An input-echo endpoint: no record read; surfaces a query param via {{input.*}}.
    private const string EchoInputDefinition =
        """{"steps":[{"id":"z","type":"transform","expression":"{{input.zone}}"}],"response":{"status":200,"body":{"ok":true,"zone":"{{z}}"}}}""";

    // An endpoint the native executor cannot run (unknown step type).
    private const string UnsupportedDefinition =
        """{"steps":[{"id":"x","type":"custom_unknown_type"}],"response":{"status":200,"body":{"ok":true}}}""";

    private HttpClient CreateClient()
    {
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "get-join", "GET", GetJoinDefinition);
        SeedEndpoint(store, "echo-input", "GET", EchoInputDefinition);
        SeedEndpoint(store, "broken", "GET", UnsupportedDefinition);
        SeedRecord(store, "players", "steamA", new { name = "alpha" });
        SeedRecord(store, "players", "steamB", new { name = "bravo" });

        return _factory.WithWebHostBuilder(builder =>
        {
            // A dead legacy server storage-api port: if the request ever proxied to legacy server the
            // test would see a 502, proving the native path never touches it.
            
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "integration-test-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private HttpClient CreateClient(InMemoryNetworkStorageStore store, IStorageApiKeyResolver resolver)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "integration-test-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => resolver);
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private HttpClient CreateClient(InMemoryNetworkStorageStore store, IErrorArchive archive, IExceptionAlertSink alertSink)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "integration-test-secret");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => store);
                services.RemoveAll<INetworkStorageProjectService>();
                services.AddScoped<INetworkStorageProjectService>(_ => new FakeProjectService());
                // Inject capturing fakes so the 409 reporting path can be asserted
                // end-to-end (archive + Discord alert sink), matching the production
                // wiring in Program.cs.
                services.RemoveAll<IErrorArchive>();
                services.AddSingleton(archive);
                services.RemoveAll<IExceptionAlertSink>();
                services.AddSingleton(alertSink);
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static void SeedEndpoint(InMemoryNetworkStorageStore store, string slug, string method, string definitionJson)
    {
        // endpointId == slug so StoreEndpointDataSource's direct read hits.
        store.UpsertEndpointAsync(
            ProjectId, slug, slug, method, enabled: true,
            JsonDocument.Parse(definitionJson).RootElement,
            versionHash: null, version: 1, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void SeedLegacyPlayerProjections(InMemoryNetworkStorageStore store, bool enabled)
    {
        store.UpsertProjectAsync(ProjectId, JsonSerializer.SerializeToElement(new { legacyPlayerProjections = enabled }), 1, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static void SeedRecord(InMemoryNetworkStorageStore store, string collectionId, string key, object payload)
    {
        store.UpsertRecordAsync(
            ProjectId, collectionId, key, JsonSerializer.SerializeToElement(payload),
            deleted: false, version: 1, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<JsonDocument> ReadBodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [SkippableFact]
    public async Task ValidKey_WithSteamIdHeader_ReturnsThatPlayersRecord_NativeOwner()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/endpoints/{ProjectId}/get-join?apiKey={ApiKey}");
        request.Headers.Add("x-steam-id", "steamA");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await ReadBodyAsync(response);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("alpha", body.RootElement.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task SteamIdHeader_HonorsIdentity_NoCrossPlayerBleed()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/endpoints/{ProjectId}/get-join?apiKey={ApiKey}");
        request.Headers.Add("x-steam-id", "steamB");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        // Identity B must read B's record, never A's and never a blank record.
        Assert.Equal("bravo", body.RootElement.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task SteamIdQueryParam_ResolvesIdentity()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/endpoints/{ProjectId}/get-join?apiKey={ApiKey}&steamId=steamA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("alpha", body.RootElement.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task InputFromQueryParams_ReachesInputExpressions()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/endpoints/{ProjectId}/echo-input?apiKey={ApiKey}&zone=spawn");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        // The `zone` query param must surface through {{input.zone}}; reserved auth
        // keys (apiKey) must not leak into input.
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task InvalidApiKey_ReturnsNativeUnauthorized_NotBunProxy()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync($"/v3/endpoints/{ProjectId}/get-join?apiKey=wrong-key");

        // Native auth rejects the key with a 401. A 502 would mean the request
        // reached the dead legacy server proxy — that must never happen.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A STALE_SAVE guard on a non-monotonic timestamp field. This 409 is NOT
    // eligible for the anti-rollback heal (savedAt is not a monotonic progress
    // counter), so a genuinely stale save is still rejected — and that 409 must
    // reach /admin/errors (archive) + Discord (alert sink) with 5xx-level urgency.
    private const string StaleSaveGuardDefinition =
        """{"steps":[{"id":"existing","type":"read","collection":"players","key":"{{steamId}}"},{"id":"guard_fresh","type":"assert","check":{"expression":"{{num(input.savedAt, 0)}} >= {{num(existing.savedAt, 0)}}"},"errorCode":"STALE_SAVE","message":"Refusing to overwrite savedAt {{num(existing.savedAt, 0)}} with older value {{num(input.savedAt, 0)}}.","status":409}],"response":{"status":200,"body":{"ok":true}}}""";

    // A save-all anti-rollback guard (the "satu" shape): input.totalLevel must be
    // >= the stored players.totalLevel, then the new value is written. When the
    // stored cache has drifted ABOVE the client's recompute, the heal raises the
    // input to the stored high-water mark so the save persists instead of 409ing.
    private const string AntiRollbackSaveDefinition =
        """{"steps":[{"id":"existing","type":"read","collection":"players","key":"{{steamId}}"},{"id":"guard_level","type":"assert","check":{"expression":"{{num(input.totalLevel, 0)}} >= {{num(existing.totalLevel, 0)}}"},"errorCode":"SAVE_REGRESSION_BLOCKED","message":"Refusing to overwrite totalLevel {{num(existing.totalLevel, 0)}} with lower value {{num(input.totalLevel, 0)}}.","status":409},{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"totalLevel","value":"{{input.totalLevel}}"}]}],"response":{"status":200,"body":{"ok":true}}}""";

    [SkippableFact]
    public async Task StaleSave409_ReportsToErrorArchiveAndDiscord()
    {
        // A non-monotonic guard (savedAt) is NOT healed, so a genuinely stale save
        // still 409s. This data-integrity conflict must be reported end-to-end —
        // the visibility the 2026-06-19 "satu" loop lacked before the alerting fix.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "save-all", "POST", StaleSaveGuardDefinition);
        SeedRecord(store, "players", "steamA", new { savedAt = 200d });

        var archive = new InMemoryErrorArchive();
        var alertSink = new RecordingAlertSink();

        using var client = CreateClient(store, archive, alertSink);

        // Act: POST a save with savedAt=100 (older than the stored 200) → 409.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{ProjectId}/save-all?apiKey={ApiKey}");
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent(
            """{"savedAt":100}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        // Assert: the 409 is returned to the caller (the guard fired).
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("STALE_SAVE",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());

        // Assert: the 409 reached the error archive (/admin/errors + per-project
        // dashboard), tagged so it is filterable.
        var archived = Assert.Single(await archive.ListRecentAsync(10, CancellationToken.None));
        Assert.Equal(409, archived.StatusCode);
        Assert.Equal("EndpointNative.STALE_SAVE", archived.Classification);
        Assert.Equal(ProjectId, archived.ProjectId);
        Assert.Equal("steamA", archived.SteamId);
        Assert.Contains("endpoint-shadow", archived.Tags!);
        Assert.Contains("savedAt 200", archived.Message);

        // Assert: the 409 reached the Discord alert sink with the same payload.
        var alerted = Assert.Single(alertSink.Captured);
        Assert.Equal(archived.Id, alerted.Id);
        Assert.Equal(409, alerted.StatusCode);
        Assert.Equal("EndpointNative.STALE_SAVE", alerted.Classification);
    }

    [SkippableFact]
    public async Task AntiRollbackBelowStored_Heals_Returns200_NoErrorReported()
    {
        // The "satu" deadlock, end-to-end through the live HTTP pipeline:
        // players.totalLevel cache (80) is ABOVE the client's recomputed save (79).
        // The anti-rollback heal raises the input to the stored high-water mark so
        // the save succeeds (200) — the player is unblocked, progress is preserved,
        // and NOTHING is reported to /admin/errors or Discord (it is not an error).
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "save-all", "POST", AntiRollbackSaveDefinition);
        SeedLegacyPlayerProjections(store, enabled: true);
        SeedRecord(store, "players", "steamA", new { totalLevel = 80d });

        var archive = new InMemoryErrorArchive();
        var alertSink = new RecordingAlertSink();

        using var client = CreateClient(store, archive, alertSink);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{ProjectId}/save-all?apiKey={ApiKey}");
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent(
            """{"totalLevel":79}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        // Assert: the save was healed and persisted, not blocked.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());

        // Assert: no 409 reached the error archive or the Discord alert sink.
        Assert.Empty(await archive.ListRecentAsync(10, CancellationToken.None));
        Assert.Empty(alertSink.Captured);
    }

    [SkippableFact]
    public async Task AntiRollbackBelowStored_WithoutProjectionsOptIn_IsRejected_NotHealed()
    {
        // A project without legacyPlayerProjections never gets the server-side raise: the
        // project's own guard rejects the lower input and the stored value is untouched.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "save-all", "POST", AntiRollbackSaveDefinition);
        SeedRecord(store, "players", "steamA", new { totalLevel = 80d });

        using var client = CreateClient(store, new InMemoryErrorArchive(), new RecordingAlertSink());

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{ProjectId}/save-all?apiKey={ApiKey}");
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"totalLevel":79}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed class RecordingAlertSink : IExceptionAlertSink
    {
        public List<CapturedErrorDto> Captured { get; } = [];

        public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            Captured.Add(capturedError);
            return Task.CompletedTask;
        }
    }
    private sealed class FakeKeyResolver : IStorageApiKeyResolver
    {
        private readonly string _validKey;
        private readonly string _projectId;
        public FakeKeyResolver(string validKey, string projectId) { _validKey = validKey; _projectId = projectId; }
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken ct)
            => Task.FromResult(string.Equals(apiKey, _validKey, StringComparison.Ordinal) && string.Equals(projectId, _projectId, StringComparison.Ordinal)
                ? new StorageApiKeyAuthResult(42, projectId, true, "secret")
                : null);
    }

    /// <summary>
    /// Resolves the project's public key to a public-key auth result and its
    /// secret key to a secret-key auth result with <c>endpoints</c> execute by
    /// default — the permission the real dedicated-server key carries. Any other
    /// key misses, so the caller can simulate an unknown public key in the
    /// apiKey query. Permission and enabled state are configurable so the
    /// endpoints:x and KEY_DISABLED gates can be exercised at the route level.
    /// </summary>
    private sealed class DualKeyResolver : IStorageApiKeyResolver
    {
        private readonly string _projectId;
        private readonly string _publicKey;
        private readonly string _secretKey;
        private readonly Dictionary<string, string> _secretPermissions;
        private readonly bool _secretEnabled;

        public DualKeyResolver(
            string projectId,
            string publicKey,
            string secretKey,
            Dictionary<string, string>? secretPermissions = null,
            bool secretEnabled = true)
        {
            _projectId = projectId;
            _publicKey = publicKey;
            _secretKey = secretKey;
            _secretPermissions = secretPermissions ?? new Dictionary<string, string> { ["endpoints"] = "x" };
            _secretEnabled = secretEnabled;
        }

        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken ct)
        {
            if (!string.Equals(projectId, _projectId, StringComparison.Ordinal))
                return Task.FromResult<StorageApiKeyAuthResult?>(null);
            if (string.Equals(apiKey, _publicKey, StringComparison.Ordinal))
                return Task.FromResult<StorageApiKeyAuthResult?>(new StorageApiKeyAuthResult(42, projectId, true, "public"));
            if (string.Equals(apiKey, _secretKey, StringComparison.Ordinal))
                return Task.FromResult<StorageApiKeyAuthResult?>(new StorageApiKeyAuthResult(42, projectId, _secretEnabled, "secret", _secretPermissions));
            return Task.FromResult<StorageApiKeyAuthResult?>(null);
        }
    }

    // ── Dedicated-server credential resolution ──────────────────────────────
    //
    // The official library (BuildUrl in NetworkStorageHttp.cs) ALWAYS appends
    // ?apiKey=<public>&secret-key=1 to endpoint calls and carries the secret key
    // in the x-secret-key header. The auth layer must therefore resolve the
    // secret key, not the public key sitting in the apiKey query param.

    [SkippableFact]
    public async Task DedicatedServerCall_SecretInXSecretKeyHeader_PublicInApiKeyQuery_Executes()
    {
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", InitPlayerDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        // The secret key (endpoints:x) must authenticate the call; the apiKey
        // query param is the library's always-present public key and must not
        // shadow it. Pre-fix this 401'd with SECRET_KEY_REQUIRED because the
        // layer resolved the public key and never saw x-secret-key.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_UnknownPublicInQuery_SecretInXSecretKeyHeader_Executes()
    {
        // The reported symptom: the apiKey query value is not a resolvable key
        // for the project, but the valid secret key (endpoints:x) is presented in
        // x-secret-key. The documented contract is that a dedicated server
        // authenticates with the Network Storage SECRET key alone. Pre-fix this
        // returned the exact observed response: 401 {"error":"UNAUTHORIZED"}.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", InitPlayerDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey=sbox_ns_unknown&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task SyncToolStyle_XApiKeyHeaderWithSecretKey_StillExecutes()
    {
        // The sync tool authenticates with the secret key in the x-api-key header
        // and no apiKey query param. The explicit header stays the highest-priority
        // credential after the precedence change.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", InitPlayerDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{ProjectId}/init-player");
        request.Headers.Add("x-api-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task PublicKeyOnlyClient_ApiKeyQuery_StillExecutes()
    {
        // A plain game client with no dedicated secret authenticates via the
        // apiKey query param alone; the precedence change must not affect it.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "echo-input", "GET", EchoInputDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var response = await client.GetAsync($"/v3/endpoints/{ProjectId}/echo-input?apiKey={PublicKey}&zone=spawn");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_Get_SecretInXSecretKeyHeader_PublicInApiKeyQuery_Executes()
    {
        // GET endpoint calls from the library use the same URL shape (BuildUrl
        // appends ?apiKey=<public>&secret-key=1) plus the x-secret-key header, and
        // GET flows through ServeEndpointSlugReadAsync → ExecuteEndpointAsync when
        // an apiKey is present. The secret key must win there too.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "GET", InitPlayerDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1&zone=spawn");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_SecretWithoutEndpointsPermission_Returns403()
    {
        // The endpoints:x gate is meaningful once a secret key authenticates the
        // call: a secret key that lacks endpoints execute must be rejected with
        // FORBIDDEN (legacy server parity — checkPermission on the secret key data).
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", InitPlayerDefinition);
        var noEndpointPerm = new DualKeyResolver(
            ProjectId, PublicKey, SecretKey,
            secretPermissions: new Dictionary<string, string> { ["collections"] = "rw" });

        using var client = CreateClient(store, noEndpointPerm);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("FORBIDDEN", body.RootElement.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_SecretKey_BooleanHasSecretKeyCondition_Passes()
    {
        // The reported pipeline: an endpoint whose SERVER_ONLY gate is
        // `{{_hasSecretKey}} == true`. A verified secret key (x-secret-key) must
        // make the check pass and the endpoint must execute.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", ServerOnlyDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_FieldFormHasSecretKeyCheck_Passes()
    {
        // The user's exact compiled shape (field: "{{_hasSecretKey}}", op: "==",
        // value: true) over the real HTTP pipeline with a verified secret key.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", ServerOnlyFieldDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_StringifiedBooleanValueCheck_Passes()
    {
        // The user's CURRENT stored definition ("value": "true" as a string from
        // the pre-fix compiler): a verified secret key must pass the SERVER_ONLY
        // gate without re-pushing the endpoint.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", ServerOnlyStringifiedValueDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("spawn", body.RootElement.GetProperty("zone").GetString());
    }

    [SkippableFact]
    public async Task PublicKeyCall_FieldFormHasSecretKeyCheck_RejectsServerOnly()
    {
        // The same field-form gate must still reject public-key (client) calls:
        // without a verified secret key the check is false → 403 SERVER_ONLY.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", ServerOnlyFieldDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}");
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("SERVER_ONLY", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("conditionRejection").GetBoolean());
    }

    [SkippableFact]
    public async Task PublicKeyCall_BooleanHasSecretKeyCondition_RejectsServerOnly()
    {
        // The same gate must keep rejecting public-key (client) calls: without a
        // verified secret key the condition is false → 403 SERVER_ONLY with the
        // conditionRejection marker.
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", ServerOnlyDefinition);

        using var client = CreateClient(store, new DualKeyResolver(ProjectId, PublicKey, SecretKey));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}");
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("SERVER_ONLY", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("conditionRejection").GetBoolean());
    }

    [SkippableFact]
    public async Task DedicatedServerCall_DisabledSecretKey_Returns403KeyDisabled()
    {
        var store = new InMemoryNetworkStorageStore();
        SeedEndpoint(store, "init-player", "POST", InitPlayerDefinition);
        var disabled = new DualKeyResolver(ProjectId, PublicKey, SecretKey, secretEnabled: false);

        using var client = CreateClient(store, disabled);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{ProjectId}/init-player?apiKey={PublicKey}&secret-key=1");
        request.Headers.Add("x-secret-key", SecretKey);
        request.Headers.Add("x-steam-id", "steamA");
        request.Content = new StringContent("""{"zone":"spawn"}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        Assert.Equal("KEY_DISABLED", body.RootElement.GetProperty("error").GetString());
    }

    private sealed class FakeProjectService : INetworkStorageProjectService
    {
        public Task<NetworkStorageProjectAccessResult?> ResolveProjectAccessAsync(long userId, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<NetworkStorageProjectAccessResult?>(new NetworkStorageProjectAccessResult(
                new WorkspaceProject(projectId, "Test Project", null, Enabled: true, null, null, null),
                Organization: null, StorageOwnerUserId: userId,
                RequireSboxAuth: false, PlayerKeyMode: null, CanManage: true));

        public Task<NetworkStorageProjectCreateResult> CreateProjectAsync(long userId, string name, string? description, bool enabled, bool requireSboxAuth, string keyMode, string organizationId, CancellationToken cancellationToken, string? hostingProfile = null)
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

public sealed class EndpointExecutionRouteTests_Sqlite(SqliteHostFactory factory) : EndpointExecutionRouteTests<SqliteHostFactory>(factory);

public sealed class EndpointExecutionRouteTests_Postgres(PostgresHostFactory factory) : EndpointExecutionRouteTests<PostgresHostFactory>(factory);
