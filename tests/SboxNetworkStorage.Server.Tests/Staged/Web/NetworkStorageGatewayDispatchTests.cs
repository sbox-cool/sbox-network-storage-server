using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageGatewayDispatchTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly HttpClient client;

    protected NetworkStorageGatewayDispatchTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [SkippableFact]
    public async Task GatewayReturnsNative404ForUnmatchedV3Paths()
    {
        // Unmatched /v3/ paths now return a native 404 instead of proxying to
        // the decommissioned Bun storage-api. All /v3/ routes are served by
        // ASP.NET Core + ScyllaDB — no Bun fallback.
        using var response = await client.GetAsync("/v3/unknown-unmatched-path");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(".NET native", response.Headers.GetValues("X-Sboxcool-Route-Owner").Single());
    }

    [SkippableFact]
    public async Task StorageApiRecordCrudRoutesAreRegistered_Not404()
    {
        // Regression: commit 161deec1 accidentally removed app.MapStorageApi()
        // from Program.cs, which unregistered /api/storage/{pid}/{cid}/{key}
        // (record CRUD) and /api/network-storage/{pid}/save-failure. Both fell
        // to the catch-all and returned 404/501 in production for days. A
        // missing apiKey must return 401 (route matched, auth rejected) — never
        // 404 (route not registered).
        using var getResp = await client.GetAsync("/api/storage/proj-1/players/test-key");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        using var postResp = await client.PostAsync(
            "/api/storage/proj-1/players/test-key", null);
        Assert.Equal(HttpStatusCode.Unauthorized, postResp.StatusCode);

        using var delResp = await client.DeleteAsync("/api/storage/proj-1/players/test-key");
        Assert.Equal(HttpStatusCode.Unauthorized, delResp.StatusCode);
    }

    [SkippableFact]
    public async Task SaveFailureRouteIsRegistered_Not404()
    {
        // Regression: the save-failure endpoint lives in MapStorageApi and was
        // lost when app.MapStorageApi() was removed. A missing apiKey must
        // return 401, never 404.
        using var response = await client.PostAsync(
            "/api/network-storage/proj-1/save-failure", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("/v3/manage/proj-1/collections")]
    [InlineData("/v3/manage/proj-1/endpoints")]
    public async Task ManagementMutationRouteResolvesHandler_Not500(string path)
    {
        // Regression: ManagementMutationCandidateHandler was registered only
        // under INetworkStorageCandidateHandler, but the gateway resolves it by
        // concrete type via GetRequiredService<ManagementMutationCandidateHandler>().
        // That threw InvalidOperationException ("No service for type ... has been
        // registered") and surfaced as a 500 on every PUT /v3/manage/*. With the
        // concrete registration the handler resolves and a missing apiKey must
        // return 401 (auth rejected) — never 500 (DI crash).
        using var response = await client.PutAsync(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [SkippableTheory]
    [InlineData("/v3/manage/proj-1/sync/preflight")]
    [InlineData("/v3/manage/proj-1/auto-test")]
    public async Task LegacySyncToolPostRouteIsServedNatively(string path)
    {
        using var response = await client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}

public sealed class NetworkStorageGatewayDispatchTests_Sqlite(SqliteHostFactory factory) : NetworkStorageGatewayDispatchTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageGatewayDispatchTests_Postgres(PostgresHostFactory factory) : NetworkStorageGatewayDispatchTests<PostgresHostFactory>(factory);
