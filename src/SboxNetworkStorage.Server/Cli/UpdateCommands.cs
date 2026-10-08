using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Updates. Every update verifies the download, backs up the database, keeps the
/// previous binary, and restores both if the migration fails. <c>--auto</c> is the
/// unattended path (<see cref="AutoUpdateCommand"/>).
/// </summary>
public static class UpdateCommands
{
    public static async Task<int> UpdateAsync(CliContext context)
    {
        if (context.Args.Flag("auto"))
        {
            return await AutoUpdateCommand.RunAsync(context);
        }

        var config = context.LoadValidConfig();
        using var http = ReleaseFeed.CreateHttpClient();
        var feed = new ReleaseFeed(http, config);
        var requested = context.Args.Option("version");
        var release = await feed.GetReleaseAsync(requested, CancellationToken.None);
        var notice = UpdateCheckService.Evaluate(BuildInfo.Version, release, DateTimeOffset.UtcNow);

        Console.WriteLine($"Installed: {BuildInfo.Version}");
        Console.WriteLine($"Latest:    {release.Version}{(release.Security ? " (security release)" : string.Empty)}");
        if (release.ChangelogUrl is not null)
        {
            Console.WriteLine($"Changelog: {release.ChangelogUrl}");
        }

        if (context.Args.Flag("check"))
        {
            Console.WriteLine(notice.UpdateAvailable ? "An update is available. Install it with: sbox-ns update" : "You are up to date.");
            return CliApp.Ok;
        }

        if (requested is null && !notice.UpdateAvailable)
        {
            Console.WriteLine("You are up to date.");
            return CliApp.Ok;
        }

        RequireUpgradable(release);
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        RequireStandaloneExecutable(binary);
        using var updateLock = AcquireUpdateLock(binary);
        var work = Directory.CreateTempSubdirectory("sbox-ns-update-");
        try
        {
            var newBinary = await StageReleaseAsync(http, feed, release.Version, binary, work.FullName);

            var serviceInstalled = ServiceCommands.IsInstalled();
            if (serviceInstalled)
            {
                await RequireServiceControlAsync("stop");
            }

            string? backup = null;
            var previous = binary + ".previous";
            var replacementAttempted = false;
            try
            {
                // Quiesce writes before the snapshot so recovery cannot discard
                // writes accepted between the backup and stopping the service.
                backup = await DatabaseBackup.BackupAsync(config, DatabaseBackup.DefaultBackupPath(config, $"pre-{release.Version}"), CancellationToken.None);
                Console.WriteLine($"Database backed up to {backup}");
                File.Copy(binary, previous, overwrite: true);
                replacementAttempted = true;
                BinarySwap.Replace(newBinary, binary);

                var migrate = await RunBinaryAsync(binary, ["db", "migrate", "--config-dir", config.ConfigDirectory, "--data-dir", config.DataDirectory]);
                if (migrate != 0)
                {
                    throw new CliException($"migration exited with code {migrate}");
                }

                UpdateRecord.Write(config, new UpdateRecord(BuildInfo.Version, release.Version, binary, previous, backup, DateTimeOffset.UtcNow, Mode: "manual"));
            }
            catch
            {
                if (replacementAttempted)
                {
                    Console.Error.WriteLine("Update failed; restoring the previous version and database backup.");
                    BinarySwap.Replace(previous, binary);
                    await DatabaseBackup.RestoreAsync(config, backup!, CancellationToken.None);
                }

                if (serviceInstalled)
                {
                    await RequireServiceControlAsync("start");
                }

                throw;
            }

            if (serviceInstalled)
            {
                await RequireServiceControlAsync("start");
            }

            Console.WriteLine($"Updated sbox-ns {BuildInfo.Version} -> {release.Version}. Undo with: sbox-ns rollback");
            return CliApp.Ok;
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    public static async Task<int> RollbackAsync(CliContext context)
    {
        if (context.Args.Flag("all-instances"))
        {
            return await AutoUpdateCommand.RollbackAllAsync(context);
        }

        var config = context.LoadValidConfig();
        var record = UpdateRecord.Read(config) ?? throw new CliException("no update to roll back (nothing recorded by `sbox-ns update`)");
        RequireRollbackable(record);
        if (!File.Exists(record.PreviousBinaryPath) || record.BackupPath is null || !File.Exists(record.BackupPath))
        {
            throw new CliException($"rollback files are missing ({record.PreviousBinaryPath}, {record.BackupPath ?? "no backup"})");
        }

        Console.WriteLine($"Rolling back {record.ToVersion} -> {record.FromVersion} and restoring {record.BackupPath}");
        ConfirmDataLoss(context);

        var serviceInstalled = ServiceCommands.IsInstalled();
        if (serviceInstalled)
        {
            await RequireServiceControlAsync("stop");
        }

        BinarySwap.Replace(record.PreviousBinaryPath, record.BinaryPath);
        await DatabaseBackup.RestoreAsync(config, record.BackupPath, CancellationToken.None);
        UpdateRecord.Delete(config);
        if (serviceInstalled)
        {
            await RequireServiceControlAsync("start");
        }

        Console.WriteLine($"Rolled back to {record.FromVersion}.");
        return CliApp.Ok;
    }

    internal static void RequireRollbackable(UpdateRecord record)
    {
        if (record.IsFailed)
        {
            throw new CliException($"the last update (to {record.ToVersion}) failed; automatic recovery was attempted. Check its recorded reason and service state before manual recovery: {record.Reason}");
        }
    }

    internal static void ConfirmDataLoss(CliContext context)
    {
        if (context.Args.Flag("yes", "y"))
        {
            return;
        }

        Console.Write("Data written since the update will be lost. Continue? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("aborted");
        }
    }

    internal static void RequireStandaloneExecutable(string binary)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(binary), "sbox-ns", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("in-place updates require the installed self-contained sbox-ns executable; do not run update through dotnet");
        }
    }

