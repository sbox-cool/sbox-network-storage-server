using System.Security.Cryptography;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class TunnelIdentityTests
{
    private const string Spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9Q==";
    private const string Signature = "axfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZWiDWtTVOeCcDFY7QsbdFyTvpLs939HXZw3mO4JmWSqQ==";

    [Fact]
    public void Shared_registry_vector_derives_name_and_verifies_fixed_P1363_signature()
    {
        var spki = Convert.FromBase64String(Spki);
        Assert.Equal("ltjff6ym5cjs", TunnelIdentity.DeriveName(spki));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var consumed);
        Assert.Equal(spki.Length, consumed);
        var payload = TunnelIdentity.Payload("register", "ltjff6ym5cjs", 1791417600,
            "00112233445566778899aabbccddeeff", 5000);
        Assert.True(key.VerifyData(payload, Convert.FromBase64String(Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        payload[^1] ^= 1;
        Assert.False(key.VerifyData(payload, Convert.FromBase64String(Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Identity_is_persistent_owner_only_and_signs_the_canonical_request()
    {
        var dir = Directory.CreateTempSubdirectory("sbox-ns-identity-").FullName;
        try
        {
            var path = Path.Combine(dir, "secrets", "identity_ecdsa_p256.pem");
            using var first = TunnelIdentity.LoadOrCreate(path);
            using var second = TunnelIdentity.LoadOrCreate(path);
            Assert.Equal(first.PublicKeySpki, second.PublicKeySpki);
            Assert.Equal(first.Name, second.Name);
            var request = second.Sign("delete", 8080, 1791417600, "00112233445566778899aabbccddeeff");
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKeySpki), out _);
            Assert.Equal(64, Convert.FromBase64String(request.Signature).Length);
            Assert.True(publicKey.VerifyData(TunnelIdentity.Payload(request.Action, request.Name, request.Timestamp,
                request.Nonce, request.LocalPort), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Identity_rejects_other_curves()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => new TunnelIdentity(key));
    }

    [Fact]
    public void DNS_payloads_are_the_exact_contract_bytes()
    {
        Assert.Equal("sbox-ns-dns-v1\nregister\nltjff6ym5cjs\n1791417600\n00112233445566778899aabbccddeeff\n203.0.113.5\n2001:db8::5\n8080",
            System.Text.Encoding.UTF8.GetString(TunnelIdentity.DnsPayload("register", "ltjff6ym5cjs", 1791417600,
                "00112233445566778899aabbccddeeff", "203.0.113.5", "2001:db8::5", 8080)));
        Assert.Equal("sbox-ns-dns-v1\ndelete\nltjff6ym5cjs\n1791417600\n00112233445566778899aabbccddeeff\n\n\n8080",
            System.Text.Encoding.UTF8.GetString(TunnelIdentity.DnsPayload("delete", "ltjff6ym5cjs", 1791417600,
                "00112233445566778899aabbccddeeff", "", "", 8080)));
        Assert.Equal("sbox-ns-dns-proof-v1\nltjff6ym5cjs\n00112233445566778899aabbccddeeff",
            System.Text.Encoding.UTF8.GetString(TunnelIdentity.DnsProofPayload("ltjff6ym5cjs", "00112233445566778899aabbccddeeff")));
    }

    [Fact]
    public void DNS_request_and_proof_signatures_verify_like_the_registry()
    {
        using var identity = new TunnelIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var request = identity.SignDns("update", "203.0.113.5", "", 8080, 1791417600, "00112233445566778899aabbccddeeff");
        // Registry algorithm: import SPKI, require canonical re-export, derive name, verify P1363 over the canonical payload.
        var spki = Convert.FromBase64String(request.PublicKeySpki);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var consumed);
        Assert.Equal(spki.Length, consumed);
        Assert.Equal(spki, key.ExportSubjectPublicKeyInfo());
        Assert.Equal(request.Name, TunnelIdentity.DeriveName(spki));
        var signature = Convert.FromBase64String(request.Signature);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(TunnelIdentity.DnsPayload("update", request.Name, 1791417600, request.Nonce, "203.0.113.5", "", 8080),
            signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(key.VerifyData(TunnelIdentity.DnsPayload("update", request.Name, 1791417600, request.Nonce, "203.0.113.6", "", 8080),
            signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var proof = Convert.FromBase64String(identity.SignDnsProof(request.Nonce));
        Assert.True(key.VerifyData(TunnelIdentity.DnsProofPayload(request.Name, request.Nonce), proof, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        // A proof can never be replayed as a signed registry request.
        Assert.False(key.VerifyData(TunnelIdentity.DnsPayload("update", request.Name, 1791417600, request.Nonce, "203.0.113.5", "", 8080),
            proof, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Throws<ArgumentException>(() => identity.SignDns("transfer", "203.0.113.5", "", 8080));
    }

    [Theory]
    [InlineData("ltjff6ym5cjs.n1.sboxns.com", true)]
    [InlineData("ltjff6ym5cjs.n10.sboxns.com", true)]
    [InlineData("ltjff6ym5cjs.sboxns.com", false)]
    [InlineData("ltjff6ym5cjs.n1.sboxns.com.evil.test", false)]
    [InlineData("ltjff6ym5cjs.n1.evil.test", false)]
    [InlineData("https://ltjff6ym5cjs.n1.sboxns.com", false)]
    [InlineData("other2345678.n1.sboxns.com", false)]
    [InlineData("ltjff6ym5cjs.n1234.sboxns.com", false)]
    [InlineData("LTJFF6YM5CJS.n1.sboxns.com", false)]
    public void DNS_hostname_must_be_this_identity_under_an_sboxns_zone(string hostname, bool allowed)
    {
        if (allowed) Assert.Equal(hostname, SboxNetworkStorage.Server.SignedDns.DnsRegistryClient.ValidateHostname(hostname, "ltjff6ym5cjs"));
        else Assert.Throws<InvalidDataException>(() => SboxNetworkStorage.Server.SignedDns.DnsRegistryClient.ValidateHostname(hostname, "ltjff6ym5cjs"));
    }

    [Theory]
    [InlineData("https://ltjff6ym5cjs.sboxns.com")]
    [InlineData("ltjff6ym5cjs.sboxns.com")]
    public void Registry_hostname_is_normalized_to_exact_HTTPS_origin(string hostname)
        => Assert.Equal("https://ltjff6ym5cjs.sboxns.com", TunnelRegistryClient.PublicUrl(hostname, "ltjff6ym5cjs"));

    [Theory]
    [InlineData("http://ltjff6ym5cjs.sboxns.com")]
    [InlineData("https://ltjff6ym5cjs.sboxns.com.evil.test")]
    [InlineData("https://user@ltjff6ym5cjs.sboxns.com")]
    [InlineData("https://ltjff6ym5cjs.sboxns.com:444")]
    [InlineData("https://ltjff6ym5cjs.sboxns.com/path")]
    [InlineData("https://different.sboxns.com")]
    public void Registry_rejects_wrong_or_unsafe_origins(string hostname)
        => Assert.Throws<InvalidDataException>(() => TunnelRegistryClient.PublicUrl(hostname, "ltjff6ym5cjs"));

    [Theory]
    [InlineData("http://remote.test/api", false)]
    [InlineData("http://127.0.0.1:5000/api", true)]
    [InlineData("http://localhost:5000/api", true)]
    [InlineData("https://sboxcool.com/api/network-storage/tunnels", true)]
    public void Production_registry_cannot_downgrade_to_remote_HTTP(string registry, bool allowed)
    {
        if (allowed) Assert.NotNull(TunnelRegistryClient.ValidateRegistry(registry));
        else Assert.Throws<InvalidOperationException>(() => TunnelRegistryClient.ValidateRegistry(registry));
    }
}
