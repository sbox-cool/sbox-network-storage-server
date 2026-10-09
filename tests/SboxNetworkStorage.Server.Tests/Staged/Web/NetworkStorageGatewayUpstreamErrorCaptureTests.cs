using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.Errors;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Network Storage routes (e.g. the s&amp;box SyncTool's <c>POST /v3/manage/:projectId/package-sync</c>)
/// terminate in ASP.NET Core and proxy to the hidden legacy server storage-api worker. When that worker returns a
/// 500, the gateway forwards the status/body to the caller. The storage-api archives its own 500s into the
/// legacy server-owned <c>internal_errors</c> rows. The gateway MUST still capture proxied upstream 5xx into the .NET
/// error archive (+ Discord alert) so the failure also carries .NET request context and fires the .NET alert.
/// </summary>
public abstract class NetworkStorageGatewayUpstreamErrorCaptureTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private static readonly TimeSpan UpstreamRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly SelfHostFactory factory;

    protected NetworkStorageGatewayUpstreamErrorCaptureTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task Gateway_Returns404ForUnmatchedV3Paths_NoBunProxy()
    {
        // The catch-all /v3/{**path} proxy to legacy server is removed. Unmatched paths
        // return a native 404. This test verifies no proxy attempt is made.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.PostAsync(
            "/v3/unknown-unmatched-path",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Gateway_NativeHandlerRequiresAuth()
    {
        // Native management routes (like package-sync) require API key auth.
        // Without auth, they return 401 — no legacy server proxy fallback.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.PostAsync(
            "/v3/manage/demo-project/package-sync",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient CreateClient(int port)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IErrorArchive>();
                services.AddSingleton<IErrorArchive, InMemoryErrorArchive>();
            });
        }).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    private static HttpListener StartListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class NetworkStorageGatewayUpstreamErrorCaptureTests_Sqlite(SqliteHostFactory factory) : NetworkStorageGatewayUpstreamErrorCaptureTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageGatewayUpstreamErrorCaptureTests_Postgres(PostgresHostFactory factory) : NetworkStorageGatewayUpstreamErrorCaptureTests<PostgresHostFactory>(factory);
