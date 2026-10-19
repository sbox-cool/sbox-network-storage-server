using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Encrypted-request v2 golden vectors. The fixture was produced by an independent node:crypto script, not by the
/// server. Its private keys are test-only, not server keys. Request URL values are taken from each envelope so the
/// shape check passes and only the AES-GCM AAD check can reject a tampered envelope.
/// </summary>
public sealed class EncryptedRequestV2VectorTests
{
    private static readonly JsonElement Vectors = LoadVectors();

    public static TheoryData<string> RejectedRequestCases()
    {
        var data = new TheoryData<string>();
        foreach (var request in Vectors.GetProperty("requests").EnumerateArray())
        {
            if (!request.GetProperty("expect").GetProperty("ok").GetBoolean())
            {
                data.Add(Text(request, "name"));
            }
        }

        return data;
    }

    [Fact]
    public void ServerKey_FingerprintAndKeyIdMatchFixture()
    {
        var serverKey = Vectors.GetProperty("serverKey");
        using var privateKey = ImportPrivateKey(serverKey);

        var fingerprint = RequestEncryptionKeyring.FingerprintOf(privateKey);

        Assert.Equal(Text(serverKey, "fingerprint"), fingerprint);
        Assert.Equal(Text(serverKey, "keyId"), fingerprint[..32]);
        Assert.Equal(Text(serverKey, "keyId"), Text(Named(Vectors.GetProperty("requests"), "valid-steam-request").GetProperty("envelope"), "keyId"));
    }

    [Fact]
    public void ValidRequest_UnwrapsAesKeyAndDecryptsPayload()
    {
        var vector = Named(Vectors.GetProperty("requests"), "valid-steam-request");
        var envelope = vector.GetProperty("envelope");
        var expect = vector.GetProperty("expect");
        using var privateKey = ImportPrivateKey(KeyFor(vector));

        var result = EncryptedRequestV2.Decrypt(envelope, privateKey, Text(envelope, "projectId"), Text(envelope, "endpointSlug"),
            steamId: null, sessionId: null, out var responseKey);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(responseKey);
        Assert.Equal(FromBase64Url(Text(vector, "aesKey")), responseKey!.AesKey);
        Assert.Equal(Text(envelope, "nonce"), responseKey.Nonce);
        Assert.Equal(Text(envelope, "keyId"), responseKey.KeyId);
        Assert.Equal(Text(expect, "requestId"), result.RequestId);
        Assert.Equal(Text(expect, "identityKey"), result.IdentityKey);
        AssertObjectEquals(expect.GetProperty("payload"), result.Payload);
    }

    [Theory]
    [MemberData(nameof(RejectedRequestCases))]
    public void RejectedRequest_FailsWithFixtureErrorAndReleasesNoResponseKey(string name)
    {
        var vector = Named(Vectors.GetProperty("requests"), name);
        var envelope = vector.GetProperty("envelope");
        var expect = vector.GetProperty("expect");
        using var privateKey = ImportPrivateKey(KeyFor(vector));

        var result = EncryptedRequestV2.Decrypt(envelope, privateKey, Text(envelope, "projectId"), Text(envelope, "endpointSlug"),
            steamId: null, sessionId: null, out var responseKey);

        Assert.False(result.Ok);
        Assert.Equal(expect.GetProperty("status").GetInt32(), result.Status);
        Assert.Equal(Text(expect, "code"), result.Code);
        Assert.StartsWith(Text(expect, "messagePrefix"), result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(responseKey);
    }

    [Fact]
    public void ResponseSeal_OpensWithFixtureKeyAndAad_AndSealResponseRoundTrips()
    {
        var response = Named(Vectors.GetProperty("responses"), "response-seal");
        var fixtureSeal = response.GetProperty("sealed");
        var aesKey = FromBase64Url(Text(response, "aesKey"));
        var aad = Encoding.UTF8.GetBytes(Text(response, "aad"));
        var expectedBody = Normalize(Text(response, "bodyJson"));

        var validEnvelope = Named(Vectors.GetProperty("requests"), "valid-steam-request").GetProperty("envelope");
        Assert.Equal(Text(validEnvelope, "nonce"), Text(response, "nonce"));
        Assert.Equal(Text(validEnvelope, "keyId"), Text(response, "keyId"));

        // The fixture seal (fixed iv) opens with the request AES key and the response AAD.
        Assert.Equal(expectedBody, Normalize(OpenGcm(fixtureSeal, aesKey, aad)));

        // SealResponse with the same key binding: random iv, same AAD, must open the same way.
        var key = new EncryptedRequestV2.ResponseKey(aesKey, Text(response, "nonce"), Text(response, "keyId"));
        var body = JsonSerializer.Deserialize<JsonElement>(Text(response, "bodyJson"));
        var produced = JsonSerializer.SerializeToElement(EncryptedRequestV2.SealResponse(key, body, new JsonSerializerOptions()));

        Assert.True(produced.GetProperty("encrypted").GetBoolean());
        Assert.Equal(2, produced.GetProperty("v").GetInt32());
        Assert.Equal(12, FromBase64Url(Text(produced, "iv")).Length);
        Assert.Equal(16, FromBase64Url(Text(produced, "tag")).Length);
        Assert.Equal(expectedBody, Normalize(OpenGcm(produced, aesKey, aad)));

        // The response AAD binds exact bytes: one extra byte must fail the tag check.
        var widerAad = aad.Concat(new byte[] { 0x01 }).ToArray();
        Assert.Throws<AuthenticationTagMismatchException>(() => { OpenGcm(produced, aesKey, widerAad); });
    }

    private static JsonElement KeyFor(JsonElement request) =>
        Text(request, "keyRole") == "wrong" ? Vectors.GetProperty("wrongKey") : Vectors.GetProperty("serverKey");

    private static RSA ImportPrivateKey(JsonElement key)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(Text(key, "privateKeyPem"));
        return rsa;
    }

    private static string OpenGcm(JsonElement seal, byte[] key, byte[] aad)
    {
        var iv = FromBase64Url(Text(seal, "iv"));
        var tag = FromBase64Url(Text(seal, "tag"));
        var ciphertext = FromBase64Url(Text(seal, "payload"));
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(iv, ciphertext, tag, plaintext, aad);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetRawText();
    }

    private static void AssertObjectEquals(JsonElement expected, JsonElement actual)
    {
        var expectedNames = expected.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var actualNames = actual.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedNames, actualNames);
        foreach (var property in expected.EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText());
        }
    }

    private static JsonElement Named(JsonElement array, string name)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (Text(item, "name") == name)
            {
                return item;
            }
        }

        throw new InvalidOperationException($"Fixture case '{name}' not found.");
    }

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidDataException($"Fixture property '{property}' is not a string.");

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        return Convert.FromBase64String(base64 + new string('=', padding));
    }

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-fixtures", "encrypted-request-v2-vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