    internal static void RequireUpgradable(ReleaseInfo release)
    {
        if (release.MinUpgradableFrom is { } minimum
            && SemanticVersion.TryParse(minimum, out var min)
            && SemanticVersion.TryParse(BuildInfo.Version, out var current)
            && current.CompareTo(min) < 0)
        {
            throw new CliException($"{release.Version} cannot be installed directly over {BuildInfo.Version}. Install {minimum} first: sbox-ns update --version {minimum}");
        }
    }

    /// <summary>Held for the whole update so a manual and an unattended update never swap the binary at once.</summary>
    internal static FileStream AcquireUpdateLock(string binary)
    {
        try
        {
            return new FileStream(binary + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new CliException("another sbox-ns update is running; try again when it has finished");
        }
    }

    /// <summary>Downloads, verifies (checksum, and signature when cosign is installed) and extracts a release; returns the new binary.</summary>
    internal static async Task<string> StageReleaseAsync(HttpClient http, ReleaseFeed feed, string version, string binary, string work)
    {
        var archiveName = $"sbox-ns-{version}-{BuildInfo.RuntimeIdentifier}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        var archive = Path.Combine(work, archiveName);
        Console.WriteLine($"Downloading {archiveName}...");
        await DownloadAsync(http, feed.AssetUrl(version, archiveName), archive);
        var sums = Path.Combine(work, "SHA256SUMS");
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS"), sums);
        VerifyChecksum(archive, archiveName, sums);
        await VerifySignatureAsync(http, feed, version, sums, work);

        var extracted = Path.Combine(work, "extracted");
        Extract(archive, extracted);
        var newBinary = Path.Combine(extracted, Path.GetFileName(binary));
        if (!File.Exists(newBinary))
        {
            throw new CliException($"{archiveName} does not contain {Path.GetFileName(binary)}");
        }

        return newBinary;
    }

    private static async Task RequireServiceControlAsync(string action)
    {
        var exitCode = await ServiceCommands.ControlAsync(action);
        if (exitCode != 0)
        {
            throw new CliException($"could not {action} the sbox-ns service (exit {exitCode}); no further update steps were performed");
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"download failed ({(int)response.StatusCode}) {url}");
        }

        await using var output = File.Create(target);
        await response.Content.CopyToAsync(output);
    }

    internal static void VerifyChecksum(string file, string assetName, string sumsFile)
    {
        var expected = File.ReadAllLines(sumsFile)
            .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*').Trim() == assetName)
            .Select(parts => parts[0].ToLowerInvariant())
            .FirstOrDefault() ?? throw new CliException($"{assetName} is not listed in SHA256SUMS");

        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expected)
        {
            throw new CliException($"checksum mismatch for {assetName}: expected {expected}, got {actual}. Nothing was installed.");
        }

        Console.WriteLine("Checksum verified.");
    }

    private static async Task VerifySignatureAsync(HttpClient http, ReleaseFeed feed, string version, string sums, string work)
    {
        if (ServiceCommands.RunCapture("cosign", ["version"]).ExitCode != 0)
        {
            Console.WriteLine("cosign not found; skipping signature verification (checksums were verified over HTTPS).");
            return;
        }

        var signature = Path.Combine(work, "SHA256SUMS.sig");
        var certificate = Path.Combine(work, "SHA256SUMS.pem");
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.sig"), signature);
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.pem"), certificate);
        var result = ServiceCommands.RunCapture("cosign",
        [
            "verify-blob", "--signature", signature, "--certificate", certificate,
            "--certificate-identity-regexp", $"^https://github.com/{feed.GitHubRepository}/",
            "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com", sums
        ]);
        if (result.ExitCode != 0)
        {
            throw new CliException("signature verification of SHA256SUMS failed. Nothing was installed.");
        }

        Console.WriteLine("Signature verified.");
    }

    private static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.Ordinal))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static async Task<int> RunBinaryAsync(string binary, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(binary) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
