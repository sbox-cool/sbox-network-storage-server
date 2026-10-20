using System.Security.Cryptography;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Key file lifecycle, key ids, fingerprints and retired-key grace for the request encryption keyring.
/// Each test gets its own temp directory, removed in Dispose.
/// </summary>
public sealed class RequestEncryptionKeyringTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public RequestEncryptionKeyringTests()
    {
        _path = Path.Combine(_directory, "request-encryption-keys.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void EnsureAndLoad_WhenFileMissing_CreatesFileWithOneCurrentKey()
    {
        var created = new List<string>();

        var keyring = RequestEncryptionKeyring.EnsureAndLoad(_path, Start, created.Add);

        Assert.True(File.Exists(_path));
        Assert.Equal(new[] { _path }, created);
        Assert.Single(keyring.Keys);
        Assert.True(keyring.Current.IsCurrent);
        Assert.Equal(keyring.Current.KeyId, keyring.Keys[0].KeyId);
        Assert.Equal(Start, keyring.Current.CreatedAt);
        Assert.Null(keyring.Current.RetiredAt);
        Assert.Null(keyring.Current.GraceUntil);
    }

    [Fact]
    public void EnsureAndLoad_WhenFileExists_LoadsSameKeyWithoutRewritingFile()
    {
        var first = RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        var contentsBefore = File.ReadAllText(_path);
        var created = new List<string>();

        var second = RequestEncryptionKeyring.EnsureAndLoad(_path, Start.AddDays(1), created.Add);

        Assert.Empty(created);
        Assert.Equal(first.Current.KeyId, second.Current.KeyId);
        Assert.Equal(first.Current.Fingerprint, second.Current.Fingerprint);
        Assert.Equal(contentsBefore, File.ReadAllText(_path));
    }

    [Fact]
    public void CurrentKeyId_IsFirst32HexOfSha256OfSpki()
    {
        var keyring = RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        var current = keyring.Current;

        Assert.Matches("^[0-9a-f]{32}$", current.KeyId);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", current.PublicKeyPem, StringComparison.Ordinal);

        var digest = Convert.ToHexString(SHA256.HashData(SpkiDerFromPem(current.PublicKeyPem))).ToLowerInvariant();
        Assert.Equal(digest[..32], current.KeyId);
        Assert.Equal(digest, current.Fingerprint);
    }

    [Fact]
    public void FingerprintOf_IsSixtyFourLowercaseHex_AndMatchesCurrentFingerprint()
    {
        var keyring = RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        using var key = keyring.FindPrivate(keyring.Current.KeyId, Start)!;

        var fingerprint = RequestEncryptionKeyring.FingerprintOf(key);

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.Equal(keyring.Current.Fingerprint, fingerprint);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    [InlineData(RequestEncryptionKeyring.DefaultGraceSeconds)]
    [InlineData(RequestEncryptionKeyring.MaxGraceSeconds)]
    public void Rotate_KeepsRetiredKeyValidWhileGraceUntilIsAfterNow(int graceSeconds)
    {
        var original = RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        var originalKeyId = original.Current.KeyId;
        var originalFingerprint = original.Current.Fingerprint;

        var rotated = RequestEncryptionKeyring.Rotate(_path, Start, graceSeconds);

        Assert.NotEqual(originalKeyId, rotated.Current.KeyId);
        Assert.True(rotated.Current.IsCurrent);
        Assert.Equal(2, rotated.Keys.Count);

        var retired = rotated.Keys[1];
        Assert.Equal(originalKeyId, retired.KeyId);
        Assert.Equal(originalFingerprint, retired.Fingerprint);
        Assert.False(retired.IsCurrent);
        Assert.Equal(Start, retired.RetiredAt);
        Assert.Equal(Start.AddSeconds(graceSeconds), retired.GraceUntil);

        Assert.NotNull(rotated.FindPrivate(originalKeyId, Start));
        Assert.NotNull(rotated.FindPrivate(originalKeyId, Start.AddSeconds(graceSeconds - 1)));
        Assert.Null(rotated.FindPrivate(originalKeyId, Start.AddSeconds(graceSeconds)));
        Assert.NotNull(rotated.FindPrivate(rotated.Current.KeyId, Start.AddSeconds(graceSeconds)));

        var reloaded = RequestEncryptionKeyring.Load(_path);
        Assert.Equal(rotated.Current.KeyId, reloaded.Current.KeyId);
        Assert.Equal(originalKeyId, reloaded.Keys[1].KeyId);
        Assert.Equal(retired.GraceUntil, reloaded.Keys[1].GraceUntil);
    }

    [Fact]
    public void FindPrivate_ExpiredRetiredKey_IsNotReturned()
    {
        var originalKeyId = RequestEncryptionKeyring.EnsureAndLoad(_path, Start).Current.KeyId;
        var rotated = RequestEncryptionKeyring.Rotate(_path, Start, 60);

        Assert.Null(rotated.FindPrivate(originalKeyId, Start.AddSeconds(61)));
        Assert.Null(rotated.FindPrivate(originalKeyId, Start.AddDays(1)));
        Assert.NotNull(rotated.FindPrivate(rotated.Current.KeyId, Start.AddDays(365)));

        // Expiry is evaluated against the caller's clock; the expired entry stays in the file until the next Rotate.
        var reloaded = RequestEncryptionKeyring.Load(_path);
        Assert.Equal(2, reloaded.Keys.Count);
        Assert.Null(reloaded.FindPrivate(originalKeyId, Start.AddSeconds(61)));
    }

    [Fact]
    public void Rotate_RemovesRetiredKeysWhoseGraceHasEnded()
    {
        var firstKeyId = RequestEncryptionKeyring.EnsureAndLoad(_path, Start).Current.KeyId;
        var secondKeyId = RequestEncryptionKeyring.Rotate(_path, Start, 60).Current.KeyId;

        var third = RequestEncryptionKeyring.Rotate(_path, Start.AddSeconds(120), 3600);

        Assert.Equal(2, third.Keys.Count);
        Assert.DoesNotContain(third.Keys, key => key.KeyId == firstKeyId);
        Assert.Equal(secondKeyId, third.Keys[1].KeyId);
        Assert.Null(third.FindPrivate(firstKeyId, Start.AddSeconds(120)));
        Assert.NotNull(third.FindPrivate(secondKeyId, Start.AddSeconds(120)));
    }

    [Fact]
    public void Rotate_WithZeroGrace_RemovesOldKeyImmediately()
    {
        var originalKeyId = RequestEncryptionKeyring.EnsureAndLoad(_path, Start).Current.KeyId;

        var rotated = RequestEncryptionKeyring.Rotate(_path, Start, 0);

        Assert.Single(rotated.Keys);
        Assert.NotEqual(originalKeyId, rotated.Current.KeyId);
        Assert.Null(rotated.FindPrivate(originalKeyId, Start));

        var reloaded = RequestEncryptionKeyring.Load(_path);
        Assert.Single(reloaded.Keys);
        Assert.Null(reloaded.FindPrivate(originalKeyId, Start));
        Assert.DoesNotContain(originalKeyId, File.ReadAllText(_path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(RequestEncryptionKeyring.MaxGraceSeconds + 1)]
    public void Rotate_GraceOutsideZeroToMax_IsRejectedAndLeavesFileUnchanged(int graceSeconds)
    {
        RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        var contentsBefore = File.ReadAllText(_path);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => RequestEncryptionKeyring.Rotate(_path, Start, graceSeconds));

        Assert.Equal("graceSeconds", error.ParamName);
        Assert.Equal(contentsBefore, File.ReadAllText(_path));
    }

    [Fact]
    public void Rotate_WhenFileMissing_ThrowsFileNotFoundAndCreatesNothing()
    {
        // Production behaviour: Rotate loads the existing file first, so a missing file is an error and no key is created.
        Directory.CreateDirectory(_directory);

        Assert.Throws<FileNotFoundException>(() => RequestEncryptionKeyring.Rotate(_path, Start, 60));

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Rotate_ConcurrentCalls_SerialiseAndKeepEveryRotation()
    {
        // Without the lock, overlapping rotations each read one key and the later write drops the earlier rotation.
        RequestEncryptionKeyring.EnsureAndLoad(_path, Start);

        const int rotations = 4;
        Parallel.For(0, rotations, _ => RequestEncryptionKeyring.Rotate(_path, Start, 3600));

        var keyring = RequestEncryptionKeyring.EnsureAndLoad(_path, Start);
        Assert.Equal(rotations + 1, keyring.Keys.Count);
        Assert.Equal(1, keyring.Keys.Count(key => key.IsCurrent));
    }

    private static byte[] SpkiDerFromPem(string pem)
    {
        var base64 = string.Concat(pem
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));
        return Convert.FromBase64String(base64);
    }
}
