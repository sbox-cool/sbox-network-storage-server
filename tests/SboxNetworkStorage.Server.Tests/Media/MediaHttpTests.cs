using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

// Media API over real HTTP: grants (POST /v3/media/{pid}/grants), raw upload (PUT .../upload/{token}) and
// object reads. Media is off by default; while off every media route answers the same not-found response
// as an unrecognized Network Storage route.
public abstract class MediaHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Player = "76561198000000001";
    private const string PlayerToken = "player-token";
    private const string NotFoundDetail =
        "This Network Storage route is not recognized. All /v3/ and /v1/ traffic is served by ASP.NET Core + Store.";
    private readonly SelfHostFactory _factory;

    protected MediaHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableFact]
    public async Task Media_routes_answer_not_found_while_media_is_disabled()
    {
        using var setup = await CreateAsync(enableMedia: false, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);
        var objectPath = $"/v3/media/{setup.Project.ProjectId}/objects/{new string('a', 32)}";

        using var grant = await setup.Client.SendAsync(Grant(setup, secret: true, Sha256Hex(jpeg), jpeg.Length));
        await AssertNotFoundAsync(grant);

        using var upload = await setup.Client.SendAsync(Upload(setup, "sbox_mg_unused.unused", jpeg));
        await AssertNotFoundAsync(upload);

        using var read = await setup.Client.GetAsync(objectPath);
        await AssertNotFoundAsync(read);

        using var head = await setup.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, objectPath));
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
    }

    [SkippableFact]
    public async Task Secret_key_grant_then_jpeg_put_is_created_and_replay_is_grant_used()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);
        var token = await IssueGrantAsync(setup, secret: true, Sha256Hex(jpeg), jpeg.Length);

        using var first = await setup.Client.SendAsync(Upload(setup, token, jpeg));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("ok").GetBoolean());
        Assert.Equal("image/jpeg", created.GetProperty("contentType").GetString());
        Assert.Equal(120, created.GetProperty("width").GetInt32());
        Assert.Equal(80, created.GetProperty("height").GetInt32());

        using var second = await setup.Client.SendAsync(Upload(setup, token, jpeg));
        await AssertErrorAsync(second, HttpStatusCode.Conflict, "GRANT_USED");
    }

    [SkippableFact]
    public async Task Expired_grant_is_gone()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);
        var token = ExpiredGrantToken(setup, Sha256Hex(jpeg), jpeg.Length);

        using var response = await setup.Client.SendAsync(Upload(setup, token, jpeg));
        await AssertErrorAsync(response, HttpStatusCode.Gone, "GRANT_EXPIRED");
    }

    [SkippableFact]
    public async Task Public_key_self_grant_is_refused_unless_the_purpose_opts_in()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);

        using var refused = await setup.Client.SendAsync(Grant(setup, secret: false, Sha256Hex(jpeg), jpeg.Length, PlayerToken));
        await AssertErrorAsync(refused, HttpStatusCode.Forbidden, "MEDIA_GRANT_FORBIDDEN");
    }

    [SkippableFact]
    public async Task Public_key_self_grant_with_opt_in_and_verified_player_then_jpeg_put_is_created()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: true, verifyPlayers: true);
        var jpeg = Jpeg(120, 80);
        var token = await IssueGrantAsync(setup, secret: false, Sha256Hex(jpeg), jpeg.Length, PlayerToken);

        using var response = await setup.Client.SendAsync(Upload(setup, token, jpeg));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [SkippableFact]
    public async Task Head_returns_read_headers_without_a_body_and_get_serves_the_record_length()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);
        var mediaId = await UploadJpegAsync(setup, jpeg);
        var objectPath = $"/v3/media/{setup.Project.ProjectId}/objects/{mediaId}";

        using var head = await setup.Client.SendAsync(ReadRequest(HttpMethod.Head, objectPath));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("image/jpeg", head.Content.Headers.ContentType!.MediaType);
        Assert.Equal(jpeg.Length, head.Content.Headers.ContentLength);
        Assert.Equal("nosniff", head.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", head.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var get = await setup.Client.SendAsync(ReadRequest(HttpMethod.Get, objectPath));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(jpeg.Length, get.Content.Headers.ContentLength);
        Assert.Equal(jpeg, await get.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task Secret_key_in_the_query_string_is_not_read_for_private_objects()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false, visibility: "private");
        var jpeg = Jpeg(120, 80);
        var mediaId = await UploadJpegAsync(setup, jpeg);
        var objectPath = $"/v3/media/{setup.Project.ProjectId}/objects/{mediaId}";

        using var viaHeader = await setup.Client.SendAsync(ReadRequest(HttpMethod.Get, objectPath, setup.Project.SecretKey));
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);

        using var viaQuery = await setup.Client.SendAsync(ReadRequest(HttpMethod.Get,
            $"{objectPath}?apiKey={Uri.EscapeDataString(setup.Project.SecretKey)}"));
        Assert.Equal(HttpStatusCode.NotFound, viaQuery.StatusCode);
    }

    [SkippableFact]
    public async Task Disabled_project_refuses_upload_and_read_with_project_disabled()
    {
        using var setup = await CreateAsync(enableMedia: true, allowClientSelfGrant: false);
        var jpeg = Jpeg(120, 80);
        var mediaId = await UploadJpegAsync(setup, jpeg);
        var objectPath = $"/v3/media/{setup.Project.ProjectId}/objects/{mediaId}";
        var pendingToken = await IssueGrantAsync(setup, secret: true, Sha256Hex(jpeg), jpeg.Length);

        await DisableProjectAsync(setup);

        using var upload = await setup.Client.SendAsync(Upload(setup, pendingToken, jpeg));
        await AssertErrorAsync(upload, HttpStatusCode.Forbidden, "PROJECT_DISABLED");
        using var read = await setup.Client.SendAsync(ReadRequest(HttpMethod.Get, objectPath));
        await AssertErrorAsync(read, HttpStatusCode.Forbidden, "PROJECT_DISABLED");
    }

    private async Task<Setup> CreateAsync(bool enableMedia, bool allowClientSelfGrant, bool verifyPlayers = false, string visibility = "public")
    {
        var facepunch = new FacepunchTransport();
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<ISboxAuthVerifier, FacepunchSboxAuthVerifier>().ConfigurePrimaryHttpMessageHandler(() => facepunch)));
        var project = await factory.CreateProjectAsync(requireSboxAuth: verifyPlayers);
        if (enableMedia)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
                NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "media", new()
                {
                    ["mediaEnabled"] = "true",
                    ["mediaPurpose"] = "avatar",
                    ["mediaPurposeEnabled"] = "true",
                    ["mediaAllowClientSelfGrant"] = allowClientSelfGrant ? "true" : "false",
                    ["mediaVisibility"] = visibility,
                    ["mediaMaxBytes"] = "1048576",
                }, CancellationToken.None);
        }
        return new Setup(factory, factory.CreateClient(), project);
    }

    private static HttpRequestMessage Grant(Setup setup, bool secret, string sha256, int sizeBytes, string? playerToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/media/{setup.Project.ProjectId}/grants")
        {
            Content = JsonContent.Create<object>(secret
                ? new { purpose = "avatar", contentType = "image/jpeg", sizeBytes, sha256, forSteamId = Player }
                : new { purpose = "avatar", contentType = "image/jpeg", sizeBytes, sha256 }),
        };
        request.Headers.Add("x-api-key", secret ? setup.Project.SecretKey : setup.Project.PublicKey);
        if (!secret)
        {
            request.Headers.Add("x-steam-id", Player);
            if (playerToken is not null) request.Headers.Add("x-sbox-token", playerToken);
        }
        return request;
    }

    private static async Task<string> IssueGrantAsync(Setup setup, bool secret, string sha256, int sizeBytes, string? playerToken = null)
    {
        using var response = await setup.Client.SendAsync(Grant(setup, secret, sha256, sizeBytes, playerToken));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("grantToken").GetString()!;
    }

    private static HttpRequestMessage Upload(Setup setup, string token, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/v3/media/{setup.Project.ProjectId}/upload/{token}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return request;
    }

    private static async Task<string> UploadJpegAsync(Setup setup, byte[] jpeg)
    {
        var token = await IssueGrantAsync(setup, secret: true, Sha256Hex(jpeg), jpeg.Length);
        using var response = await setup.Client.SendAsync(Upload(setup, token, jpeg));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mediaId").GetString()!;
    }

    private static HttpRequestMessage ReadRequest(HttpMethod method, string path, string? apiKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        return request;
    }

    private static async Task DisableProjectAsync(Setup setup)
    {
        await using var scope = setup.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            NetworkStorageServices.LocalOwnerUserId, setup.Project.ProjectId, "project", new() { ["enabled"] = "false" }, CancellationToken.None);
    }

    // Signs a grant whose expiry is already in the past, so the expiry branch is exercised without waiting out the TTL.
    private static string ExpiredGrantToken(Setup setup, string sha256, int sizeBytes)
    {
        using var scope = setup.Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<MediaGrantTokens>();
        return tokens.Issue(new MediaGrantClaims(
            Version: 1,
            ProjectId: setup.Project.ProjectId,
            Purpose: "avatar",
            ContentType: "image/jpeg",
            MaxBytes: sizeBytes,
            Sha256: sha256,
            MediaId: MediaGrantTokens.NewId(),
            OwnerSteamId: Player,
            ExpiresAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000,
            GrantId: MediaGrantTokens.NewId()));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetString());
        Assert.Equal(NotFoundDetail, body.GetProperty("detail").GetString());
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Minimal JPEG: SOI then a baseline SOF0 header carrying the dimensions.</summary>
    private static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
    ];

    private sealed class FacepunchTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var verified = body.RootElement.GetProperty("token").GetString() == PlayerToken ? Player : null;
            return new HttpResponseMessage(verified is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(verified is null ? "{}" : $"{{\"Status\":\"ok\",\"SteamId\":{verified}}}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record Setup(SelfHostFactory Factory, HttpClient Client, SelfHostProject Project) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}

public sealed class SqliteMediaHttpTests(SqliteHostFactory factory)
    : MediaHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresMediaHttpTests(PostgresHostFactory factory)
    : MediaHttpTests<PostgresHostFactory>(factory);
