using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// S3 over HTTP: <c>x-ns-library-status</c> (with latest and sunset) rides on every response, the server-info
/// compat block, and old clients see no difference in response bodies.
/// </summary>
public abstract class LibraryStatusHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected LibraryStatusHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static HttpRequestMessage Get(string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    [SkippableTheory]
    [InlineData(null, "unknown")]                 // an old library: no header at all
    [InlineData("1.1.0", "supported")]
    [InlineData("1.4.0", "supported")]            // newer than this server knows
    [InlineData("0.9.0", "unsupported")]          // below the matrix minimum
    [InlineData("1.0.9", "unsupported")]
    [InlineData("garbage", "unknown")]            // invalid counts as legacy and is not an error
    [InlineData("1.0", "unknown")]                // the old hardcoded constant
    [InlineData("", "unknown")]
    public async Task LibraryStatusHeaderFollowsTheDeclaredVersion(string? version, string expected)
    {
        using var client = factory.CreateClient();
        using var request = Get("/v3/server-info");
        if (version is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, version);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Header(response, CompatHeaders.LibraryStatus));
        var embedded = LibraryMatrix.LoadEmbedded();
        // 'latest' goes with outdated or worse; 'sunset' only with deprecated (nothing is deprecated yet).
        Assert.Equal(expected == "unsupported" ? embedded.Latest : null, Header(response, CompatHeaders.LibraryLatest));
        Assert.Null(Header(response, CompatHeaders.LibrarySunset));
    }

    [SkippableFact]
    public async Task OldLibraryRequestsGetTheSameBodyWithAndWithoutCompatHeaders()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var url = $"/v3/values/{project.ProjectId}";

        using var plain = await client.SendAsync(Get(url, ("x-api-key", project.PublicKey)));
        using var withHeaders = await client.SendAsync(Get(url, ("x-api-key", project.PublicKey), (ClientHeaders.LibraryVersion, "1.1.0"),
            (ClientHeaders.ClientType, "game"), (ClientHeaders.EngineVersion, "26.10.02")));
        using var unsupported = await client.SendAsync(Get(url, ("x-api-key", project.PublicKey), (ClientHeaders.LibraryVersion, "0.9.0")));

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        var body = await plain.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, withHeaders.StatusCode);
        Assert.Equal(body, await withHeaders.Content.ReadAsStringAsync());
        // Runtime clients below the minimum are still served: enforcement is for the editor and dashboard.
        Assert.Equal(HttpStatusCode.OK, unsupported.StatusCode);
        Assert.Equal(body, await unsupported.Content.ReadAsStringAsync());

        Assert.Equal("unknown", Header(plain, CompatHeaders.LibraryStatus));
        Assert.Equal("supported", Header(withHeaders, CompatHeaders.LibraryStatus));
        Assert.Equal("unsupported", Header(unsupported, CompatHeaders.LibraryStatus));
    }

    [SkippableFact]
    public async Task ServerInfoGainsACompatBlockAndKeepsItsOtherFields()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v3/server-info");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(BuildInfo.Version, root.GetProperty("version").GetString());
        Assert.Equal("v3", root.GetProperty("apiVersions")[0].GetString());
        Assert.True(root.TryGetProperty("capabilities", out _));
        Assert.True(root.TryGetProperty("tunnel", out _));
        Assert.True(root.TryGetProperty("update", out _));

        var compat = root.GetProperty("compat");
        var library = compat.GetProperty("library");
        Assert.Equal("1.1.0", library.GetProperty("minimum").GetString());
        Assert.Equal("1.1.0", library.GetProperty("recommended").GetString());
        Assert.Equal("1.2.0", library.GetProperty("latest").GetString());
        Assert.Equal("supported", library.GetProperty("legacy").GetString());
        Assert.Equal(JsonValueKind.Array, library.GetProperty("deprecated").ValueKind);
        Assert.Equal(JsonValueKind.Array, library.GetProperty("blocked").ValueKind);
        Assert.Equal(JsonValueKind.Array, library.GetProperty("tested").ValueKind);
        Assert.Equal("1.0.165702", library.GetProperty("knownBroken")[0].GetProperty("version").GetString());
        Assert.False(compat.GetProperty("overlay").GetProperty("applied").GetBoolean());
        Assert.Equal(LibraryMatrix.MatrixUrl, compat.GetProperty("matrixUrl").GetString());
    }

    [SkippableFact]
    public async Task EveryResponseCarriesAStatusIncludingErrorsAndRejections()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        using var ok = await client.SendAsync(Get($"/v3/values/{project.ProjectId}", ("x-api-key", project.PublicKey), (ClientHeaders.LibraryVersion, "1.1.0")));
        using var unauthorized = await client.SendAsync(Get($"/v3/values/{project.ProjectId}", ("x-api-key", "sbox_ns_wrong"), (ClientHeaders.LibraryVersion, "0.9.0")));
        using var notFound = await client.SendAsync(Get("/v3/nothing-here", (ClientHeaders.LibraryVersion, "1.1.0")));
        using var health = await client.GetAsync("/health");

        Assert.Equal("supported", Header(ok, CompatHeaders.LibraryStatus));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("unsupported", Header(unauthorized, CompatHeaders.LibraryStatus));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("supported", Header(notFound, CompatHeaders.LibraryStatus));
        Assert.Equal("unknown", Header(health, CompatHeaders.LibraryStatus));
    }

    [SkippableTheory]
    [InlineData("1.1.0", "supported")]
    [InlineData("1.0", "unknown")]       // the old constant the shipped library sends
    [InlineData("garbage", "unknown")]
    public async Task StatusUsesTheRevisionInitBodyVersionWhenThereIsNoHeader(string bodyVersion, string status)
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}",
            new { projectId = project.ProjectId, clientType = "game", networkStorageVersion = bodyVersion, revisionId = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, Header(response, CompatHeaders.LibraryStatus));
    }
}

public sealed class SqliteLibraryStatusHttpTests(SqliteHostFactory factory) : LibraryStatusHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresLibraryStatusHttpTests(PostgresHostFactory factory) : LibraryStatusHttpTests<PostgresHostFactory>(factory);
