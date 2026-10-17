using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class AdminLinkCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-login-link-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private EffectiveConfig Load(Dictionary<string, string> flags)
        => ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"), flags, _ => null);

    [Fact]
    public void Public_url_wins_and_loses_its_trailing_slash()
        => Assert.Equal("https://ns.example.com", ServerBaseUrl.FromConfig(
            Load(new() { ["server.public_url"] = "https://ns.example.com/" }), () => "203.0.113.7"));

    [Fact]
    public void Wildcard_http_listener_uses_the_detected_address_and_port()
        => Assert.Equal("http://203.0.113.7:8080", ServerBaseUrl.FromConfig(
            Load(new() { ["server.listen"] = "0.0.0.0:8080" }), () => "203.0.113.7"));

    [Fact]
    public void Wildcard_ipv6_listener_uses_the_bracketed_detected_address()
        => Assert.Equal("http://[2001:db8::1]:8080", ServerBaseUrl.FromConfig(
            Load(new() { ["server.listen"] = "[::]:8080" }), () => "[2001:db8::1]"));

    [Fact]
    public void Explicit_listen_address_is_used_verbatim()
        => Assert.Equal("http://198.51.100.4:9000", ServerBaseUrl.FromConfig(
            Load(new() { ["server.listen"] = "198.51.100.4:9000" }), () => throw new InvalidOperationException("not needed")));

    [Fact]
    public void Acme_tls_uses_the_certificate_domain_over_https()
        => Assert.Equal("https://ns.example.org", ServerBaseUrl.FromConfig(Load(new()
        {
            ["tls.mode"] = "acme",
            ["tls.acme_domain"] = "ns.example.org",
            ["tls.acme_email"] = "ops@example.org",
            ["tls.acme_accept_terms"] = "true",
            ["tls.https_listen"] = "0.0.0.0:443"
        }), () => "203.0.113.7"));

    [Theory]
    [InlineData("http://127.0.0.1:8080", false)]
    [InlineData("http://localhost:8443", false)]
    [InlineData("http://localhost", false)]
    [InlineData("http://127.0.0.1:8090", true)]
    [InlineData("http://[::1]:8081", true)]
    [InlineData("http://203.0.113.7:8080", true)]
    [InlineData("http://[2001:db8::1]:8080", true)]
    [InlineData("https://203.0.113.7", true)]
    [InlineData("http://ns.example.com:3000", false)]
    [InlineData("https://ns.example.com", false)]
    public void Game_clients_refuse_ip_hosts_and_loopback_outside_the_allowed_ports(string url, bool refused)
        => Assert.Equal(refused, ServerBaseUrl.GameClientBlockReason(url) is not null);

    [Theory]
    [InlineData(null, 15)]
    [InlineData("1", 1)]
    [InlineData("60", 60)]
    public void Minutes_default_to_fifteen_and_allow_up_to_an_hour(string? value, int expected)
        => Assert.Equal(expected, AdminLinkCommand.ParseMinutes(value));

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("-5")]
    [InlineData("ten")]
    public void Minutes_outside_the_range_are_usage_errors(string value)
        => Assert.Equal(CliApp.Usage, Assert.Throws<CliException>(() => AdminLinkCommand.ParseMinutes(value)).ExitCode);
}
