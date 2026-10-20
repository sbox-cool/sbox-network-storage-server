using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// LTS body equality: callers with no version, a current version, an old version and garbage get
/// byte-identical bodies and status codes on data-plane reads, endpoint calls and errors. Only the
/// compat response headers may differ; enforcement never rewrites a served body.
/// </summary>
public abstract class LibraryVersionBodyParityHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";
    private readonly TFactory factory;

    protected LibraryVersionBodyParityHttpTests(TFactory factory) => this.factory = factory;

    /// <summary>No library header, a current library, an old library, and an unparseable one.</summary>
    private static readonly string?[] Callers = [null, "1.1.0", "0.9.0", "garbage"];

    private static HttpRequestMessage Versioned(HttpMethod method, string url, string? libraryVersion)
    {
        var request = new HttpRequestMessage(method, url);
        if (libraryVersion is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, libraryVersion);
        return request;
    }

    // Code/Auth/NetworkStorageDedicatedServerSecret.cs TryBuildDedicatedStorageHeaders.
    private static void Dedicated(HttpRequestMessage request, string publicKey, string secretKey, string? libraryVersion)
    {
        request.Headers.Add("x-api-key", secretKey);
        request.Headers.Add("x-public-key", publicKey);
        if (libraryVersion is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, libraryVersion);
    }

    [SkippableFact]
    public async Task Versioned_callers_get_identical_bodies_and_statuses()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Body parity");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            await store.UpsertCollectionAsync(project.ProjectId, "players", "Players", "public",
                JsonSerializer.SerializeToElement(new { id = "players", name = "Players", collectionType = "per-steamid" }), 1, CancellationToken.None);
            await store.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
                JsonSerializer.SerializeToElement(new
                {
                    id = "ping", name = "Ping", slug = "ping", method = "POST", enabled = true,
                    response = new { pong = true },
                }), null, 1, CancellationToken.None);
        }
        using var client = factory.CreateClient();
        var docUrl = $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey={project.PublicKey}";
        using (var save = new HttpRequestMessage(HttpMethod.Post, docUrl) { Content = JsonContent.Create(new { coins = 250 }) })
        {
            Dedicated(save, project.PublicKey, project.SecretKey, null);
            using var saved = await client.SendAsync(save);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var values = new List<(HttpStatusCode Status, byte[] Body)>();
        var record = new List<(HttpStatusCode Status, byte[] Body)>();
        var endpoint = new List<(HttpStatusCode Status, byte[] Body)>();
        var error = new List<(HttpStatusCode Status, byte[] Body)>();
        foreach (var caller in Callers)
        {
            using (var response = await client.SendAsync(Versioned(HttpMethod.Get, $"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}", caller)))
                values.Add((response.StatusCode, await response.Content.ReadAsByteArrayAsync()));
            using (var request = Versioned(HttpMethod.Get, docUrl, caller))
            {
                Dedicated(request, project.PublicKey, project.SecretKey, null);
                using var response = await client.SendAsync(request);
                record.Add((response.StatusCode, await response.Content.ReadAsByteArrayAsync()));
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/ping?apiKey={project.PublicKey}")
            {
                Content = JsonContent.Create(new
                {
                    security = new { configVersion = "", clientMode = "legacy", authSessions = "disabled", encryptedRequests = "disabled" },
                    _endpointSlug = "ping",
                }),
            })
            {
                // The proven library call shape (Code/Endpoints/NetworkStorageEndpointSecurity.cs: clientMode is one of
                // legacy | session | encrypted | session+encrypted); only the library version varies between callers.
                request.Headers.Add("x-public-key", project.PublicKey);
                request.Headers.Add("x-ns-client-type", "editor");
                if (caller is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, caller);
                using var response = await client.SendAsync(request);
                endpoint.Add((response.StatusCode, await response.Content.ReadAsByteArrayAsync()));
            }
            using (var response = await client.SendAsync(Versioned(HttpMethod.Get, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey=wrong", caller)))
                error.Add((response.StatusCode, await response.Content.ReadAsByteArrayAsync()));
        }

        Assert.All(values, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.All(record, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.All(endpoint, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.All(error, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        foreach (var results in new[] { values, record, endpoint, error })
            foreach (var result in results.Skip(1))
            {
                Assert.Equal(results[0].Status, result.Status);
                Assert.Equal(results[0].Body, result.Body);
            }
    }
}

public sealed class SqliteLibraryVersionBodyParityHttpTests(SqliteHostFactory factory) : LibraryVersionBodyParityHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresLibraryVersionBodyParityHttpTests(PostgresHostFactory factory) : LibraryVersionBodyParityHttpTests<PostgresHostFactory>(factory);
