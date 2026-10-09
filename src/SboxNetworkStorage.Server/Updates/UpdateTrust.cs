using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// Where releases come from and who may sign them. These are compile-time values: code that
/// runs as root must never take them from a file the service account can write.
/// </summary>
public sealed class UpdateTrust(string repository, string feedUrl, IReadOnlyList<byte[]> signingKeys)
{
    public const string DefaultRepository = "sbox-cool/sbox-network-storage-server";
    public const string DefaultFeedUrl = "https://sboxcool.com/api/network-storage/releases/latest";
    public const string CosignIssuer = "https://token.actions.githubusercontent.com";

    private const string KeysResource = "SboxNetworkStorage.Server.Updates.release-signing-keys.pub";

    private static readonly Lazy<UpdateTrust> CompiledTrust = new(Create);

    public string Repository { get; } = repository;

    public string FeedUrl { get; } = feedUrl;

    /// <summary>SubjectPublicKeyInfo (DER) of every key whose signature is accepted.</summary>
    public IReadOnlyList<byte[]> SigningKeys { get; } = signingKeys;

    /// <summary>Only the release workflow on a version tag may sign a release.</summary>
    public string CosignIdentityPattern
        => $"^https://github\\.com/{Regex.Escape(Repository)}/\\.github/workflows/release\\.yml@refs/tags/v.+$";

    public static UpdateTrust Compiled => CompiledTrust.Value;

    private static UpdateTrust Create()
    {
        var repository = DefaultRepository;
        var feedUrl = DefaultFeedUrl;
#if DEBUG
        // Staging override for development builds only; release builds have no override.
        repository = Environment.GetEnvironmentVariable("SBOX_NS_DEV_UPDATE_REPO") is { Length: > 0 } r ? r : repository;
        feedUrl = Environment.GetEnvironmentVariable("SBOX_NS_DEV_UPDATE_FEED_URL") is { Length: > 0 } f ? f : feedUrl;
#endif
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream(KeysResource)
            ?? throw new InvalidOperationException($"embedded resource {KeysResource} is missing");
        using var reader = new StreamReader(stream);
        return new UpdateTrust(repository, feedUrl, ReleaseSignature.ParseKeys(reader.ReadToEnd()));
    }
}

/// <summary>Detached ECDSA P-256 (SHA-256, DER) signature over <c>SHA256SUMS</c>.</summary>
public static class ReleaseSignature
{
    public const string AssetName = "SHA256SUMS.p256.sig";

    /// <summary>Reads every <c>PUBLIC KEY</c> PEM block in <paramref name="text"/>; other text is ignored.</summary>
    public static IReadOnlyList<byte[]> ParseKeys(string text)
    {
        var keys = new List<byte[]>();
        var remaining = text.AsSpan();
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            if (remaining[fields.Label].SequenceEqual("PUBLIC KEY"))
            {
                keys.Add(Convert.FromBase64String(remaining[fields.Base64Data].ToString()));
            }

            remaining = remaining[fields.Location.End..];
        }

        return keys;
    }

    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="data"/> by any of <paramref name="keys"/>.</summary>
    public static bool Verify(byte[] data, byte[] signature, IReadOnlyList<byte[]> keys)
    {
        foreach (var key in keys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            try
            {
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // A malformed signature is simply not valid for this key.
            }
        }

        return false;
    }
}
