using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed record TunnelRequest(string PublicKeySpki, string Action, string Name, long Timestamp,
    string Nonce, int LocalPort, string Signature);

public sealed class TunnelIdentity : IDisposable
{
    private readonly ECDsa _key;
    public string Name { get; }
    public byte[] PublicKeySpki { get; }

    public TunnelIdentity(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new InvalidOperationException("The tunnel identity must use named-curve P-256.");
        _key = key;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
        Name = DeriveName(PublicKeySpki);
    }

    public static TunnelIdentity LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            TunnelFiles.WriteSecret(path, generated.ExportPkcs8PrivateKeyPem(), overwrite: false);
        }
        TunnelFiles.Restrict(path);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            // Public-only PEM is not a usable identity.
            _ = key.ExportParameters(true);
            return new TunnelIdentity(key);
        }
        catch { key.Dispose(); throw; }
    }

    public static string DeriveName(ReadOnlySpan<byte> canonicalSpki)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var digest = SHA256.HashData(canonicalSpki);
        Span<char> name = stackalloc char[12];
        for (var i = 0; i < name.Length; i++)
        {
            var bit = i * 5;
            var word = (digest[bit / 8] << 8) | digest[bit / 8 + 1];
            name[i] = alphabet[(word >> (11 - bit % 8)) & 31];
        }
        return new string(name);
    }

    public static byte[] Payload(string action, string name, long timestamp, string nonce, int localPort)
        => Encoding.UTF8.GetBytes($"sbox-ns-tunnel-v1\n{action}\n{name}\n{timestamp.ToString(CultureInfo.InvariantCulture)}\n{nonce}\n{localPort.ToString(CultureInfo.InvariantCulture)}");

    public TunnelRequest Sign(string action, int localPort, long? timestamp = null, string? nonce = null)
    {
        if (action is not ("register" or "delete") || localPort is < 1 or > 65535)
            throw new ArgumentException("Invalid tunnel action or port.");
        var time = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var signature = _key.SignData(Payload(action, Name, time, nonce, localPort), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(Convert.ToBase64String(PublicKeySpki), action, Name, time, nonce, localPort, Convert.ToBase64String(signature));
    }

    public void Dispose() => _key.Dispose();
}

public static class TunnelFiles
{
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            ProtectWindows(security, directory: false);
            file.SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void WriteSecret(string path, string value, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var folder = new DirectoryInfo(directory);
            var security = folder.GetAccessControl();
            ProtectWindows(security, directory: true);
            folder.SetAccessControl(security);
        }
        else
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { File.Delete(temp); }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(FileSystemSecurity security, bool directory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new System.Security.SecurityException("The secret file has no resolvable Windows owner.");
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new System.Security.SecurityException("The current Windows user has no security identifier.");
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var principal in new[] { owner, currentUser, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
    }
}
