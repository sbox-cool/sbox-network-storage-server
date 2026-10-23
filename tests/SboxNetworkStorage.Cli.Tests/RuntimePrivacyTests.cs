using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class RuntimePrivacyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ns-private-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Initialization_repairs_broad_data_access_without_rotating_existing_secrets()
    {
        var configDir = Path.Combine(_root, "config");
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        if (OperatingSystem.IsWindows()) GrantUsers(dataDir);
        else File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.WriteAllText(Path.Combine(dataDir, "existing.db"), "private player data");
        ConfigFiles.WriteAll(configDir, new Dictionary<string, object>());
        var config = ConfigLoader.Load(configDir, dataDir, environment: _ => null);
        var before = ServerSecrets.EnsureAndLoad(config);
        var after = ServerSecrets.EnsureAndLoad(config);
        Assert.Equal(before, after);
        AssertPrivate(dataDir, directory: true);
        AssertPrivate(config.StateDirectory, directory: true);
        AssertPrivate(config.SecretsDirectory, directory: true);
        foreach (var secret in Directory.EnumerateFiles(config.SecretsDirectory)) AssertPrivate(secret, directory: false);
        if (OperatingSystem.IsWindows()) AssertPrivate(Path.Combine(dataDir, "existing.db"), directory: false);
    }

    private static void AssertPrivate(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.False(rule.AccessControlType == AccessControlType.Allow &&
                    (rule.IdentityReference.Equals(users) || rule.IdentityReference.Equals(everyone) || rule.IdentityReference.Equals(authenticated)), path);
        }
        else
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (directory) expected |= UnixFileMode.UserExecute;
            Assert.Equal(expected, File.GetUnixFileMode(path));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantUsers(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
