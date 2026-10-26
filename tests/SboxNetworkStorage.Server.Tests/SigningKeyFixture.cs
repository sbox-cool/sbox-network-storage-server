using System.IO;
using System.Security.Cryptography;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Creates a security signing key the way the first server start does, and a store over it.</summary>
internal static class SigningKeyFixture
{
    public static SecuritySigningKeyStore Create(string folder)
    {
        Directory.CreateDirectory(folder);
        var keyPath = Path.Combine(folder, "security_signing_key.pem");
        using var key = RSA.Create(2048);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new SecuritySigningKeyStore(keyPath, Path.Combine(folder, "security_signing_rotations.json"));
    }
}
