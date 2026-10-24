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
        if (context.Args.Positional(1) == "verify")
        {
            return await UpdateVerifyCommand.RunAsync(context);
        }
        if (context.Args.Positional(1) == "observed-libraries")
        {
            return await UpdatePreflightCommand.RunObservedAsync(context);
        }

        if (context.Args.Flag("auto"))
        {
            return await AutoUpdateCommand.RunAsync(context);
        }

        if (context.Args.Flag("allow-downgrade") && context.Args.Option("version") is null)
        {
            throw new CliException("--allow-downgrade needs --version X.Y.Z", CliApp.Usage);
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

        var downgrade = IsDowngrade(BuildInfo.Version, release.Version);
        if (downgrade && (requested is null || !context.Args.Flag("allow-downgrade")))
        {
            throw new CliException($"{release.Version} is not newer than the installed {BuildInfo.Version}. Downgrades need a pinned version and --allow-downgrade (--version X.Y.Z --allow-downgrade), and only go ahead when the older release can run on the current database without a restore (see docs/self-hosting.md).", CliApp.Usage);
        }

        RequireUpgradable(release);
        if (HasOtherInstances(config))
        {
            throw new CliException("other instances share the installed binary; manual update only handles one instance. Use `sbox-ns update --auto --all-instances` (or `sbox-ns rollback --all-instances`) so every database is backed up and every service is restarted", CliApp.Usage);
        }
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        RequireStandaloneExecutable(binary);
        using var updateLock = AcquireUpdateLock(binary);
        var state = UpdateState.ForHost();
        state.Ensure();
        var host = new SystemUpdateHost();
        var instance = ServerInstances.ForConfig(config);
        var review = await UpdatePreflightCommand.ReviewAsync(http, feed, release.Version, host, binary, [instance]);
        if (review?.Blocked == true && !context.Args.Flag("ignore-compat"))
        {
            throw new CliException($"{release.Version} is known to break: {string.Join(", ", review.Failed)}. Nothing was installed. Install it anyway with --ignore-compat.");
        }
        if (review is { Untested.Count: > 0 })
        {
            Console.WriteLine($"warning: no compatibility evidence for {string.Join(", ", review.Untested)}.");
        }
        var work = Directory.CreateTempSubdirectory("sbox-ns-update-");
        try
        {
            var newBinary = await StageReleaseAsync(http, feed, release.Version, binary, work.FullName);
            if (downgrade)
            {
                // Decided before the service stops or anything is backed up: an unsafe downgrade changes nothing.
                await RequireDataPreservingRollbackAsync(host, binary, newBinary, [instance],
                    $"To go back anyway, install {release.Version} on a restored database backup taken before the newer release (`sbox-ns db restore <backup>`); data written since that backup is lost.");
            }

            // Only this instance's unit: it runs the database being migrated and the binary every instance shares.
            var serviceActive = await host.IsActiveAsync(instance.Unit, CancellationToken.None);
            if (serviceActive)
            {
                await RequireServiceControlAsync(host, instance.Unit, "stop");
            }

            string? backup = null;
            var previous = state.PreviousBinaryPath;
            var replacementAttempted = false;
            try
            {
                if (downgrade)
                {
                    // Re-checked while stopped: a write accepted between the first check and the stop
                    // cannot have moved the schema, but the check is cheap and the swap has not happened yet.
                    var quiesced = await CheckDataPreservingAsync(host, binary, newBinary, [instance]);
                    if (!quiesced.Safe)
                    {
                        throw new CliException($"quiesced compatibility check failed: {quiesced.Reason}; nothing was installed or swapped.");
                    }
                }

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

                if (serviceActive)
                {
                    await RequireServiceControlAsync(host, instance.Unit, "start");
                }

                throw;
            }

            if (serviceActive)
            {
                await RequireServiceControlAsync(host, instance.Unit, "start");
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

        var keepData = context.Args.Flag("keep-data");
        var config = context.LoadValidConfig();
        if (HasOtherInstances(config))
        {
            throw new CliException("other instances share the installed binary; manual rollback only handles one instance. Use `sbox-ns rollback --all-instances` so every database is restored and every service is restarted", CliApp.Usage);
        }
        var state = UpdateState.ForHost();
        var instance = ServerInstances.ForConfig(config);
        var record = state.Read(instance.Name) ?? throw new CliException("no update to roll back (nothing recorded by `sbox-ns update`)");
        RequireRollbackable(record);
        state.Require();

        // The record only says what happened. Rollback restores the fixed installed binary from the
        // root-only state folder and never follows a path stored in the record.
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        RequireStandaloneExecutable(binary);
        if (!File.Exists(state.PreviousBinaryPath) || (!keepData && (record.BackupPath is null || !File.Exists(record.BackupPath))))
        {
            throw new CliException($"rollback files are missing ({state.PreviousBinaryPath}, {(keepData ? "backup not needed" : record.BackupPath ?? "no backup")})");
        }

        var host = new SystemUpdateHost();
        if (keepData)
        {
            await RequireDataPreservingRollbackAsync(host, binary, state.PreviousBinaryPath, [instance], RestoreAlternative);
            Console.WriteLine($"Rolling back {record.ToVersion} -> {record.FromVersion}; the database is kept as it is.");
        }
        else
        {
            Console.WriteLine($"Rolling back {record.ToVersion} -> {record.FromVersion} and restoring {record.BackupPath}");
            ConfirmDataLoss(context);
        }

        using var updateLock = AcquireUpdateLock(binary);

        // Only this instance's unit: it runs the database being rolled back and the binary every instance shares.
        var serviceActive = await host.IsActiveAsync(instance.Unit, CancellationToken.None);
        if (serviceActive)
        {
            await RequireServiceControlAsync(host, instance.Unit, "stop");
        }

        if (keepData)
        {
            // Re-checked while stopped, before the layout revert and the binary swap.
            var quiesced = await CheckDataPreservingAsync(host, binary, state.PreviousBinaryPath, [instance]);
            if (!quiesced.Safe)
            {
                if (serviceActive)
                {
                    await RequireServiceControlAsync(host, instance.Unit, "start");
                }

                throw new CliException($"quiesced compatibility check failed: {quiesced.Reason}; nothing was changed.");
            }
        }

        // The binary being restored may predate the state folder and expect its secrets in the config folder.
        await RevertLayoutForRollbackAsync(host, binary, instance, serviceActive);

        BinarySwap.Replace(state.PreviousBinaryPath, binary);
        if (!keepData)
        {
            RequireSuccess(await host.RestoreAsync(binary, instance, record.BackupPath!, CancellationToken.None), "database restore");
        }

        state.Delete(instance.Name);
        if (serviceActive)
        {
            await RequireServiceControlAsync(host, instance.Unit, "start");
        }

        Console.WriteLine(keepData ? $"Rolled back to {record.FromVersion}; no data was lost." : $"Rolled back to {record.FromVersion}.");
        return CliApp.Ok;
    }

    /// <summary>
    /// The <c>--keep-data</c> / <c>--allow-downgrade</c> gate: throws unless <paramref name="targetBinary"/> can run on every
    /// instance's current database. The floor rule decides and the target binary's own <c>compatibility</c> probe
    /// must agree (see <see cref="SchemaRollback.CheckDataPreservingAsync"/>); either side refusing changes nothing.
    /// <paramref name="alternative"/> names today's restore path in the refusal.
    /// </summary>
    internal static async Task RequireDataPreservingRollbackAsync(IUpdateHost host, string installedBinary, string targetBinary,
        IReadOnlyList<ServerInstance> instances, string alternative)
    {
        var decision = await CheckDataPreservingAsync(host, installedBinary, targetBinary, instances);
        if (!decision.Safe)
        {
            throw new CliException($"cannot do this without losing data: {decision.Reason}. {alternative}");
        }

        Console.WriteLine($"Data-preserving: {decision.Reason}.");
    }

    /// <summary>Same gate without throwing or printing, for the re-check while the services are stopped.</summary>
    internal static Task<RollbackDecision> CheckDataPreservingAsync(IUpdateHost host, string installedBinary, string targetBinary,
        IReadOnlyList<ServerInstance> instances)
        => SchemaRollback.CheckDataPreservingAsync(host, installedBinary, targetBinary, instances, CancellationToken.None);

    internal const string RestoreAlternative = "`sbox-ns rollback` (without --keep-data) restores the database backup taken before the update and loses everything written since.";

    /// <summary>
    /// Fail-closed downgrade detection: true unless <paramref name="target"/> is provably newer than
    /// <paramref name="installed"/>. An unparseable version on either side, an equal version
    /// (equal-but-different builds), or an older target all take the downgrade path: a pinned
    /// <c>--version</c> with <c>--allow-downgrade</c> plus the compatibility gate, and never an
    /// unattended install.
    /// </summary>
    internal static bool IsDowngrade(string installed, string target)
        => !SemanticVersion.TryParse(target, out var next)
            || !SemanticVersion.TryParse(installed, out var current)
            || next.CompareTo(current) <= 0;

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
    internal static async Task<string> StageReleaseAsync(HttpClient http, ReleaseFeed feed, string version, string binary, string work,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)>? runCapture = null)
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
        await VerifyCosignAsync(http, feed, version, sums, work, runCapture ?? ServiceCommands.RunCapture);

        var extracted = Path.Combine(work, "extracted");
        Extract(archive, extracted);
        var newBinary = Path.Combine(extracted, Path.GetFileName(binary));
        if (!File.Exists(newBinary))
        {
            throw new CliException($"{archiveName} does not contain {Path.GetFileName(binary)}");
        }

        return newBinary;
    }

    private static async Task RequireServiceControlAsync(IUpdateHost host, string unit, string action)
    {
        var exitCode = action == "start"
            ? await host.StartAsync(unit, CancellationToken.None)
            : await host.StopAsync(unit, CancellationToken.None);
        if (exitCode != 0)
        {
            throw new CliException($"could not {action} {unit} (exit {exitCode}); no further update steps were performed");
        }
    }

    /// <summary>Starts a unit that a failed rollback step stopped, when it was running before; returns what the operator should know.</summary>
    private static async Task<string> RestartUnchangedAsync(IUpdateHost host, string unit, bool wasActive)
    {
        if (!wasActive)
        {
            return $"{unit} was not running and stays stopped.";
        }

        var exitCode = await host.StartAsync(unit, CancellationToken.None);
        return exitCode == 0
            ? $"{unit} was started again on the installed binary."
            : $"{unit} could not be started again (exit {exitCode}); start it by hand.";
    }

    /// <summary>
    /// Moves the runtime files back into the config folder for the older binary. The installed binary is still the new one
    /// until the swap, so a revert that left the host unchanged starts the unit again instead of leaving it stopped.
    /// </summary>
    internal static async Task RevertLayoutForRollbackAsync(IUpdateHost host, string binary, ServerInstance instance, bool serviceActive)
    {
        int exitCode;
        try
        {
            exitCode = await host.RevertLayoutAsync(binary, [instance], CancellationToken.None);
        }
        catch (LayoutMigrationException ex) when (!ex.HostUnchanged)
        {
            throw new CliException($"cannot move the runtime files back into the config folder: {ex.Message}. The layout is partly reverted, so {instance.Unit} stays stopped; follow the recovery steps above, then run rollback again. The previous binary was not restored.");
        }
        catch (LayoutMigrationException ex)
        {
            var restart = await RestartUnchangedAsync(host, instance.Unit, serviceActive);
            throw new CliException($"cannot move the runtime files back into the config folder: {ex.Message}. Every change was undone. {restart} The previous binary was not restored.");
        }
        catch (CliException ex)
        {
            // Refused before the first change (for example not root, or an untrusted config folder): nothing moved.
            var restart = await RestartUnchangedAsync(host, instance.Unit, serviceActive);
            throw new CliException($"cannot move the runtime files back into the config folder: {ex.Message}. Nothing was changed. {restart} The previous binary was not restored.");
        }

        if (exitCode != 0)
        {
            throw new CliException($"layout revert exited with code {exitCode}; {instance.Unit} stays stopped and the previous binary was not restored");
        }
    }

    /// <summary>True when another registered instance shares the installed binary; manual update and rollback only handle one instance.</summary>
    internal static bool HasOtherInstances(EffectiveConfig config)
    {
        var registered = OperatingSystem.IsLinux() ? ServerInstances.Enumerate() : [];
        return HasOtherInstances(config, registered);
    }

    internal static bool HasOtherInstances(EffectiveConfig config, IReadOnlyList<ServerInstance> registered)
        => registered.Any(i => !string.Equals(i.Config.ConfigDirectory, config.ConfigDirectory, StringComparison.Ordinal));

    internal static async Task DownloadAsync(HttpClient http, string url, string target)
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

    internal static async Task DownloadRequiredAsync(HttpClient http, string url, string target)
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
    private static async Task VerifyCosignAsync(HttpClient http, ReleaseFeed feed, string version, string sums, string work,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> runCapture)
    {
        if (runCapture("cosign", ["version"]).ExitCode != 0)
        {
            return;
        }

        var signature = Path.Combine(work, "SHA256SUMS.sig");
        var certificate = Path.Combine(work, "SHA256SUMS.pem");
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.sig"), signature);
        await DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS.pem"), certificate);
        var result = runCapture("cosign",
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
