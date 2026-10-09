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
        var state = UpdateState.ForHost();
        state.Ensure();
        var host = new SystemUpdateHost();
        var instance = ServerInstances.ForConfig(config);
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
            var previous = state.PreviousBinaryPath;
            var replacementAttempted = false;
            try
            {
                // Quiesce writes before the snapshot so recovery cannot discard
                // writes accepted between the backup and stopping the service.
                backup = DatabaseBackup.DefaultBackupPath(config, $"pre-{release.Version}");
                RequireSuccess(await host.BackupAsync(binary, instance, backup, CancellationToken.None), "database backup");
                Console.WriteLine($"Database backed up to {backup}");
                File.Copy(binary, previous, overwrite: true);
                replacementAttempted = true;
                BinarySwap.Replace(newBinary, binary);

                RequireSuccess(await host.MigrateAsync(binary, instance, CancellationToken.None), "migration");
                state.Write(instance.Name, new UpdateRecord(BuildInfo.Version, release.Version, binary, previous, backup, DateTimeOffset.UtcNow, Mode: "manual"));
            }
            catch
            {
                if (replacementAttempted)
                {
                    Console.Error.WriteLine("Update failed; restoring the previous version and database backup.");
                    BinarySwap.Replace(previous, binary);
                    RequireSuccess(await host.RestoreAsync(binary, instance, backup!, CancellationToken.None), "database restore");
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
        var state = UpdateState.ForHost();
        var instance = ServerInstances.ForConfig(config);
        var record = state.Read(instance.Name) ?? throw new CliException("no update to roll back (nothing recorded by `sbox-ns update`)");
        RequireRollbackable(record);
        state.Require();

        // The record only says what happened. Rollback restores the fixed installed binary from the
        // root-only state folder and never follows a path stored in the record.
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        RequireStandaloneExecutable(binary);
        if (!File.Exists(state.PreviousBinaryPath) || record.BackupPath is null || !File.Exists(record.BackupPath))
        {
            throw new CliException($"rollback files are missing ({state.PreviousBinaryPath}, {record.BackupPath ?? "no backup"})");
        }

        Console.WriteLine($"Rolling back {record.ToVersion} -> {record.FromVersion} and restoring {record.BackupPath}");
        ConfirmDataLoss(context);
        using var updateLock = AcquireUpdateLock(binary);

        var serviceInstalled = ServiceCommands.IsInstalled();
        if (serviceInstalled)
        {
            await RequireServiceControlAsync("stop");
        }

        // The binary being restored may predate the state folder and expect its secrets in the config folder.
        var host = new SystemUpdateHost();
        try
        {
            RequireSuccess(await host.RevertLayoutAsync(binary, [instance], CancellationToken.None), "layout revert");
        }
        catch (LayoutMigrationException ex)
        {
            throw new CliException($"cannot move the runtime files back into the config folder: {ex.Message}. The previous binary was not restored.");
        }

        BinarySwap.Replace(state.PreviousBinaryPath, binary);
        RequireSuccess(await host.RestoreAsync(binary, instance, record.BackupPath, CancellationToken.None), "database restore");
        state.Delete(instance.Name);
        if (serviceInstalled)
        {
            await RequireServiceControlAsync("start");
        }

        Console.WriteLine($"Rolled back to {record.FromVersion}.");
        return CliApp.Ok;
    }

    private static void RequireSuccess(int exitCode, string step)
    {
        if (exitCode != 0)
        {
            throw new CliException($"{step} exited with code {exitCode}");
        }
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

    /// <summary>
    /// Downloads a release, verifies the pinned-key signature of SHA256SUMS and then the archive checksum,
    /// and extracts it; returns the new binary. Any missing or invalid signature aborts before anything is installed.
    /// </summary>
    internal static async Task<string> StageReleaseAsync(HttpClient http, ReleaseFeed feed, string version, string binary, string work)
    {
        var archiveName = $"sbox-ns-{version}-{BuildInfo.RuntimeIdentifier}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        var archive = Path.Combine(work, archiveName);
        var sums = Path.Combine(work, "SHA256SUMS");
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS"), sums);
        var signature = Path.Combine(work, ReleaseSignature.AssetName);
        await DownloadRequiredAsync(http, feed.AssetUrl(version, ReleaseSignature.AssetName), signature);
        VerifyReleaseSignature(sums, signature, feed.Trust);
        Console.WriteLine($"Downloading {archiveName}...");
        await DownloadAsync(http, feed.AssetUrl(version, archiveName), archive);
        VerifyChecksum(archive, archiveName, sums);
        await VerifyCosignAsync(http, feed, version, sums, work);

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

    /// <summary>Checks the detached P-256 signature of SHA256SUMS against the keys pinned in the binary.</summary>
    internal static void VerifyReleaseSignature(string sumsFile, string signatureFile, UpdateTrust trust)
    {
        if (trust.SigningKeys.Count == 0)
        {
            throw new CliException("this build has no pinned release signing key, so no release can be verified. Nothing was installed.");
        }

        if (!ReleaseSignature.Verify(File.ReadAllBytes(sumsFile), File.ReadAllBytes(signatureFile), trust.SigningKeys))
        {
            throw new CliException("signature verification of SHA256SUMS failed. Nothing was installed.");
        }

        Console.WriteLine("Release signature verified.");
    }

    private static async Task DownloadRequiredAsync(HttpClient http, string url, string target)
    {
        try
        {
            await DownloadAsync(http, url, target);
        }
        catch (CliException ex)
        {
            throw new CliException($"the release signature {Path.GetFileName(target)} is missing or unavailable ({ex.Message}). Nothing was installed.");
        }
    }

    /// <summary>Extra transparency check when cosign is installed; the pinned-key signature is the required one.</summary>
    private static async Task VerifyCosignAsync(HttpClient http, ReleaseFeed feed, string version, string sums, string work)
    {
        if (ServiceCommands.RunCapture("cosign", ["version"]).ExitCode != 0)
        {
            return;
        }

        var signature = Path.Combine(work, "SHA256SUMS.sig");
        var certificate = Path.Combine(work, "SHA256SUMS.pem");
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.sig"), signature);
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.pem"), certificate);
        var result = ServiceCommands.RunCapture("cosign",
        [
            "verify-blob", "--signature", signature, "--certificate", certificate,
            "--certificate-identity-regexp", feed.Trust.CosignIdentityPattern,
            "--certificate-oidc-issuer", UpdateTrust.CosignIssuer, sums
        ]);
        if (result.ExitCode != 0)
        {
            throw new CliException("cosign verification of SHA256SUMS failed (identity must be the release workflow on a version tag). Nothing was installed.");
        }

        Console.WriteLine("Cosign signature verified.");
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
}
