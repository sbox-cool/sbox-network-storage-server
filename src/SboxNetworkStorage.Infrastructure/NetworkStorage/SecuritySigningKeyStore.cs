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
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// The security-config signing key and the rotations that announce it to clients.
///
/// A rotation signs a statement with the key being replaced: "fingerprint A is replaced by B, announced until T".
/// The statement is kept in the rotations file and published, signed, under <c>signing.rotations</c> in every config
/// generated while it is unexpired. A client that trusts A verifies the statement with A's public key, follows to B,
/// and keeps following through later rotations. The rotations file travels with the signing key file.
/// </summary>
public sealed class SecuritySigningKeyStore
{
    /// <summary>Version of the rotations file.</summary>
    public const int FileSchemaVersion = 1;

    /// <summary>Most statements one config publishes. Matches the client library's limit.</summary>
    public const int MaxRotations = 8;

    /// <summary>Domain prefix of every statement signature, so a signature cannot be replayed in another context.</summary>
    public const string StatementDomain = "sboxcool.network-storage.signing-key-rotation.v1";

    /// <summary>How often <see cref="Current"/> checks the files for a change. Tests inject a shorter or fake interval.</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(5);

    private const int RsaKeyBits = 2048;
    private const string KeyIdEnvironmentVariable = "NETWORK_STORAGE_SECURITY_CONFIG_KEY_ID";

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly long _checkIntervalTicks;
    private readonly Func<long> _timestamp;
    private readonly string? _configuredKeyId;
    private readonly ILogger<SecuritySigningKeyStore>? _logger;

    private readonly Lock _gate = new();
    private SecuritySigningSnapshot? _snapshot;
    private DateTime _snapshotKeyWriteTimeUtc;
    private DateTime _snapshotRotationsWriteTimeUtc;
    private long _nextCheckTicks;
    private bool _missingReported;

    public SecuritySigningKeyStore(string keyPath, string rotationsPath, string? configuredKeyId = null,
        ILogger<SecuritySigningKeyStore>? logger = null, TimeSpan? checkInterval = null, Func<long>? timestamp = null)
    {
        KeyPath = keyPath;
        RotationsPath = rotationsPath;
        var keyId = string.IsNullOrWhiteSpace(configuredKeyId)
            ? Environment.GetEnvironmentVariable(KeyIdEnvironmentVariable)
            : configuredKeyId;
        _configuredKeyId = string.IsNullOrWhiteSpace(keyId) ? null : keyId.Trim();
        _logger = logger;
        _checkIntervalTicks = (long)((checkInterval ?? DefaultCheckInterval).TotalSeconds * Stopwatch.Frequency);
        _timestamp = timestamp ?? (() => Stopwatch.GetTimestamp());
    }

    public string KeyPath { get; }

    public string RotationsPath { get; }

    /// <summary>The key and announced statements in effect now, reloaded when a file changed (checked at most every five seconds).</summary>
    public SecuritySigningSnapshot Current()
    {
        lock (_gate)
        {
            var now = _timestamp();
            if (_snapshot is null || now >= _nextCheckTicks)
            {
                _nextCheckTicks = now + _checkIntervalTicks;
                RefreshIfChanged();
            }

            return _snapshot!;
        }
    }

    /// <summary>Forces a reload from disk and returns the new snapshot.</summary>
    public SecuritySigningSnapshot Reload()
    {
        lock (_gate)
        {
            var keyWriteTime = File.GetLastWriteTimeUtc(KeyPath);
            var rotationsWriteTime = RotationsWriteTimeUtc();
            var snapshot = Load();
            _snapshot = snapshot;
            _snapshotKeyWriteTimeUtc = keyWriteTime;
            _snapshotRotationsWriteTimeUtc = rotationsWriteTime;
            _nextCheckTicks = _timestamp() + _checkIntervalTicks;
            return snapshot;
        }
    }

