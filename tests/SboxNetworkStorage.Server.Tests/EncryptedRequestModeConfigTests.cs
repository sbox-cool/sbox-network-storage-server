using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Encrypted-request modes on the signed security config: the mode matrix, legacy
/// enableEncryptedRequests migration, the v2 encryption block, and signature coverage.
/// </summary>
[Collection(SecurityConfigProcessEnvironmentCollection.Name)]
public sealed class EncryptedRequestModeConfigTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbox-ns-mode-" + Guid.NewGuid().ToString("N"));
    private readonly string _keysPath;
    private readonly RequestEncryptionKeyStore _store;

    public EncryptedRequestModeConfigTests()
    {
        Directory.CreateDirectory(_folder);
        _keysPath = Path.Combine(_folder, "request_encryption_keys.json");
        RequestEncryptionKeyring.EnsureAndLoad(_keysPath, Now);
        _store = new RequestEncryptionKeyStore(_keysPath);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("off", true, false, false, false, false)]
    [InlineData("v1", false, true, false, false, true)]
    [InlineData("v2-preferred", false, true, true, true, true)]
    [InlineData("v2-required", false, false, true, true, true)]
    public void Mode_matrix_matches_the_denial_rules(string mode, bool plain, bool v1, bool v2, bool offersV2, bool requires)
    {
        Assert.Equal(plain, EncryptedRequestModes.AcceptsPlain(mode));
        Assert.Equal(v1, EncryptedRequestModes.AcceptsV1(mode));
        Assert.Equal(v2, EncryptedRequestModes.AcceptsV2(mode));
        Assert.Equal(offersV2, EncryptedRequestModes.OffersV2(mode));
        Assert.Equal(requires, EncryptedRequestModes.RequiresEncryption(mode));
    }

    [Theory]
    [InlineData("v2-required", false, "v2-required")]
    [InlineData("bogus", true, "v1")]
    [InlineData(null, true, "v1")]
    [InlineData(null, false, "off")]
    [InlineData(null, null, "off")]
    public void Resolve_prefers_a_valid_stored_mode_and_maps_legacy_true_to_v1(string? stored, bool? legacy, string expected)
        => Assert.Equal(expected, EncryptedRequestModes.Resolve(stored, legacy));

    [Theory]
    [InlineData("off", "disabled", false)]
    [InlineData("v1", "required", true)]
    public void Off_and_v1_publish_no_encryption_block(string mode, string encryptedRequests, bool enabled)
    {
        var config = Build(Project($",\"encryptedRequestMode\":\"{mode}\""));

        Assert.False(config.TryGetProperty("encryption", out _));
        Assert.Equal(encryptedRequests, config.GetProperty("modes").GetProperty("encryptedRequests").GetString());
        var settings = config.GetProperty("settings");
        Assert.Equal(enabled, settings.GetProperty("enableEncryptedRequests").GetBoolean());
        Assert.False(settings.TryGetProperty("encryptedRequestMode", out _));
    }

    [Fact]
    public void Absent_mode_is_byte_identical_to_explicit_off()
    {
        var absent = Build(Project());
        var explicitOff = Build(Project(",\"encryptedRequestMode\":\"off\""));

        Assert.Equal(absent.GetProperty("configVersion").GetString(), explicitOff.GetProperty("configVersion").GetString());
        Assert.Equal(absent.GetRawText(), explicitOff.GetRawText());
    }

    [Fact]
    public void Legacy_enable_flag_migrates_to_v1_without_a_v2_block()
    {
        var config = Build(Project(",\"enableEncryptedRequests\":true"));

        Assert.False(config.TryGetProperty("encryption", out _));
        Assert.Equal("required", config.GetProperty("modes").GetProperty("encryptedRequests").GetString());
        Assert.True(config.GetProperty("settings").GetProperty("enableEncryptedRequests").GetBoolean());
    }

    [Theory]
    [InlineData("v2-preferred", "required", false)]
    [InlineData("v2-required", "required", true)]
    public void V2_modes_publish_the_encryption_block_for_the_current_key(string mode, string encryptedRequests, bool required)
    {
        var config = Build(Project($",\"encryptedRequestMode\":\"{mode}\""));
        var key = _store.Current().Current;
        var block = config.GetProperty("encryption");

        Assert.Equal("rsa-oaep-256+aes-256-gcm", block.GetProperty("algorithm").GetString());
        Assert.Equal(key.KeyId, block.GetProperty("keyId").GetString());
        Assert.Equal(key.Fingerprint, block.GetProperty("fingerprint").GetString());
        Assert.Equal(required, block.GetProperty("required").GetBoolean());
        Assert.Equal(key.PublicKeyJwk["n"]!.GetValue<string>(), block.GetProperty("publicKeyJwk").GetProperty("n").GetString());
        Assert.Equal(key.PublicKeyJwk["e"]!.GetValue<string>(), block.GetProperty("publicKeyJwk").GetProperty("e").GetString());
        Assert.Equal(encryptedRequests, config.GetProperty("modes").GetProperty("encryptedRequests").GetString());
        Assert.Equal(mode, config.GetProperty("settings").GetProperty("encryptedRequestMode").GetString());
    }

    [Fact]
    public void Signature_covers_the_encryption_block()
    {
        var config = Build(Project(",\"encryptedRequestMode\":\"v2-required\""));
        var signature = Base64UrlDecode(config.GetProperty("signature").GetString()!);
        var publicKeyPem = config.GetProperty("signing").GetProperty("publicKeyPem").GetString()!;

        var payload = JsonNode.Parse(config.GetRawText())!.AsObject();
        payload.Remove("signature");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);

        Assert.True(rsa.VerifyData(NetworkStorageSecurityConfigBuilder.StableSerialize(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        payload["encryption"]!.AsObject()["required"] = false;
        Assert.False(rsa.VerifyData(NetworkStorageSecurityConfigBuilder.StableSerialize(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Key_rotation_changes_the_published_key_but_not_the_config_version()
    {
        var project = Project(",\"encryptedRequestMode\":\"v2-preferred\"");
        var before = Build(project);
        var previousKeyId = _store.Current().Current.KeyId;

        RequestEncryptionKeyring.Rotate(_keysPath, Now.AddMinutes(1), 3600);
        _store.Reload();
        var after = Build(project);

        Assert.NotEqual(previousKeyId, after.GetProperty("encryption").GetProperty("keyId").GetString());
        Assert.Equal(before.GetProperty("configVersion").GetString(), after.GetProperty("configVersion").GetString());
    }

    private static JsonElement Project(string extra = "") =>
        JsonDocument.Parse("{\"id\":\"demo-project\",\"authSessionTtlSeconds\":7200" + extra + "}").RootElement.Clone();

    private JsonElement Build(JsonElement project) =>
        NetworkStorageSecurityConfigBuilder.Build("demo-project", project, Now, _store);

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
