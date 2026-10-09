using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class TunnelLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbox-ns-tunnel-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private EffectiveConfig Load() => ConfigLoader.Load(_dir, Path.Combine(_dir, "data"), environment: _ => null);

    private void Configure()
        => ConfigFiles.WriteAll(_dir, new Dictionary<string, object>
        {
            ["server.listen"] = "0.0.0.0:4488", ["server.public_url"] = "https://own.example.test",
            ["tls.mode"] = "certificate", ["tls.certificate_path"] = "cert.pem", ["tls.key_path"] = "key.pem"
        });

    [Fact]
    public async Task Enable_reenable_disable_and_reenable_preserve_identity_and_restore_original_TLS_and_URL()
    {
        Configure();
        var registry = new RegistryHandler();
        using var registryHttp = new HttpClient(registry);
        byte[] binary = [1, 2, 3, 4];
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(binary));
        var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))));
        var baseline = File.ReadAllText(Path.Combine(_dir, "server.toml"));
        await manager.EnableAsync(Load(), CancellationToken.None);
        var enabled = Load();
        Assert.True(enabled.IsValid, string.Join("; ", enabled.Issues));
        Assert.True(enabled.GetBoolean("tunnel.enabled"));
        Assert.Equal("127.0.0.1:4488", enabled.GetString("server.listen"));
        Assert.Equal("off", enabled.GetString("tls.mode"));
        Assert.Equal("https://" + enabled.GetString("tunnel.hostname"), enabled.GetString("server.public_url"));
        var name = enabled.GetString("tunnel.name");
        Assert.Equal("test-token", File.ReadAllText(TunnelManager.TokenPath(enabled)));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TunnelManager.TokenPath(enabled)));
        // A missing token after interruption is recovered by a signed idempotent register.
        File.Delete(TunnelManager.TokenPath(enabled));
        await manager.EnableAsync(Load(), CancellationToken.None);
        Assert.Equal(name, Load().GetString("tunnel.name"));
        Assert.Equal("0.0.0.0:4488", Load().GetString("tunnel.previous_listen"));
        Assert.Single(registry.Names);
        await manager.DisableAsync(Load(), CancellationToken.None);
        await manager.DisableAsync(Load(), CancellationToken.None);
        var disabled = Load();
        Assert.True(disabled.IsValid, string.Join("; ", disabled.Issues));
        Assert.False(disabled.GetBoolean("tunnel.enabled"));
        Assert.Equal("0.0.0.0:4488", disabled.GetString("server.listen"));
        Assert.Equal("https://own.example.test", disabled.GetString("server.public_url"));
        Assert.Equal("certificate", disabled.GetString("tls.mode"));
        Assert.False(File.Exists(TunnelManager.TokenPath(disabled)));
        Assert.Empty(registry.Names);
        await manager.EnableAsync(Load(), CancellationToken.None);
        Assert.Equal(name, Load().GetString("tunnel.name"));
        Assert.Equal(baseline, File.ReadAllText(Path.Combine(_dir, "server.toml")));
        Assert.Equal(3, registry.Registers);
        Assert.Equal(1, registry.Deletes);
    }

    [Fact]
    public async Task Failed_checksum_after_registration_changes_no_config_or_existing_token()
    {
        Configure();
        var before = Load().Values.ToDictionary(p => p.Key, p => p.Value.Value);
        TunnelFiles.WriteSecret(TunnelManager.TokenPath(Load()), "previous-token");
        using var registryHttp = new HttpClient(new RegistryHandler());
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler([1, 2, 3]));
        var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", new string('0', 64))));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnableAsync(Load(), CancellationToken.None));
        foreach (var pair in before) Assert.Equal(pair.Value, Load().Values[pair.Key].Value);
        Assert.Equal("previous-token", File.ReadAllText(TunnelManager.TokenPath(Load())));
        Assert.False(File.Exists(CloudflaredInstaller.ExecutablePath(Load().ExecutablesDirectory)));
    }

    [Fact]
    public async Task Registry_failure_keeps_enabled_configuration_and_token_on_disable()
    {
        Configure();
        var registry = new RegistryHandler();
        using var registryHttp = new HttpClient(registry);
        byte[] binary = [1];
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(binary));
        var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))));
        await manager.EnableAsync(Load(), CancellationToken.None);
        registry.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.DisableAsync(Load(), CancellationToken.None));
        Assert.True(Load().GetBoolean("tunnel.enabled"));
        Assert.Equal("test-token", File.ReadAllText(TunnelManager.TokenPath(Load())));
        Assert.Single(registry.Names);
    }

    [Fact]
    public async Task Invalid_registry_hostname_cannot_change_listener_public_URL_or_install_connector()
    {
        Configure();
        using var registryHttp = new HttpClient(new InvalidHostnameHandler());
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler([1]));
        var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloads));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnableAsync(Load(), CancellationToken.None));
        Assert.Equal("0.0.0.0:4488", Load().GetString("server.listen"));
        Assert.Equal("https://own.example.test", Load().GetString("server.public_url"));
        Assert.False(Load().GetBoolean("tunnel.enabled"));
        Assert.False(File.Exists(TunnelManager.TokenPath(Load())));
        Assert.False(File.Exists(CloudflaredInstaller.ExecutablePath(Load().ExecutablesDirectory)));
    }

    private sealed class InvalidHostnameHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { hostname = "https://evil.test", tunnelToken = "test-token" })
            });
    }

    internal sealed class RegistryHandler : HttpMessageHandler
    {
        public HashSet<string> Names { get; } = [];
        public int Registers { get; private set; }
        public int Deletes { get; private set; }
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var request = await message.Content!.ReadFromJsonAsync<TunnelRequest>(cancellationToken: ct) ?? throw new JsonException();
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKeySpki), out _);
            Assert.Equal(request.Name, TunnelIdentity.DeriveName(key.ExportSubjectPublicKeyInfo()));
            Assert.True(key.VerifyData(TunnelIdentity.Payload(request.Action, request.Name, request.Timestamp, request.Nonce,
                request.LocalPort), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            if (request.Action == "register")
            {
                Registers++;
                Names.Add(request.Name);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { hostname = $"https://{request.Name}.sboxns.com", tunnelToken = "test-token" }) };
            }
            Deletes++;
            Names.Remove(request.Name);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
        }
    }
}
