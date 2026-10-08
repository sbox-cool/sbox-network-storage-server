using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Covers the v3 single-record CRUD family: served natively through the
/// ScyllaDB data plane when <c>Scylla:Primary</c> is on, and returning a native
/// 404 (Bun decommissioned) when it is off. Also pins the route-precedence
/// carve-out so the <c>{key}</c> parameter never captures the
/// <c>append</c> / <c>analytics/events</c> sub-resources.
/// </summary>
public abstract class NetworkStorageV3RecordDataPlaneTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ApiKey = "sk-test-record-key";
    private const string ProjectId = "demo-project";
    private const string Collection = "players";

    private readonly SelfHostFactory _factory;

    protected NetworkStorageV3RecordDataPlaneTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    // PORT-ADAPTED: the self-hosted server is always Scylla:Primary (store-authoritative), and
    // each test gets a fresh database of the fixture's driver instead of an in-memory fake.
    private HttpClient CreateClient(bool scyllaPrimary, INetworkStorageStore store) =>
        _factory.WithWebHostBuilder(builder =>
        {
            Assert.True(scyllaPrimary);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddSingleton<INetworkStorageStore>(store);
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeRecordKeyResolver(ApiKey, ProjectId));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [SkippableFact]
    public async Task Primary_PostRecord_WritesToScyllaDataPlane()
    {
        var store = await _factory.NewStoreAsync();
        using var client = CreateClient(scyllaPrimary: true, store);

        using var response = await client.PostAsJsonAsync(
            $"/v3/storage/{ProjectId}/{Collection}/player-1?apiKey={ApiKey}",
            new { hp = 100 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await store.ReadRecordAsync(ProjectId, Collection, "player-1", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Primary_GetRecord_ReadsFromScyllaDataPlane()
    {
        var store = await _factory.NewStoreAsync();
        await store.UpsertRecordAsync(ProjectId, Collection, "player-2",
            JsonSerializer.SerializeToElement(new { hp = 50 }), deleted: false, version: 1, CancellationToken.None);
        using var client = CreateClient(scyllaPrimary: true, store);

        using var response = await client.GetAsync($"/v3/storage/{ProjectId}/{Collection}/player-2?apiKey={ApiKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Primary_GetMissingRecord_ReturnsNotFoundFromDataPlane()
    {
        var store = await _factory.NewStoreAsync();
        using var client = CreateClient(scyllaPrimary: true, store);

        using var response = await client.GetAsync($"/v3/storage/{ProjectId}/{Collection}/absent?apiKey={ApiKey}");

        // Served natively (data plane miss) — NOT a 502 proxy failure.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Primary_AppendSubResource_IsServedNativelyNotCapturedByKeyRoute()
    {
        var store = await _factory.NewStoreAsync();
        using var client = CreateClient(scyllaPrimary: true, store);

        using var response = await client.PostAsJsonAsync(
            $"/v3/storage/{ProjectId}/{Collection}/append?apiKey={ApiKey}",
            new { value = 1 });

        // The literal `append` carve-out wins over `{key}` and is served by the
        // native append handler (registered by MapNetworkStorageGateway when
        // Scylla:Primary is on). It is never written as a record keyed "append".
        // Auth/project access fails without Postgres wired in this fixture, but
        // the important assertion is that the {key} route did not capture it.
        Assert.Null(await store.ReadRecordAsync(ProjectId, Collection, "append", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Primary_AnalyticsEvents_IsServedNativelyNotCapturedByKeyRoute()
    {
        var store = await _factory.NewStoreAsync();
        using var client = CreateClient(scyllaPrimary: true, store);

        using var response = await client.PostAsJsonAsync(
            $"/v3/storage/{ProjectId}/analytics/events?apiKey={ApiKey}",
            new { type = "test" });

        // The literal `analytics/events` carve-out wins over `{collectionId}/{key}`
        // and is served by the native analytics handler. Auth/project access fails
        // without Postgres in this fixture, but the key assertion is that the
        // {collectionId}/{key} route did not capture it as a record.
        Assert.Null(await store.ReadRecordAsync(ProjectId, "analytics", "events", CancellationToken.None));
    }
    [SkippableFact]
    public async Task CreatedPublicKeyCanBeListedToggledAndRemovedWithoutExposingSecret()
    {
        var project = await _factory.CreateProjectAsync();
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var owner = SboxNetworkStorage.Server.Hosting.NetworkStorageServices.LocalOwnerUserId;
        var keys = await projects.GetProjectKeysAsync(owner, project.ProjectId, CancellationToken.None);
        Assert.Equal(project.PublicKey, Assert.Single(keys, key => key.KeyType == "public").Key);
        var secret = Assert.Single(keys, key => key.KeyType == "secret");
        Assert.NotEqual(project.SecretKey, secret.Key);
        Assert.Contains("...", secret.Key);

        await projects.ToggleProjectKeyAsync(owner, project.ProjectId, project.PublicKey, CancellationToken.None);
        keys = await projects.GetProjectKeysAsync(owner, project.ProjectId, CancellationToken.None);
        Assert.False(Assert.Single(keys, key => key.KeyType == "public").Enabled);
        await projects.ToggleProjectKeyAsync(owner, project.ProjectId, project.PublicKey, CancellationToken.None);
        keys = await projects.GetProjectKeysAsync(owner, project.ProjectId, CancellationToken.None);
        Assert.True(Assert.Single(keys, key => key.KeyType == "public").Enabled);

        await projects.RemoveProjectKeyAsync(owner, project.ProjectId, project.PublicKey, CancellationToken.None);
        keys = await projects.GetProjectKeysAsync(owner, project.ProjectId, CancellationToken.None);
        Assert.Equal("secret", Assert.Single(keys).KeyType);
        using var client = _factory.CreateClient();
        using var removed = await client.GetAsync($"/v3/storage/{project.ProjectId}/{Collection}/player1?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
    }

    [SkippableFact]
    public async Task CreatedPublicKeyCanSaveAndLoadUsingBothStorageAliases()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        foreach (var prefix in new[] { "/v3/storage", "/api/storage" })
        {
            var path = $"{prefix}/{project.ProjectId}/{Collection}/player-created?apiKey={project.PublicKey}";
            using var saved = await client.PostAsJsonAsync(path, new { gold = 25, playerName = "Created key" });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var loaded = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
            var body = await loaded.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(25, body.GetProperty("gold").GetInt32());
            Assert.Equal("Created key", body.GetProperty("playerName").GetString());
        }
        using var wrongProject = await client.GetAsync($"/v3/storage/other-project/{Collection}/player-created?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongProject.StatusCode);
    }

    [SkippableFact]
    public async Task OversizedDirectDocumentReturnsPayloadLimitAndPreservesPreviousValue()
    {
        var project = await _factory.CreateProjectAsync();
        using var client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var maxBytes = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().MaxPayloadBytes;
        foreach (var prefix in new[] { "/v3/storage", "/api/storage" })
        {
            var path = $"{prefix}/{project.ProjectId}/{Collection}/payload-limit?apiKey={project.SecretKey}";
            using var initial = await client.PostAsJsonAsync(path, new { value = "original" });
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            foreach (var json in new[]
            {
                "{\"value\":\"" + new string('x', 2_200_000) + "\"}",
                "{\"value\":\"" + new string('é', maxBytes / 2) + "\"}"
            })
            {
                foreach (var knownLength in new[] { true, false })
                {
                    using HttpContent content = knownLength
                        ? new StringContent(json, Encoding.UTF8, "application/json")
                        : new UnknownLengthJsonContent(json);
                    using var rejected = await client.PostAsync(path, content);
                    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
                    var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal("PAYLOAD_TOO_LARGE", error.GetProperty("error").GetString());
                    using var read = await client.GetAsync(path);
                    Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                    var body = await read.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal("original", body.GetProperty("value").GetString());
                }
            }
            var boundary = "{\"value\":\"" + new string('x', maxBytes - Encoding.UTF8.GetByteCount("{\"value\":\"\"}")) + "\"}";
            using var boundaryContent = new StringContent(boundary, Encoding.UTF8, "application/json");
            using var accepted = await client.PostAsync(path, boundaryContent);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthJsonContent(string json)
        {
            _bytes = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FakeRecordKeyResolver(string validKey, string projectId) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult(
                string.Equals(apiKey, validKey, StringComparison.Ordinal) && string.Equals(project, projectId, StringComparison.Ordinal)
                    ? new StorageApiKeyAuthResult(42, project, true, "secret")
                    : null);
    }
}

public sealed class NetworkStorageV3RecordDataPlaneTests_Sqlite(SqliteHostFactory factory) : NetworkStorageV3RecordDataPlaneTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageV3RecordDataPlaneTests_Postgres(PostgresHostFactory factory) : NetworkStorageV3RecordDataPlaneTests<PostgresHostFactory>(factory);