    private void RefreshIfChanged()
    {
        if (_snapshot is not null && !File.Exists(KeyPath))
        {
            // Keep signing with the key already loaded so an accidental delete does not break clients, but say so once.
            if (!_missingReported)
            {
                _missingReported = true;
                _logger?.LogWarning("Security signing key {Path} is missing; continuing with the key already loaded.", KeyPath);
            }

            return;
        }

        _missingReported = false;
        var keyWriteTime = File.GetLastWriteTimeUtc(KeyPath);
        var rotationsWriteTime = RotationsWriteTimeUtc();
        if (_snapshot is not null && keyWriteTime == _snapshotKeyWriteTimeUtc && rotationsWriteTime == _snapshotRotationsWriteTimeUtc)
        {
            return;
        }

        try
        {
            _snapshot = Load();
        }
        catch (IOException) when (_snapshot is not null)
        {
            // A rotation may hold a file for a moment; keep the last good snapshot and retry at the next check.
            return;
        }
        catch (InvalidDataException ex) when (_snapshot is not null)
        {
            // A rotations file that no longer parses must not stop signing; keep the last good snapshot until it changes again.
            _logger?.LogError(ex, "Keeping the last good security signing state; the rotations file could not be loaded.");
        }

        _snapshotKeyWriteTimeUtc = keyWriteTime;
        _snapshotRotationsWriteTimeUtc = rotationsWriteTime;
    }

    private DateTime RotationsWriteTimeUtc()
        => File.Exists(RotationsPath) ? File.GetLastWriteTimeUtc(RotationsPath) : DateTime.MinValue;

