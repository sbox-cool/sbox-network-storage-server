using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Server-held RSA keys that wrap the per-request AES key of encrypted-request v2. The file keeps the current
/// key plus retired keys still inside their grace window. A keyring is an immutable snapshot; callers must not
/// dispose the private keys it hands out.
/// </summary>
public sealed class RequestEncryptionKeyring
{
    public const int DefaultGraceSeconds = 86400;
    public const int MaxGraceSeconds = 2592000;

    private const int FileSchemaVersion = 1;
    private const int KeyIdLength = 32;
    private const int RsaKeyBits = 2048;

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<Entry> _entries; // current first, then retired in file order

    private RequestEncryptionKeyring(IReadOnlyList<Entry> entries)
    {
        _entries = entries;
        Keys = [.. entries.Select(entry => entry.Info)];
        Current = Keys[0];
    }

    /// <summary>The key new encrypted requests are wrapped to.</summary>
    public RequestEncryptionKeyInfo Current { get; }

    /// <summary>The current key first, then the retired keys still on file.</summary>
    public IReadOnlyList<RequestEncryptionKeyInfo> Keys { get; }

    /// <summary>
    /// Creates the file with one new key when it is missing; otherwise loads it. An existing file is never overwritten.
    /// </summary>
    public static RequestEncryptionKeyring EnsureAndLoad(string path, DateTimeOffset now, Action<string>? onCreated = null)
    {
        if (File.Exists(path)) return Load(path);

        var entry = NewCurrentEntry(now);
        try
        {
            WriteFile(path, [entry], overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the file first; use its keys and drop ours.
            entry.Private.Dispose();
            return Load(path);
        }

        onCreated?.Invoke(path);
        return new RequestEncryptionKeyring([entry]);
    }

    /// <summary>
    /// Makes a new key current and keeps the previous current key retired until <paramref name="now"/> plus
    /// <paramref name="graceSeconds"/>. Retired keys whose grace has ended are removed from the file.
    /// </summary>
    public static RequestEncryptionKeyring Rotate(string path, DateTimeOffset now, int graceSeconds)
    {
        if (graceSeconds is < 0 or > MaxGraceSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(graceSeconds), graceSeconds,
                $"Grace must be between 0 and {MaxGraceSeconds} seconds.");
        }

        var existing = Load(path);
        var previous = existing._entries[0];
        var retired = previous with
        {
            Info = previous.Info with { IsCurrent = false, RetiredAt = now, GraceUntil = now.AddSeconds(graceSeconds) },
        };

        var entries = new List<Entry> { NewCurrentEntry(now) };
        entries.AddRange(existing._entries.Skip(1));
        entries.Add(retired);
        entries.RemoveAll(entry => !entry.Info.IsCurrent && entry.Info.GraceUntil <= now);

        WriteFile(path, entries, overwrite: true);
        return new RequestEncryptionKeyring(entries);
    }

