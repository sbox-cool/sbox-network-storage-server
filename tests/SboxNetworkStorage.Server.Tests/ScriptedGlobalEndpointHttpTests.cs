using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

// A `scope: global` endpoint exists only as a module import. Over HTTP it must be
// indistinguishable from a slug that was never defined.
public abstract class ScriptedGlobalEndpointHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string GlobalDefinition = """{"runtime":"javascript","scope":"global","script":"export function coinsOf() { return 1; }"}""";
    private readonly SelfHostFactory _factory;

    protected ScriptedGlobalEndpointHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task GlobalEndpoint_OverHttp_IsByteIdenticalToAnUnknownEndpoint()
    {
        // Two projects so the slug in the body matches: "bank" exists only in the first.
        var withGlobal = await _factory.CreateProjectAsync("Globals");
        var withoutGlobal = await _factory.CreateProjectAsync("Empty");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            await store.UpsertEndpointAsync(withGlobal.ProjectId, "bank", "bank", "POST", true,
                JsonDocument.Parse(GlobalDefinition).RootElement, null, 1, CancellationToken.None);
        }
        using var client = _factory.CreateClient();

        var global = await PostAsync(client, withGlobal, "bank");
        var unknown = await PostAsync(client, withoutGlobal, "bank");

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(unknown.Status, global.Status);
        Assert.Equal(Encoding.UTF8.GetString(unknown.Body), Encoding.UTF8.GetString(global.Body));
        Assert.Equal(unknown.Headers, global.Headers);
    }

    private static async Task<Reply> PostAsync(HttpClient client, SelfHostProject project, string slug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/v3/endpoints/{project.ProjectId}/{slug}", UriKind.Relative))
        {
            Content = JsonContent.Create(new Dictionary<string, object?>()),
        };
        request.Headers.Add("x-api-key", project.PublicKey);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key != "Date")
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            // X-Correlation-ID is minted per request; its presence is still compared through the name.
            .Select(header => header.Key == "X-Correlation-ID"
                ? "X-Correlation-ID: <per-request>"
                : $"{header.Key}: {string.Join(",", header.Value)}");
        return new Reply(response.StatusCode, body, string.Join("\n", headers));
    }

    private sealed record Reply(HttpStatusCode Status, byte[] Body, string Headers);
}

public sealed class SqliteScriptedGlobalEndpointHttpTests(SqliteHostFactory factory)
    : ScriptedGlobalEndpointHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresScriptedGlobalEndpointHttpTests(PostgresHostFactory factory)
    : ScriptedGlobalEndpointHttpTests<PostgresHostFactory>(factory);
