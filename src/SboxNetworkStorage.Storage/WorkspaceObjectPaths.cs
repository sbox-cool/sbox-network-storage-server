namespace SboxNetworkStorage.Storage;

/// <summary>
/// Path rules shared by every <see cref="INetworkStorageStore"/> driver for the
/// workspace object family, so listing semantics cannot drift between drivers.
/// </summary>
public static class WorkspaceObjectPaths
{
    /// <summary>Normalizes an object path: forward slashes, no leading or trailing slash.</summary>
    public static string NormalizeObjectPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Workspace object path must not be empty.", nameof(path));
        }

        return normalized;
    }

    /// <summary>Normalizes a directory path to the prefix form used for matching (<c>a/b/</c>, or empty for the root).</summary>
    public static string NormalizeDirectoryPrefix(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        var trimmed = directoryPath.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// Collapses every object below <paramref name="directoryPrefix"/> into the
    /// immediate children of that directory, ordered by name (ordinal).
    /// </summary>
    public static IReadOnlyList<WorkspaceObjectEntry> ImmediateChildren(
        string directoryPrefix,
        IEnumerable<(string Path, long LengthBytes, DateTimeOffset LastChanged)> objectsUnderPrefix)
    {
        var files = new Dictionary<string, WorkspaceObjectEntry>(StringComparer.Ordinal);
        var directories = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var (path, length, lastChanged) in objectsUnderPrefix)
        {
            if (!path.StartsWith(directoryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = path[directoryPrefix.Length..];
            var slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                files[remainder] = new WorkspaceObjectEntry(remainder, IsDirectory: false, length, lastChanged);
                continue;
            }

            var directoryName = remainder[..slash];
            directories[directoryName] = directories.TryGetValue(directoryName, out var existing) && existing > lastChanged
                ? existing
                : lastChanged;
        }

        return directories
            .Select(d => new WorkspaceObjectEntry(d.Key, IsDirectory: true, LengthBytes: null, LastChanged: d.Value))
            .Concat(files.Values)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.IsDirectory)
            .ToList();
    }
}