    /// <summary>
    /// The private key for <paramref name="keyId"/> while it may still unwrap requests: the current key, or a retired
    /// key whose grace has not ended. Null otherwise. Callers must not dispose it.
    /// </summary>
    public RSA? FindPrivate(string keyId, DateTimeOffset now)
    {
        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.Info.KeyId, keyId, StringComparison.Ordinal)) continue;
            return entry.Info.IsCurrent || entry.Info.GraceUntil > now ? entry.Private : null;
        }

        return null;
    }

    /// <summary>Lowercase hex SHA-256 of the key's SubjectPublicKeyInfo DER (64 characters).</summary>
    public static string FingerprintOf(RSA key)
        => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    /// <summary>Reads and validates the keyring file. Throws when it is missing or malformed.</summary>
    internal static RequestEncryptionKeyring Load(string path)
    {
        KeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path), FileJson);
        }
        catch (JsonException ex)
        {
            throw Invalid(path, "is not valid JSON", ex);
        }

        if (file is null) throw Invalid(path, "is empty");
        if (file.SchemaVersion != FileSchemaVersion) throw Invalid(path, $"has unsupported schemaVersion {file.SchemaVersion}");
        if (file.Keys is null or { Count: 0 }) throw Invalid(path, "lists no keys");

        var current = file.Keys.FirstOrDefault(key => string.Equals(key.KeyId, file.CurrentKeyId, StringComparison.Ordinal))
            ?? throw Invalid(path, "currentKeyId names no key");
        if (current.RetiredAt is not null || current.GraceUntil is not null)
        {
            throw Invalid(path, "marks the current key as retired");
        }

        var entries = new List<Entry> { Restore(path, current, isCurrent: true) };
        foreach (var stored in file.Keys)
        {
            if (ReferenceEquals(stored, current)) continue;
            if (stored.RetiredAt is null || stored.GraceUntil is null)
            {
                throw Invalid(path, $"key {stored.KeyId} is neither current nor retired");
            }

            entries.Add(Restore(path, stored, isCurrent: false));
        }

        if (entries.Select(entry => entry.Info.KeyId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw Invalid(path, "lists a key id more than once");
        }

        return new RequestEncryptionKeyring(entries);
    }

    private static Entry NewCurrentEntry(DateTimeOffset createdAt)
    {
        var key = RSA.Create(RsaKeyBits);
        return Describe(key, key.ExportPkcs8PrivateKeyPem(), createdAt, retiredAt: null, graceUntil: null, isCurrent: true);
    }

    private static Entry Restore(string path, KeyFileEntry stored, bool isCurrent)
    {
        if (stored.KeyId is null || stored.PrivateKeyPem is null)
        {
            throw Invalid(path, "holds a key without a keyId or privateKeyPem");
        }

        var key = RSA.Create();
        try
        {
            key.ImportFromPem(stored.PrivateKeyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw Invalid(path, $"holds an unreadable private key for {stored.KeyId}", ex);
        }

        var entry = Describe(key, stored.PrivateKeyPem, ParseTime(path, stored.CreatedAt),
            ParseOptionalTime(path, stored.RetiredAt), ParseOptionalTime(path, stored.GraceUntil), isCurrent);
        if (!string.Equals(entry.Info.KeyId, stored.KeyId, StringComparison.Ordinal))
        {
            key.Dispose();
            throw Invalid(path, $"key {stored.KeyId} does not match its private key");
        }

        return entry;
    }

    private static Entry Describe(RSA key, string privateKeyPem, DateTimeOffset createdAt,
        DateTimeOffset? retiredAt, DateTimeOffset? graceUntil, bool isCurrent)
    {
        var fingerprint = FingerprintOf(key);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var jwk = new JsonObject
        {
            ["kty"] = "RSA",
            ["n"] = Base64Url.EncodeToString(parameters.Modulus!),
            ["e"] = Base64Url.EncodeToString(parameters.Exponent!),
        };
        var info = new RequestEncryptionKeyInfo(fingerprint[..KeyIdLength], fingerprint, createdAt, retiredAt, graceUntil,
            isCurrent, key.ExportSubjectPublicKeyInfoPem(), jwk);
        return new Entry(info, key, privateKeyPem);
    }

    private static void WriteFile(string path, IReadOnlyList<Entry> entries, bool overwrite)
    {
        var file = new KeyFile(FileSchemaVersion, entries[0].Info.KeyId,
            [.. entries.Select(entry => new KeyFileEntry(
                entry.Info.KeyId,
                entry.Info.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                entry.Info.RetiredAt?.ToString("O", CultureInfo.InvariantCulture),
                entry.Info.GraceUntil?.ToString("O", CultureInfo.InvariantCulture),
                entry.PrivateKeyPem))]);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, FileJson) + "\n");

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Owner-only from creation on Unix, matching ServerSecrets.EnsureFile.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(temp, full, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void MoveIntoPlace(string temp, string path, bool overwrite)
    {
        // Windows reports a sharing violation while a reader holds the old file open; retry briefly there.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite);
                return;
            }
            catch (Exception ex) when (overwrite && OperatingSystem.IsWindows() && attempt < 10
                                       && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(attempt * 20);
            }
        }
    }

    private static DateTimeOffset ParseTime(string path, string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw Invalid(path, $"holds a malformed timestamp '{text}'");

    private static DateTimeOffset? ParseOptionalTime(string path, string? text)
        => text is null ? null : ParseTime(path, text);

    private static InvalidDataException Invalid(string path, string reason, Exception? inner = null)
        => new($"Request encryption keys file '{path}' is invalid: {reason}.", inner);

    private sealed record Entry(RequestEncryptionKeyInfo Info, RSA Private, string PrivateKeyPem);

    internal sealed record KeyFile(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("currentKeyId")] string? CurrentKeyId,
        [property: JsonPropertyName("keys")] IReadOnlyList<KeyFileEntry>? Keys);

    internal sealed record KeyFileEntry(
        [property: JsonPropertyName("keyId")] string? KeyId,
        [property: JsonPropertyName("createdAt")] string? CreatedAt,
        [property: JsonPropertyName("retiredAt")] string? RetiredAt,
        [property: JsonPropertyName("graceUntil")] string? GraceUntil,
        [property: JsonPropertyName("privateKeyPem")] string? PrivateKeyPem);
}

/// <summary>Public description of one server key. The private half never leaves the keyring.</summary>
public sealed record RequestEncryptionKeyInfo(
    string KeyId,
    string Fingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset? GraceUntil,
    bool IsCurrent,
    string PublicKeyPem,
    JsonObject PublicKeyJwk);

/// <summary>
/// Shared, thread-safe view of the keyring file. <see cref="Current"/> returns a cached snapshot and reloads it when
/// the file's write time changes, checking at most every five seconds. A failed reload of a readable-but-busy file
/// keeps the last good snapshot; invalid content throws.
/// </summary>
public sealed class RequestEncryptionKeyStore
{
    private static readonly long CheckIntervalTicks = Stopwatch.Frequency * 5;

    private readonly Lock _gate = new();
    private RequestEncryptionKeyring? _snapshot;
    private DateTime _snapshotWriteTimeUtc;
    private long _nextCheckTicks;

    public RequestEncryptionKeyStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>The cached snapshot, reloaded when the file changed (checked at most every five seconds).</summary>
    public RequestEncryptionKeyring Current()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_snapshot is null || now >= _nextCheckTicks)
            {
                _nextCheckTicks = now + CheckIntervalTicks;
                RefreshIfChanged();
            }

            return _snapshot!;
        }
    }

    /// <summary>Forces a reload from disk and returns the new snapshot.</summary>
    public RequestEncryptionKeyring Reload()
    {
        lock (_gate)
        {
            var writeTime = File.GetLastWriteTimeUtc(Path);
            var snapshot = RequestEncryptionKeyring.Load(Path);
            _snapshot = snapshot;
            _snapshotWriteTimeUtc = writeTime;
            _nextCheckTicks = Stopwatch.GetTimestamp() + CheckIntervalTicks;
            return snapshot;
        }
    }

    private void RefreshIfChanged()
    {
        var writeTime = File.GetLastWriteTimeUtc(Path);
        if (_snapshot is not null && writeTime == _snapshotWriteTimeUtc) return;

        try
        {
            _snapshot = RequestEncryptionKeyring.Load(Path);
        }
        catch (IOException) when (_snapshot is not null)
        {
            // A rotation may hold the file for a moment; keep the last good snapshot and retry at the next check.
            return;
        }

        _snapshotWriteTimeUtc = writeTime;
    }
}
