using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tunnels;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class DnsProofEndpointTests
{
    private const string Nonce = "00112233445566778899aabbccddeeff";

    [Fact]
    public async Task Without_an_identity_the_proof_is_404_and_no_key_is_created()
    {
        using var factory = new SqliteHostFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/.well-known/sbox-ns/dns-proof/{Nonce}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(File.Exists(TunnelManager.IdentityPath(factory.Config)));
    }

    [Fact]
    public async Task Valid_nonce_is_signed_with_the_identity_key_over_the_proof_payload()
    {
        using var factory = new SqliteHostFactory();
        using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(factory.Config));
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/.well-known/sbox-ns/dns-proof/{Nonce}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var signature = Convert.FromBase64String(body.RootElement.GetProperty("signature").GetString()!);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(identity.PublicKeySpki, out _);
        Assert.True(key.VerifyData(TunnelIdentity.DnsProofPayload(identity.Name, Nonce), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("0011223344556677")]
    [InlineData("00112233445566778899AABBCCDDEEFF")]
    [InlineData("00112233445566778899aabbccddeeffaa")]
    [InlineData("zz112233445566778899aabbccddeeff")]
    public async Task Malformed_nonce_is_404(string nonce)
    {
        using var factory = new SqliteHostFactory();
        using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(factory.Config));
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/.well-known/sbox-ns/dns-proof/{nonce}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
