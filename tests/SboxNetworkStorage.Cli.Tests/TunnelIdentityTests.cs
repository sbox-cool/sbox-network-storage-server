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
