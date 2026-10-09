using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class SystemdCapabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-capabilities-").FullName;

    private EffectiveConfig Config(string listen, string tls = "off", string https = "0.0.0.0:443")
        => ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"),
            new Dictionary<string, string>
            {
                ["server.listen"] = listen,
                ["tls.mode"] = tls,
                ["tls.https_listen"] = https,
            }, environment: _ => null);

    [Theory]
    [InlineData("0.0.0.0:8080", "off", "0.0.0.0:443", false)]
    [InlineData("0.0.0.0:80", "off", "0.0.0.0:443", true)]
    [InlineData("[::]:8080", "certificate", "[::]:443", true)]
    [InlineData("0.0.0.0:8080", "certificate", "0.0.0.0:8443", false)]
    public void Only_active_privileged_listeners_need_capabilities(string listen, string tls, string https, bool expected)
        => Assert.Equal(expected, SystemdUnits.NeedsBindCapability(Config(listen, tls, https)));

    [Fact]
    public void Moving_to_a_high_port_removes_only_the_managed_grant()
    {
        var unitDirectory = Path.Combine(_root, "units");
        var directory = Path.Combine(unitDirectory, "sbox-ns.service.d");
        Directory.CreateDirectory(directory);
        var operatorPath = Path.Combine(directory, "90-operator.conf");
        File.WriteAllText(operatorPath, "[Service]\nProtectProc=default\n");
        var managedPath = Path.Combine(directory, "10-sbox-ns-bind.conf");

        Assert.True(SystemdUnits.SyncBindDropIn("sbox-ns", Config("0.0.0.0:80"), unitDirectory));
        Assert.Equal("[Service]\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\nAmbientCapabilities=CAP_NET_BIND_SERVICE\n", File.ReadAllText(managedPath));
        Assert.False(SystemdUnits.SyncBindDropIn("sbox-ns", Config("0.0.0.0:80"), unitDirectory));
        Assert.True(SystemdUnits.SyncBindDropIn("sbox-ns", Config("0.0.0.0:8080"), unitDirectory));
        Assert.False(File.Exists(managedPath));
        Assert.Equal("[Service]\nProtectProc=default\n", File.ReadAllText(operatorPath));
        Assert.False(SystemdUnits.SyncBindDropIn("sbox-ns", Config("0.0.0.0:8080"), unitDirectory));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
