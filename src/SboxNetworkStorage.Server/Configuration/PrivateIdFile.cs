using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>A random UUID persisted in an owner-only file (mode 0600 / owner-only ACL), created at most once.</summary>
public static class PrivateIdFile
{
    /// <summary>Returns the stored ID, or null when the file is missing or does not hold a valid non-empty UUID.</summary>
    public static Guid? Read(string path)
        => File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) && id != Guid.Empty ? id : null;

    /// <summary>
    /// Creates the file with a new random UUIDv4 when it does not exist (concurrent creators keep the first),
    /// then returns the stored ID, or null when an existing file is invalid.
    /// </summary>
    public static Guid? LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var file = new FileStream(temporary, options))
                using (var writer = new StreamWriter(file))
                    writer.Write(Guid.NewGuid().ToString("D"));
                TunnelFiles.Restrict(temporary);
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        var id = Read(path);
        if (id is not null) TunnelFiles.Restrict(path);
        return id;
    }
}
