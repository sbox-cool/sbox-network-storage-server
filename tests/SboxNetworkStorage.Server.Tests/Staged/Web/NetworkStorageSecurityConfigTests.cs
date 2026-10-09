using SboxNetworkStorage.Server.Tests.Hosting;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class NetworkStorageSecurityConfigTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ProjectJson = """
        {
          "id": "demo-project",
          "enableAuthSessions": true,
          "authSessionTtlSeconds": 7200,
          "enableEncryptedRequests": true,
          "encryptedRequestWindowSeconds": 90,
          "analytics": { "enabled": false }
        }
        """;

    private readonly SelfHostFactory factory;
    private readonly HttpClient client;

    protected NetworkStorageSecurityConfigTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
        client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "test-secret-do-not-use-in-production");
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static NetworkStorageRequest BuildRequest(string projectId)
    {
        var route = NetworkStorageRouteClassifier.Classify("GET", $"/v3/security-config/{projectId}");
        return new NetworkStorageRequest(
            route,
            new Dictionary<string, string>(),
            ContentType: null,
            AuthSignals: new Dictionary<string, bool>(),
            Credentials: NetworkStorageCredentials.None,
            Body: null,
            ResolvedOwnerUserId: null,
            CancellationToken: CancellationToken.None);
    }

    [SkippableFact]
    public async Task ProjectMetadataReturnsSignedBunCompatiblePayload()
    {
        var handler = new SecurityConfigHandler(new StubProjectStore(ProjectJson), TimeProvider.System);

        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        Assert.Equal("anonymous", result.AuthDecision);
        Assert.Contains("store/projects/demo-project", result.StoragePathsRead);

        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("store", json.GetProperty("source").GetString());
        var config = json.GetProperty("config");
        Assert.Equal("demo-project", config.GetProperty("projectId").GetString());
        Assert.True(config.GetProperty("settings").GetProperty("enableAuthSessions").GetBoolean());
        Assert.True(config.GetProperty("settings").GetProperty("enableEncryptedRequests").GetBoolean());
        AssertSecurityConfigSignature(config);
    }

    [SkippableFact]
    public async Task NullIntegerFieldsFallBackToDefaultsInsteadOfThrowing()
    {
        // The store stores unset numeric project settings as JSON null. JsonElement.TryGetInt32
        // throws InvalidOperationException on a Null-kind element, so the builder must guard
        // against it and fall back to the defaults rather than 500.
        const string nullProjectJson = """
            {
              "id": "demo-project",
              "enableAuthSessions": true,
              "authSessionTtlSeconds": null,
              "enableEncryptedRequests": true,
              "encryptedRequestWindowSeconds": null,
              "analytics": { "enabled": false }
            }
            """;
        var handler = new SecurityConfigHandler(new StubProjectStore(nullProjectJson), TimeProvider.System);

        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        var settings = json.GetProperty("config").GetProperty("settings");
        Assert.Equal(3600, settings.GetProperty("authSessionTtlSeconds").GetInt32());
        Assert.Equal(120, settings.GetProperty("encryptedRequestWindowSeconds").GetInt32());
    }

    [SkippableFact]
    public async Task MissingProjectReturnsNotFoundCode()
    {
        var handler = new SecurityConfigHandler(new StubProjectStore(), TimeProvider.System);

        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("SECURITY_CONFIG_NOT_FOUND", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("missing", json.GetProperty("source").GetString());
    }

    [SkippableFact]
    public async Task StoreErrorReturnsReadFailedCode()
    {
        var handler = new SecurityConfigHandler(new StubProjectStore(throws: true), TimeProvider.System);

        var result = await handler.ExecuteAsync(BuildRequest("demo-project"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("SECURITY_CONFIG_READ_FAILED", result.PublicErrorCode);
        var json = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("error", json.GetProperty("source").GetString());
    }

    [SkippableFact]
    public async Task LiveRouteServesStoreProjectConfigWithoutPublishedBlob()
    {
        using var liveClient = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NETWORK_STORAGE_AUTH_SESSION_SECRET", "test-secret-do-not-use-in-production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageStore>();
                services.AddScoped<INetworkStorageStore>(_ => new StubProjectStore(ProjectJson));
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await liveClient.GetAsync("/v3/security-config/demo-project");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=15", response.Headers.CacheControl?.ToString());
        Assert.Equal("store", response.Headers.GetValues("X-Security-Config-Source").Single());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("store", root.GetProperty("source").GetString());
        AssertSecurityConfigSignature(root.GetProperty("config"));
    }

    private static void AssertSecurityConfigSignature(JsonElement config)
    {
        var jwk = config.GetProperty("signing").GetProperty("publicKeyJwk");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString()),
            Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString()),
        });

        Assert.True(rsa.VerifyData(
            StableSerialize(config, skipSignature: true),
            Base64UrlDecode(config.GetProperty("signature").GetString()),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        var settingsHash = SHA256.HashData(StableSerialize(config.GetProperty("settings")));
        Assert.Equal(
            Convert.ToHexString(settingsHash).ToLowerInvariant()[..16],
            config.GetProperty("configVersion").GetString());
    }

    private static byte[] StableSerialize(JsonElement element, bool skipSignature = false)
    {
        using var stream = new MemoryStream();
        // PORT-ADAPTED: the signer serializes with UnsafeRelaxedJsonEscaping (JSON.stringify-compatible,
        // production commit 9901667f); this upstream helper predates that fix and used the default
        // encoder, which escapes '+' and '/' in the base64 key material and can never verify.
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteStable(writer, element, skipSignature);
        return stream.ToArray();
    }

    private static void WriteStable(Utf8JsonWriter writer, JsonElement element, bool skipSignature)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (skipSignature && property.NameEquals("signature"))
                    continue;
                writer.WritePropertyName(property.Name);
                WriteStable(writer, property.Value, skipSignature: false);
            }
            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteStable(writer, item, skipSignature: false);
            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }

    private static byte[] Base64UrlDecode(string? value)
    {
        var normalized = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private sealed class StubProjectStore : EmptyNetworkStorageStore
    {
        private readonly string? json;
        private readonly bool throws;

        public StubProjectStore(string? json = null, bool throws = false)
        {
            this.json = json;
            this.throws = throws;
        }

        public override Task<JsonElement?> ReadProjectAsync(string projectId, CancellationToken ct)
        {
            if (throws)
                throw new InvalidOperationException($"simulated project read failure for {projectId}");
            if (string.IsNullOrEmpty(json))
                return Task.FromResult<JsonElement?>(null);

            using var document = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(document.RootElement.Clone());
        }
    }
}

public sealed class NetworkStorageSecurityConfigTests_Sqlite(SqliteHostFactory factory) : NetworkStorageSecurityConfigTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageSecurityConfigTests_Postgres(PostgresHostFactory factory) : NetworkStorageSecurityConfigTests<PostgresHostFactory>(factory);
