using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.SignedDns;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class DnsLifecycleTests : IDisposable
{
    private static readonly DnsEnableOptions Terms = new(AcceptLetsEncryptTerms: true, Email: "owner@example.test");
    private readonly string _dir = Directory.CreateTempSubdirectory("sbox-ns-dns-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private EffectiveConfig Load() => ConfigLoader.Load(_dir, Path.Combine(_dir, "data"), environment: _ => null);
    private Dictionary<string, object> Snapshot() => Load().Values.ToDictionary(p => p.Key, p => p.Value.Value);

    private void AssertUnchanged(Dictionary<string, object> before)
    {
        foreach (var pair in before) Assert.Equal(pair.Value, Load().Values[pair.Key].Value);
    }

    private void Configure(string listen = "0.0.0.0:4488")
        => ConfigFiles.WriteAll(_dir, new Dictionary<string, object>
        {
            ["server.listen"] = listen, ["server.public_url"] = "https://own.example.test",
            ["tls.mode"] = "certificate", ["tls.certificate_path"] = "cert.pem", ["tls.key_path"] = "key.pem"
        });

    [Fact]
    public async Task Enable_reenable_disable_restore_original_TLS_and_URL_and_keep_identity()
    {
        Configure();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var manager = new DnsManager(new DnsRegistryClient(http));
        var baseline = File.ReadAllText(Path.Combine(_dir, "server.toml"));

        await manager.EnableAsync(Load(), Terms, CancellationToken.None);
        var enabled = Load();
        Assert.True(enabled.IsValid, string.Join("; ", enabled.Issues));
        var hostname = enabled.GetString("dns.hostname");
        Assert.Matches(@"^[a-z2-7]{12}\.n1\.sboxns\.com$", hostname);
        Assert.Equal($"https://{hostname}", enabled.GetString("server.public_url"));
        Assert.Equal("acme", enabled.GetString("tls.mode"));
        Assert.Equal(hostname, enabled.GetString("tls.acme_domain"));
        Assert.Equal("owner@example.test", enabled.GetString("tls.acme_email"));
        Assert.True(enabled.GetBoolean("tls.acme_accept_terms"));
        Assert.Equal("0.0.0.0:443", enabled.GetString("tls.https_listen"));
        Assert.Equal("0.0.0.0:4488", enabled.GetString("server.listen"));
        Assert.Equal("203.0.113.7", enabled.GetString("dns.ipv4"));
        Assert.Equal("", enabled.GetString("dns.ipv6"));
        Assert.True(enabled.GetBoolean("dns.auto_address"));
        Assert.Equal("https://own.example.test", enabled.GetString("dns.previous_public_url"));
        Assert.Equal("certificate", enabled.GetString("dns.previous_tls_mode"));
        Assert.Equal(4488, registry.LastPort);

        await manager.EnableAsync(Load(), new DnsEnableOptions(), CancellationToken.None);
        Assert.Equal(hostname, Load().GetString("dns.hostname"));
        Assert.Equal("https://own.example.test", Load().GetString("dns.previous_public_url"));
        Assert.Equal("certificate", Load().GetString("dns.previous_tls_mode"));

        await manager.DisableAsync(Load(), CancellationToken.None);
        await manager.DisableAsync(Load(), CancellationToken.None);
        var disabled = Load();
        Assert.True(disabled.IsValid, string.Join("; ", disabled.Issues));
        Assert.False(disabled.GetBoolean("dns.enabled"));
        Assert.Equal("https://own.example.test", disabled.GetString("server.public_url"));
        Assert.Equal("certificate", disabled.GetString("tls.mode"));
        Assert.Equal("", disabled.GetString("tls.acme_domain"));
        Assert.Empty(registry.Names);

        await manager.EnableAsync(Load(), Terms, CancellationToken.None);
        Assert.Equal(hostname, Load().GetString("dns.hostname"));
        Assert.Equal(baseline, File.ReadAllText(Path.Combine(_dir, "server.toml")));
        Assert.Equal(3, registry.Registers);
        Assert.Equal(1, registry.Deletes);
    }

    [Fact]
    public async Task Missing_LetsEncrypt_terms_or_email_changes_nothing_and_sends_nothing()
    {
        Configure();
        var before = Snapshot();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var manager = new DnsManager(new DnsRegistryClient(http));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnableAsync(Load(), new DnsEnableOptions(Email: "a@b.test"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnableAsync(Load(), new DnsEnableOptions(AcceptLetsEncryptTerms: true), CancellationToken.None));
        AssertUnchanged(before);
        Assert.Equal(0, registry.Requests);
    }

    [Fact]
    public async Task Loopback_listener_is_refused_before_contacting_the_registry()
    {
        Configure("127.0.0.1:4488");
        var before = Snapshot();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DnsManager(new DnsRegistryClient(http)).EnableAsync(Load(), Terms, CancellationToken.None));
        AssertUnchanged(before);
        Assert.Equal(0, registry.Requests);
    }

    [Fact]
    public async Task Ownership_proof_failure_reports_the_address_and_changes_nothing()
    {
        Configure();
        var before = Snapshot();
        using var http = new HttpClient(new RegistryHandler { FailWith = (HttpStatusCode)422 });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new DnsManager(new DnsRegistryClient(http)).EnableAsync(Load(), Terms with { Ipv4 = "198.51.100.9" }, CancellationToken.None));
        Assert.Contains("Ownership proof failed", ex.Message);
        Assert.Contains("http://198.51.100.9:4488", ex.Message);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Foreign_registry_hostname_changes_nothing()
    {
        Configure();
        var before = Snapshot();
        using var http = new HttpClient(new RegistryHandler { Hostname = name => $"{name}.n1.sboxns.com.evil.test" });
        await Assert.ThrowsAsync<InvalidDataException>(() => new DnsManager(new DnsRegistryClient(http)).EnableAsync(Load(), Terms, CancellationToken.None));
        AssertUnchanged(before);
    }

    [Fact]
    public async Task Registry_failure_keeps_enabled_configuration_on_disable()
    {
        Configure();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var manager = new DnsManager(new DnsRegistryClient(http));
        await manager.EnableAsync(Load(), Terms, CancellationToken.None);
        var before = Snapshot();
        registry.FailWith = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.DisableAsync(Load(), CancellationToken.None));
        AssertUnchanged(before);
        Assert.Single(registry.Names);
    }

    [Fact]
    public async Task Explicit_addresses_skip_detection_and_disable_automatic_updates()
    {
        Configure();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var manager = new DnsManager(new DnsRegistryClient(http));
        await manager.EnableAsync(Load(), Terms with { Ipv4 = "198.51.100.9", Ipv6 = "2001:db8::5" }, CancellationToken.None);
        Assert.Equal("198.51.100.9", Load().GetString("dns.ipv4"));
        Assert.Equal("2001:db8::5", Load().GetString("dns.ipv6"));
        Assert.False(Load().GetBoolean("dns.auto_address"));
        Assert.Equal(0, registry.WhoAmIs);
        Assert.False(await manager.RefreshAddressAsync(Load(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnableAsync(Load(), Terms with { Ipv4 = "2001:db8::5" }, CancellationToken.None));
    }

    [Fact]
    public async Task Address_refresh_updates_only_when_the_public_address_changes()
    {
        Configure();
        var registry = new RegistryHandler();
        using var http = new HttpClient(registry);
        var manager = new DnsManager(new DnsRegistryClient(http));
        await manager.EnableAsync(Load(), Terms, CancellationToken.None);
        Assert.False(await manager.RefreshAddressAsync(Load(), CancellationToken.None));
        registry.PublicIp = "203.0.113.99";
        Assert.True(await manager.RefreshAddressAsync(Load(), CancellationToken.None));
        Assert.Equal("203.0.113.99", Load().GetString("dns.ipv4"));
        Assert.Equal("update", registry.LastAction);
        Assert.True(Load().IsValid);
    }

    [Fact]
    public async Task Tunnel_and_DNS_modes_are_mutually_exclusive()
    {
        Configure();
        byte[] binary = [1, 2, 3];
        using var tunnelHttp = new HttpClient(new TunnelLifecycleTests.RegistryHandler());
        using var downloads = new HttpClient(new CloudflaredInstallerTests.BytesHandler(binary));
        var tunnels = new TunnelManager(new TunnelRegistryClient(tunnelHttp), new CloudflaredInstaller(downloads,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(binary)))));
        var registry = new RegistryHandler();
        using var dnsHttp = new HttpClient(registry);
        var dns = new DnsManager(new DnsRegistryClient(dnsHttp));

        await tunnels.EnableAsync(Load(), CancellationToken.None);
        var tunnelState = Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => dns.EnableAsync(Load(), Terms, CancellationToken.None));
        AssertUnchanged(tunnelState);
        Assert.Equal(0, registry.Requests);

        await tunnels.DisableAsync(Load(), CancellationToken.None);
        await dns.EnableAsync(Load(), Terms, CancellationToken.None);
        var dnsState = Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tunnels.EnableAsync(Load(), CancellationToken.None));
        AssertUnchanged(dnsState);

        // After DNS disable the managed overlays no longer conflict and tunnel mode works again.
        await dns.DisableAsync(Load(), CancellationToken.None);
        await tunnels.EnableAsync(Load(), CancellationToken.None);
        Assert.True(Load().IsValid, string.Join("; ", Load().Issues));
        Assert.True(Load().GetBoolean("tunnel.enabled"));
    }

    internal sealed class RegistryHandler : HttpMessageHandler
    {
        public HashSet<string> Names { get; } = [];
        public int Registers { get; private set; }
        public int Deletes { get; private set; }
        public int WhoAmIs { get; private set; }
        public int Requests { get; private set; }
        public int LastPort { get; private set; }
        public string? LastAction { get; private set; }
        public string PublicIp { get; set; } = "203.0.113.7";
        public HttpStatusCode? FailWith { get; set; }
        public Func<string, string> Hostname { get; init; } = name => $"{name}.n1.sboxns.com";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            Requests++;
            if (message.Method == HttpMethod.Get)
            {
                Assert.EndsWith("/dns/whoami", message.RequestUri!.AbsolutePath);
                WhoAmIs++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ip = PublicIp }) };
            }
            if (FailWith is { } status) return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"secret upstream detail\"}") };
            var request = await message.Content!.ReadFromJsonAsync<DnsRequest>(cancellationToken: ct) ?? throw new JsonException();
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKeySpki), out _);
            Assert.Equal(request.Name, TunnelIdentity.DeriveName(key.ExportSubjectPublicKeyInfo()));
            Assert.Matches("^[0-9a-f]{32}$", request.Nonce);
            Assert.True(key.VerifyData(TunnelIdentity.DnsPayload(request.Action, request.Name, request.Timestamp, request.Nonce,
                request.Ipv4, request.Ipv6, request.Port), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            LastPort = request.Port;
            LastAction = request.Action;
            if (request.Action is "register" or "update")
            {
                Assert.True(request.Ipv4.Length > 0 || request.Ipv6.Length > 0);
                Registers += request.Action == "register" ? 1 : 0;
                Names.Add(request.Name);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { hostname = Hostname(request.Name), ipv4 = request.Ipv4, ipv6 = request.Ipv6 }) };
            }
            Deletes++;
            Names.Remove(request.Name);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
        }
    }
}
