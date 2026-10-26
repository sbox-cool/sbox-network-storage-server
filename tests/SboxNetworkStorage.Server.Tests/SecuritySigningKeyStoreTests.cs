using System.Buffers.Text;
using System.Security.Cryptography;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Rotation statements a library follows: each one must verify under the key it replaces.</summary>
public sealed class SecuritySigningKeyStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbox-ns-signing-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Announced_statement_verifies_under_the_previous_key()
    {
        var store = SigningKeyFixture.Create(_folder);
        var previous = store.Current();
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        SecuritySigningKeyStore.Rotate(store.KeyPath, store.RotationsPath, now, 3600);
        var current = store.Reload();

        var statement = Assert.Single(current.AnnouncedAt(now));
        Assert.Equal(previous.Fingerprint, statement["previousFingerprint"]!.GetValue<string>());
        Assert.Equal(current.Fingerprint, statement["nextFingerprint"]!.GetValue<string>());
        var signature = Base64Url.DecodeFromChars(statement["signature"]!.GetValue<string>());
        Assert.True(previous.Key.VerifyData(SecuritySigningKeyStore.StatementBytes(statement), signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Zero_grace_announces_nothing_after_the_rotation()
    {
        var store = SigningKeyFixture.Create(_folder);
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        SecuritySigningKeyStore.Rotate(store.KeyPath, store.RotationsPath, now, 0);

        Assert.Empty(store.Reload().AnnouncedAt(now.AddSeconds(1)));
    }
}
