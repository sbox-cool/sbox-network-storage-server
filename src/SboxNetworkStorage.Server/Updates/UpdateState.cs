using System.Text.Json;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// Updater state that privileged code trusts: the last-update record of every instance and the
/// previous binary. On Linux it lives in <c>/var/lib/sbox-ns-update</c> (root only), never in a
/// folder the service account can write.
/// </summary>
public sealed class UpdateState(string root, FolderTrust trust)
{
    public const string LinuxDirectory = "/var/lib/sbox-ns-update";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static UpdateState ForHost()
        => new(OperatingSystem.IsLinux() ? LinuxDirectory : Path.Combine(ConfigPaths.InstallDirectory, "update-state"), FolderTrust.Host);

    public string Root => root;

    /// <summary>Where the binary being replaced is kept so rollback can restore it.</summary>
    public string PreviousBinaryPath => Path.Combine(root, "sbox-ns.previous");

    public string RecordPath(string instanceName) => Path.Combine(root, instanceName, "last-update.json");

    /// <summary>Creates the folder (root only) when missing and refuses to continue if it is not trustworthy.</summary>
    public void Ensure()
    {
        if (!Directory.Exists(root))
        {
            CreatePrivateDirectory(root);
        }

        trust.Require(root, "update state folder");
    }

    // The overload that takes a Unix mode throws on Windows even for a default mode.
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The recorded update of <paramref name="instanceName"/>, or null. Null as well when the folder is not readable by this user.</summary>
    public UpdateRecord? Read(string instanceName)
    {
        var path = RecordPath(instanceName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            trust.Require(root, "update state folder");
            return JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(path));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string instanceName, UpdateRecord record)
    {
        Ensure();
        var path = RecordPath(instanceName);
        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        ConfigFiles.WriteAtomically(path, JsonSerializer.Serialize(record, WriteOptions));
    }

    public void Delete(string instanceName) => File.Delete(RecordPath(instanceName));

    /// <summary>Throws unless the folder exists and can be trusted; used before a rollback acts on its contents.</summary>
    public void Require() => trust.Require(root, "update state folder");
}
