using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>Product identity: user-visible names say Network Storage.</summary>
public sealed class CliBrandingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-branding-").FullName;

    [Fact]
    public void Help_banner_names_network_storage()
    {
        Assert.Contains("self-hosted s&box Network Storage server", CliApp.HelpText);
        Assert.DoesNotContain("Networked Storage", CliApp.HelpText);
    }
    [Fact]
    public void Rendered_service_unit_describes_network_storage()
    {
        if (OperatingSystem.IsWindows()) return; // Units are Linux-only.
        var config = ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"), environment: _ => null);
        var rendered = SystemdUnits.ServiceUnit("/usr/local/bin/sbox-ns", config, "sbox-ns");
        Assert.Contains("Description=sbox Network Storage Server", rendered);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
