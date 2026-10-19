using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// Issue #152 over HTTP. A library-identified caller (x-ns-client-type, x-ns-library-version, or a body
/// security block) gets endpoint-route errors as HTTP 200 with the real status in the body (reference epError
/// shape). Every other client keeps the real status and its existing body.
/// </summary>
public abstract class LibraryErrorEnvelopeHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";
    private readonly TFactory factory;

    protected LibraryErrorEnvelopeHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    // Published r422519 build (snapshot 026acfa): header-only GET, no body, no
    // x-ns-library-version. x-ns-client-type is set on the authenticated path only.
    private static HttpRequestMessage LegacyGet(string url, string publicKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-public-key", publicKey);
        request.Headers.Add("x-steam-id", SteamId);
        request.Headers.Add("x-ns-client-type", "game");
        return request;
    }

    private async Task<SelfHostProject> ProjectWithPingAsync(string name)
    {
        var project = await factory.CreateProjectAsync(name);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
            JsonSerializer.SerializeToElement(new
            {
                id = "ping", name = "Ping", slug = "ping", method = "POST", enabled = true,
                response = new { pong = true },
            }), null, 1, CancellationToken.None);
        return project;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Library callers: HTTP 200, the real status and error in the body, request id on the header and body.</summary>
    private static async Task<JsonElement> AssertEnvelopeAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Request-Id"));
        var root = await JsonAsync(response);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(root.GetProperty("_requestId").GetString(), error.GetProperty("requestId").GetString());
        Assert.True(root.TryGetProperty("message", out _));
        return root;
    }

    /// <summary>Every other client: the real status, with no envelope fields.</summary>
    private static async Task<JsonElement> AssertPlainErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var root = await JsonAsync(response);
        Assert.False(root.TryGetProperty("status", out _));
        Assert.False(root.TryGetProperty("_requestId", out _));
        return root;
    }

    private async Task<IReadOnlyList<JsonElement>> RequestLogAsync(string projectId)
    {
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>()
            .ListStorageRequestLogAsync(projectId, 50, CancellationToken.None);
    }

    private static JsonElement RowFor(IReadOnlyList<JsonElement> log, string pathSuffix)
        => log.Single(r => r.GetProperty("path").GetString()!.EndsWith(pathSuffix, StringComparison.Ordinal));

    [SkippableFact]
    public async Task HeaderOnlyGet_FromR422519Shape_GetsEnvelope()
    {
        var project = await factory.CreateProjectAsync("Envelope GET");
        using var client = Client(factory);
        using var response = await client.SendAsync(LegacyGet($"/v3/endpoints/{project.ProjectId}/missing?apiKey={project.PublicKey}", project.PublicKey));

        await AssertEnvelopeAsync(response, 404, "ENDPOINT_NOT_FOUND");
    }

    [SkippableFact]
    public async Task LibraryCallers_GetEnvelopeForEachEndpointRouteErrorClass()
    {
        var project = await ProjectWithPingAsync("Envelope classes");
        var pid = project.ProjectId;
        var pub = project.PublicKey;
        using var client = Client(factory);
        var cases = new (HttpMethod Method, string Url, object? Body, int Status, string Code)[]
        {
            (HttpMethod.Post, $"/v3/endpoints/{pid}/ping", new { coins = 1 }, 401, "UNAUTHORIZED"),
            (HttpMethod.Get, $"/v3/endpoints/{pid}/ping?apiKey=wrong", null, 401, "UNAUTHORIZED"),
            (HttpMethod.Get, $"/v3/endpoints/{pid}/missing?apiKey={pub}", null, 404, "ENDPOINT_NOT_FOUND"),
            (HttpMethod.Post, $"/v3/endpoints/{pid}/missing?apiKey={pub}", new { coins = 1 }, 404, "ENDPOINT_NOT_FOUND"),
            (HttpMethod.Post, $"/v3/endpoints/{pid}?apiKey={pub}", new { coins = 1 }, 400, "INVALID_REQUEST"),
        };
        foreach (var (method, url, body, status, code) in cases)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("x-ns-client-type", "editor");
            using var response = await client.SendAsync(request);
            await AssertEnvelopeAsync(response, status, code);
        }
    }

    [SkippableFact]
    public async Task PlainCallers_KeepRealStatusAndLegacyBodies()
    {
        var project = await ProjectWithPingAsync("Envelope plain");
        using var client = Client(factory);

        using (var missingKey = await client.PostAsJsonAsync($"/v3/endpoints/{project.ProjectId}/ping", new { coins = 1 }))
        {
            var root = await AssertPlainErrorAsync(missingKey, HttpStatusCode.Unauthorized);
            Assert.Equal("UNAUTHORIZED", root.GetProperty("error").GetString());
        }
        using (var notFound = await client.GetAsync($"/v3/endpoints/{project.ProjectId}/missing?apiKey={project.PublicKey}"))
        {
            var root = await AssertPlainErrorAsync(notFound, HttpStatusCode.NotFound);
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("ENDPOINT_NOT_FOUND", root.GetProperty("error").GetProperty("code").GetString());
        }
    }

    // Published r238672 / r422519 / r423774 POST: the body carries a security block and _endpointSlug.
    // The dedicated-server public-key path sends no client-type header there, so the body alone identifies.
    [SkippableFact]
    public async Task OldRevisionPostBody_IdentifiesLibraryWithoutHeaders()
    {
        var project = await factory.CreateProjectAsync("Envelope old body");
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/missing?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new
            {
                security = new { configVersion = "", clientMode = "legacy", authSessions = "disabled", encryptedRequests = "disabled", revisionId = 0, revisionOutdated = false },
                _endpointSlug = "missing",
            }),
        };
        using var response = await client.SendAsync(request);

        await AssertEnvelopeAsync(response, 404, "ENDPOINT_NOT_FOUND");
    }

    [SkippableFact]
    public async Task SuccessfulLibraryCall_IsByteIdenticalToPlainCall()
    {
        var project = await ProjectWithPingAsync("Envelope success");
        using var client = Client(factory);
        async Task<(HttpStatusCode Status, byte[] Body)> PingAsync(bool library)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/ping?apiKey={project.PublicKey}")
            {
                Content = JsonContent.Create(new { }),
            };
            if (library) request.Headers.Add("x-ns-client-type", "editor");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
        }

        var plain = await PingAsync(false);
        var library = await PingAsync(true);

        Assert.Equal(HttpStatusCode.OK, plain.Status);
        Assert.Equal(plain.Status, library.Status);
        Assert.Equal(plain.Body, library.Body);
    }

    [SkippableFact]
    public async Task NonEndpointRoutes_KeepRealStatusForLibraryCallers()
    {
        var project = await factory.CreateProjectAsync("Envelope routes");
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/storage/{project.ProjectId}/players/{SteamId}?apiKey=wrong");
        request.Headers.Add("x-ns-client-type", "game");
        using var response = await client.SendAsync(request);

        await AssertPlainErrorAsync(response, HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task RequestLog_RecordsRealStatusBehindEnvelope()
    {
        var project = await ProjectWithPingAsync("Envelope log");
        using var client = Client(factory);
        using (var response = await client.SendAsync(LegacyGet($"/v3/endpoints/{project.ProjectId}/missing?apiKey={project.PublicKey}", project.PublicKey)))
            await AssertEnvelopeAsync(response, 404, "ENDPOINT_NOT_FOUND");

        var row = RowFor(await RequestLogAsync(project.ProjectId), "/missing");
        Assert.Equal(404, row.GetProperty("status_code").GetInt32());
        Assert.Equal("game", row.GetProperty("client_type").GetString());
    }

    [SkippableFact]
    public async Task SecurityDenial_PlainCaller_KeepsRealStatusAndNoEnvelope()
    {
        var project = await ProjectWithPingAsync("Envelope security plain");
        await factory.UpdateProjectAsync(project.ProjectId, p => p with { EnableAuthSessions = true, RequireAuthSessions = true });
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/ping?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("x-steam-id", SteamId);
        using var response = await client.SendAsync(request);

        var root = await AssertPlainErrorAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("SECURITY_SESSION_REQUIRED", root.GetProperty("error").GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("security", out _));
        Assert.False(root.TryGetProperty("retrySafe", out _));
    }

    [SkippableFact]
    public async Task SecurityDenial_LibraryCaller_GetsEnvelopeWithSecurityAndRetrySafe()
    {
        var project = await ProjectWithPingAsync("Envelope security library");
        await factory.UpdateProjectAsync(project.ProjectId, p => p with { EnableAuthSessions = true, RequireAuthSessions = true });
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{project.ProjectId}/ping?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("x-public-key", project.PublicKey);
        request.Headers.Add("x-steam-id", SteamId);
        request.Headers.Add("x-ns-client-type", "game");
        using var response = await client.SendAsync(request);

        var root = await AssertEnvelopeAsync(response, 401, "SECURITY_SESSION_REQUIRED");
        Assert.True(root.GetProperty("retrySafe").GetBoolean());
        Assert.True(root.TryGetProperty("security", out _));
    }

    [SkippableFact]
    public async Task ServerInfo_AdvertisesLibraryErrorEnvelopeCapability()
    {
        using var client = Client(factory);
        using var response = await client.GetAsync("/v3/server-info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await JsonAsync(response);
        Assert.Contains("library-error-envelope", root.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
    }
}

public sealed class SqliteLibraryErrorEnvelopeHttpTests(SqliteHostFactory factory) : LibraryErrorEnvelopeHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresLibraryErrorEnvelopeHttpTests(PostgresHostFactory factory) : LibraryErrorEnvelopeHttpTests<PostgresHostFactory>(factory);

/// <summary>Unhandled exceptions follow the same contract in <see cref="ExceptionHandlingMiddleware"/>; no server needed.</summary>
public sealed class LibraryErrorEnvelopeExceptionTests
{
    [Fact]
    public async Task LibraryCallerOnEndpointRoute_GetsEnvelopeFor500()
    {
        var context = BuildContext(library: true, endpoint: true);

        await BuildMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = await ReadBodyAsync(context);
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("INTERNAL_ERROR", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(root.GetProperty("_requestId").GetString(), root.GetProperty("error").GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task PlainCallerOnEndpointRoute_KeepsRealStatus()
    {
        var context = BuildContext(library: false, endpoint: true);

        await BuildMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var root = await ReadBodyAsync(context);
        Assert.False(root.TryGetProperty("ok", out _));
        Assert.False(root.TryGetProperty("_requestId", out _));
    }

    [Fact]
    public async Task LibraryCallerOffEndpointRoute_KeepsRealStatus()
    {
        var context = BuildContext(library: true, endpoint: false);

        await BuildMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var root = await ReadBodyAsync(context);
        Assert.False(root.TryGetProperty("ok", out _));
    }

    private static ExceptionHandlingMiddleware BuildMiddleware()
        => new(_ => throw new InvalidOperationException("boom"), new InMemoryErrorArchive(), NullLogger<ExceptionHandlingMiddleware>.Instance);

    private static DefaultHttpContext BuildContext(bool library, bool endpoint)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = endpoint ? "/v3/endpoints/abc/ping" : "/v3/values/abc";
        context.Response.Body = new MemoryStream();
        context.RequestAborted = CancellationToken.None;
        context.Items[CorrelationContext.ItemKey] = "req_test_correlation";
        if (library) context.Request.Headers["x-ns-client-type"] = "game";
        if (endpoint)
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(LibraryErrorEnvelope.EndpointRouteMarker.Instance), "endpoint"));
        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.Clone();
    }
}
