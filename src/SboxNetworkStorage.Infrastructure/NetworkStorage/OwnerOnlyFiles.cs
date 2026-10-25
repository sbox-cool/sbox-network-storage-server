using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Atomic, owner-only writes and the exclusive sibling lock for the server's key files. A write goes to a temp file
/// beside the target, is flushed to disk, and is then moved over the target, so a reader sees the old file or the new
/// one, never a partial one.
/// </summary>
internal static class OwnerOnlyFiles
{
    /// <summary>Replaces <paramref name="path"/> with <paramref name="bytes"/>. Owner-only from creation on Unix.</summary>
    internal static void WriteBytes(string path, byte[] bytes, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Owner-only from creation on Unix, matching ServerSecrets.EnsureFile.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(temp, full, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void MoveIntoPlace(string temp, string path, bool overwrite)
    {
        // Windows reports a sharing violation while a reader holds the old file open; retry briefly there.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite);
                return;
            }
            catch (Exception ex) when (overwrite && OperatingSystem.IsWindows() && attempt < 10
                                       && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(attempt * 20);
            }
        }
    }

    /// <summary>
    /// Exclusive lock on the file's sibling <c>.lock</c> file. FileShare.None is an OS lock (sharing mode on
    /// Windows, flock on Unix), so it is released if the process dies. Waits up to ten seconds for another holder.
    /// </summary>
    internal static FileStream AcquireLock(string path)
    {
        var lockPath = Path.GetFullPath(path) + ".lock";
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        for (var attempt = 1; ; attempt++)
        {
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(Math.Min(attempt * 20, 200));
            }
        }
    }
}