    private SecuritySigningSnapshot Load()
    {
        var key = ReadKey(KeyPath);
        try
        {
            var fingerprint = RequestEncryptionKeyring.FingerprintOf(key);
            var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
            var file = ReadRotationsFile(RotationsPath);
            var keyId = file.CurrentFingerprint == fingerprint && file.CurrentKeyId is not null
                ? file.CurrentKeyId
                : _configuredKeyId ?? KeyIdOf(publicKeyPem);
            return new SecuritySigningSnapshot(key, keyId, fingerprint, publicKeyPem, JwkOf(key), LiveChain(file.Links, fingerprint));
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces the signing key and announces the replacement for <paramref name="graceSeconds"/>. The statement is
    /// signed with the key being replaced, so clients that trust it can follow. Nothing changes when the rotation is
    /// refused: a missing key file, an out-of-range grace, or a full announcement list.
    /// </summary>
    public static SigningKeyRotation Rotate(string keyPath, string rotationsPath, DateTimeOffset now, int graceSeconds)
    {
        if (graceSeconds is < 0 or > RequestEncryptionKeyring.MaxGraceSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(graceSeconds), graceSeconds,
                $"Grace must be a whole number from 0 to {RequestEncryptionKeyring.MaxGraceSeconds} seconds.");
        }

        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException("No security signing key exists yet; start the server once to create it.", keyPath);
        }

        // Held across the read-modify-write so two rotations cannot both start from the same key.
        using var lockFile = OwnerOnlyFiles.AcquireLock(keyPath);
        using var previous = ReadKey(keyPath);
        var previousFingerprint = RequestEncryptionKeyring.FingerprintOf(previous);
        var live = LiveChain(ReadRotationsFile(rotationsPath).Links, previousFingerprint);
        var kept = live.Skip(ValidSuffixStart(live, now)).ToList();
        if (kept.Count >= MaxRotations)
        {
            throw new InvalidOperationException(
                $"{MaxRotations} rotations are already announced. The oldest stays announced until {Iso(kept[0].NotAfter)}; rotate again after it expires.");
        }

        using var next = RSA.Create(RsaKeyBits);
        var nextFingerprint = RequestEncryptionKeyring.FingerprintOf(next);
        var notAfter = now.AddSeconds(graceSeconds);
        var statement = new JsonObject
        {
            ["previousFingerprint"] = previousFingerprint,
            ["nextFingerprint"] = nextFingerprint,
            ["nextPublicKeyJwk"] = JwkOf(next),
            ["issuedAt"] = Iso(now),
            ["notAfter"] = Iso(notAfter),
        };
        statement["signature"] = Base64Url.EncodeToString(
            previous.SignData(StatementBytes(statement), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var nextKeyId = KeyIdOf(next.ExportSubjectPublicKeyInfoPem());
        // A zero grace announces nothing, so no link is kept for it. A link with NotAfter = now would still match a
        // config generated in the same second, because generatedAt has whole seconds.
        var links = new List<RotationLink>(kept);
        if (graceSeconds > 0)
        {
            links.Add(new(statement, previousFingerprint, nextFingerprint, notAfter));
        }

        // The rotations file goes first. A crash between the two writes leaves a link the live key does not end,
        // which the next load ignores and the next rotation drops.
        WriteRotationsFile(rotationsPath, links, nextFingerprint, nextKeyId);
        OwnerOnlyFiles.WriteBytes(keyPath, Encoding.UTF8.GetBytes(next.ExportPkcs8PrivateKeyPem()), overwrite: true);
        return new SigningKeyRotation(previousFingerprint, nextFingerprint, nextKeyId, notAfter, links.Count);
    }

    /// <summary>Key id the builder publishes for a public key: the same derivation the server always used.</summary>
    internal static string KeyIdOf(string publicKeyPem)
        => NetworkStorageSecurityConfigBuilder.HashPrefix(Encoding.UTF8.GetBytes(publicKeyPem));

    /// <summary>Index of the first link of the unexpired tail of <paramref name="links"/>; equals Count when the last one has expired.</summary>
    internal static int ValidSuffixStart(IReadOnlyList<RotationLink> links, DateTimeOffset at)
    {
        var start = links.Count;
        while (start > 0 && links[start - 1].NotAfter >= at)
        {
            start--;
        }

        return start;
    }

    /// <summary>Bytes a statement signature covers: the domain, a newline, then the statement without its signature in the config's canonical form.</summary>
    internal static byte[] StatementBytes(JsonObject statement)
    {
        var unsigned = (JsonObject)statement.DeepClone();
        unsigned.Remove("signature");
        var prefix = Encoding.ASCII.GetBytes(StatementDomain + "\n");
        var body = NetworkStorageSecurityConfigBuilder.StableSerialize(unsigned);
        return [.. prefix, .. body];
    }

    /// <summary>The links up to the last one that ends at <paramref name="fingerprint"/>. A link after it is a rotation that never completed.</summary>
    private static IReadOnlyList<RotationLink> LiveChain(IReadOnlyList<RotationLink> links, string fingerprint)
    {
        var end = -1;
        for (var i = 0; i < links.Count; i++)
        {
            if (links[i].NextFingerprint == fingerprint)
            {
                end = i;
            }
        }

        if (end < 0)
        {
            return [];
        }

        return links.Take(end + 1).ToList();
    }

    private static RSA ReadKey(string path)
    {
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException($"Security signing key {path} is not an RSA private key in PEM form.", ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static JsonObject JwkOf(RSA key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["kty"] = "RSA",
            ["n"] = Base64Url.EncodeToString(parameters.Modulus ?? []),
            ["e"] = Base64Url.EncodeToString(parameters.Exponent ?? []),
        };
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static RotationsFile ReadRotationsFile(string path)
    {
        if (!File.Exists(path))
        {
            return new RotationsFile(null, null, []);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw Invalid(path, "is not valid JSON", ex);
        }

        if (node is not JsonObject root)
        {
            throw Invalid(path, "is not a JSON object");
        }

        if (root["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var schema) || schema != FileSchemaVersion)
        {
            throw Invalid(path, $"has an unsupported schemaVersion (expected {FileSchemaVersion})");
        }

        if (root["current"] is not JsonObject current)
        {
            throw Invalid(path, "has no current entry");
        }

        if (root["rotations"] is not JsonArray array)
        {
            throw Invalid(path, "has no rotations array");
        }

        var links = new List<RotationLink>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject statement)
            {
                throw Invalid(path, "has a rotation that is not an object");
            }

            links.Add(ParseLink(path, statement));
        }

        for (var i = 1; i < links.Count; i++)
        {
            if (links[i].PreviousFingerprint != links[i - 1].NextFingerprint)
            {
                throw Invalid(path, "has rotations that do not follow each other");
            }
        }

        return new RotationsFile(StringField(path, current, "fingerprint"), StringField(path, current, "keyId"), links);
    }

    private static RotationLink ParseLink(string path, JsonObject statement)
    {
        var previous = StringField(path, statement, "previousFingerprint");
        var next = StringField(path, statement, "nextFingerprint");
        StringField(path, statement, "issuedAt");
        StringField(path, statement, "signature");
        if (!IsFingerprint(previous) || !IsFingerprint(next) || statement["nextPublicKeyJwk"] is not JsonObject)
        {
            throw Invalid(path, "has a malformed rotation");
        }

        var notAfterText = StringField(path, statement, "notAfter");
        if (!DateTimeOffset.TryParse(notAfterText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var notAfter))
        {
            throw Invalid(path, $"has a rotation with a malformed notAfter '{notAfterText}'");
        }

        return new RotationLink(statement, previous, next, notAfter);
    }

    private static string StringField(string path, JsonObject obj, string name)
    {
        if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return text;
        }

        throw Invalid(path, $"is missing the string field '{name}'");
    }

    private static bool IsFingerprint(string text)
        => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteRotationsFile(string path, IReadOnlyList<RotationLink> links, string currentFingerprint, string currentKeyId)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = FileSchemaVersion,
            ["current"] = new JsonObject
            {
                ["fingerprint"] = currentFingerprint,
                ["keyId"] = currentKeyId,
            },
            ["rotations"] = new JsonArray(links.Select(link => (JsonNode?)link.Statement.DeepClone()).ToArray()),
        };
        OwnerOnlyFiles.WriteBytes(path, Encoding.UTF8.GetBytes(root.ToJsonString(FileJson) + "\n"), overwrite: true);
    }

    private static InvalidDataException Invalid(string path, string problem, Exception? inner = null)
        => new($"Security signing rotations file {path} {problem}.", inner);

    private sealed record RotationsFile(string? CurrentFingerprint, string? CurrentKeyId, IReadOnlyList<RotationLink> Links);
}

/// <summary>
/// The signing key in use and the rotations that lead trusted clients to it. The snapshot owns <see cref="Key"/>;
/// callers must not dispose it.
/// </summary>
public sealed class SecuritySigningSnapshot
{
    internal SecuritySigningSnapshot(RSA key, string keyId, string fingerprint, string publicKeyPem,
        JsonObject publicKeyJwk, IReadOnlyList<RotationLink> rotations)
    {
        Key = key;
        KeyId = keyId;
        Fingerprint = fingerprint;
        PublicKeyPem = publicKeyPem;
        PublicKeyJwk = publicKeyJwk;
        Rotations = rotations;
    }

