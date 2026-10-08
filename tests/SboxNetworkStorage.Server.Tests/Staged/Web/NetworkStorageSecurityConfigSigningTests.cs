using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Regression tests for "security config RSA verification failed" on client startup.
///
/// Two independent causes, both covered here:
///   1. The signed payload was serialized with Utf8JsonWriter's DEFAULT encoder, which
///      emits the plus and slash characters as six-character unicode escapes. Both are
///      all over the base64 publicKeyPem and JWK modulus embedded in the payload, so the
///      bytes the server signed could not be reproduced by a JSON.stringify verifier.
///   2. The signing key was a per-process ephemeral keypair, so keyId and the public
///      key changed on every restart and never matched the Bun signer's key.
/// </summary>
public sealed class NetworkStorageSecurityConfigSigningTests
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

    private static JsonElement Project => JsonDocument.Parse(ProjectJson).RootElement.Clone();

    [Fact]
    public void Signed_payload_does_not_unicode_escape_base64_characters()
    {
        var node = new JsonObject
        {
            ["publicKeyPem"] = "abc+def/ghi=",
        };

        var text = Encoding.UTF8.GetString(NetworkStorageSecurityConfigBuilder.StableSerialize(node));

        // JSON.stringify emits these verbatim; the default .NET encoder would not.
        Assert.Contains("abc+def/ghi=", text);
        Assert.DoesNotContain("\\u002B", text);
        Assert.DoesNotContain("\\u002F", text);
    }

    [Fact]
    public void Signature_verifies_against_the_published_public_key()
    {
        var config = NetworkStorageSecurityConfigBuilder.Build("demo-project", Project, DateTimeOffset.UnixEpoch);

        var signature = Base64UrlDecode(config.GetProperty("signature").GetString()!);
        var publicKeyPem = config.GetProperty("signing").GetProperty("publicKeyPem").GetString()!;

        // Re-serialize exactly what was signed: the payload without the signature.
        var payload = JsonNode.Parse(config.GetRawText())!.AsObject();
        payload.Remove("signature");
        var signedBytes = NetworkStorageSecurityConfigBuilder.StableSerialize(payload);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);

        Assert.True(rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void KeyId_is_stable_across_builds_within_a_process()
    {
        var first = NetworkStorageSecurityConfigBuilder.Build("demo-project", Project, DateTimeOffset.UnixEpoch);
        var second = NetworkStorageSecurityConfigBuilder.Build("demo-project", Project, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            first.GetProperty("signing").GetProperty("keyId").GetString(),
            second.GetProperty("signing").GetProperty("keyId").GetString());
    }

    /// <summary>
    /// The JWK modulus and the PEM must describe the SAME key — a client that verifies
    /// via the JWK must reach the same result as one that verifies via the PEM.
    /// </summary>
    [Fact]
    public void Published_jwk_and_pem_describe_the_same_key()
    {
        var config = NetworkStorageSecurityConfigBuilder.Build("demo-project", Project, DateTimeOffset.UnixEpoch);
        var signing = config.GetProperty("signing");

        using var fromPem = RSA.Create();
        fromPem.ImportFromPem(signing.GetProperty("publicKeyPem").GetString()!);
        var pemParameters = fromPem.ExportParameters(includePrivateParameters: false);

        var jwk = signing.GetProperty("publicKeyJwk");
        Assert.Equal(pemParameters.Modulus, Base64UrlDecode(jwk.GetProperty("n").GetString()!));
        Assert.Equal(pemParameters.Exponent, Base64UrlDecode(jwk.GetProperty("e").GetString()!));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
