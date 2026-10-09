using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// The units the CLI writes are the files under <c>install/</c> with the placeholders substituted,
/// so the files the installer embeds and the units <c>service install</c> writes cannot drift apart.
/// </summary>
public sealed class SystemdUnitRenderTests : IDisposable
{
    // A user that does not exist: the group then falls back to the user name.
    private const string User = "sbox-ns-render-test";
    private const string Binary = "/usr/local/bin/sbox-ns";

    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-units-").FullName;

    private static string InstallFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "install", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"install/{name} not found above {AppContext.BaseDirectory}");
    }

    private static void AssertNoPlaceholders(string unit)
        => Assert.DoesNotMatch(new Regex("@[A-Z_]+@"), string.Join('\n', unit.Split('\n').Where(line => !line.TrimStart().StartsWith('#'))));

    [Fact]
    public void Service_unit_is_the_install_file_with_placeholders_substituted()
    {
        if (OperatingSystem.IsWindows()) return; // systemd units quote Windows paths differently; the units are Linux-only.
        var config = ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"), environment: _ => null);
        var expected = InstallFile("sbox-ns.service")
            .Replace("@BINARY@", Binary)
            .Replace("@USER@", User)
            .Replace("@GROUP@", User)
            .Replace("@CONFIG_DIR@", config.ConfigDirectory)
            .Replace("@DATA_DIR@", config.DataDirectory)
            .Replace("@READ_WRITE_PATHS@", config.DataDirectory);

        var rendered = SystemdUnits.ServiceUnit(Binary, config, User);

        Assert.Equal(expected, rendered);
        AssertNoPlaceholders(rendered);
    }

    [Fact]
    public void Unmigrated_layout_keeps_the_config_folder_writable_only_when_asked()
    {
        if (OperatingSystem.IsWindows()) return; // systemd units quote Windows paths differently; the units are Linux-only.
        var config = ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"), environment: _ => null);

        var line = SystemdUnits.ServiceUnit(Binary, config, User, writableConfig: true)
            .Split('\n').Single(l => l.StartsWith("ReadWritePaths=", StringComparison.Ordinal));

        Assert.Equal($"ReadWritePaths={config.DataDirectory} {config.ConfigDirectory}", line);
    }

    [Fact]
    public void Instance_template_is_the_install_file_with_placeholders_substituted()
    {
        var expected = InstallFile("sbox-ns@.service")
            .Replace("@BINARY@", Binary)
            .Replace("@USER@", User)
            .Replace("@GROUP@", User)
            .Replace("@READ_WRITE_PATHS@", "/var/lib/sbox-ns/%i");

        var rendered = SystemdUnits.InstanceTemplate(Binary, User);

        Assert.Equal(expected, rendered);
        AssertNoPlaceholders(rendered);
    }

    [Fact]
    public void Update_units_are_the_install_files_with_placeholders_substituted()
    {
        var service = SystemdUnits.UpdateService(Binary, "--channel stable");

        Assert.Equal(InstallFile("sbox-ns-update.service").Replace("@BINARY@", Binary).Replace("@ARGUMENTS@", "--channel stable"), service);
        AssertNoPlaceholders(service);
        Assert.Equal(InstallFile("sbox-ns-update.timer"), SystemdUnits.UpdateTimerUnit);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