    public RSA Key { get; }

    public string KeyId { get; }

    /// <summary>Lowercase hex SHA-256 of the public key's SubjectPublicKeyInfo DER.</summary>
    public string Fingerprint { get; }

    public string PublicKeyPem { get; }

    public JsonObject PublicKeyJwk { get; }

    /// <summary>The live chain, oldest first, including expired links. A config publishes only the unexpired tail.</summary>
    public IReadOnlyList<RotationLink> Rotations { get; }

    /// <summary>
    /// Copies of the statements a config generated at <paramref name="generatedAt"/> publishes: the unexpired tail of the
    /// chain, oldest first, at most <see cref="SecuritySigningKeyStore.MaxRotations"/>.
    /// </summary>
    public IReadOnlyList<JsonObject> AnnouncedAt(DateTimeOffset generatedAt)
    {
        var first = Math.Max(
            SecuritySigningKeyStore.ValidSuffixStart(Rotations, generatedAt),
            Rotations.Count - SecuritySigningKeyStore.MaxRotations);
        return [.. Rotations.Skip(first).Select(link => (JsonObject)link.Statement.DeepClone())];
    }
}

/// <summary>One announced rotation: the statement as published, its two fingerprints, and when it stops being announced.</summary>
public sealed record RotationLink(JsonObject Statement, string PreviousFingerprint, string NextFingerprint, DateTimeOffset NotAfter);

/// <summary>What a rotation changed.</summary>
public sealed record SigningKeyRotation(string PreviousFingerprint, string NextFingerprint, string NextKeyId,
    DateTimeOffset AnnouncedUntil, int Statements);
