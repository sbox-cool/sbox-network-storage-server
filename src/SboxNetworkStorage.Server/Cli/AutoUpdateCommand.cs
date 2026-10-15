using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// <c>sbox-ns update --auto [--all-instances]</c>, run by the <c>sbox-ns-update</c> timer, and
/// <c>sbox-ns rollback --all-instances</c>.
/// </summary>
/// <remarks>
/// Exit codes of <c>update --auto</c>: 0 installed and healthy, or nothing to do (the reason is
/// printed); 1 the update failed and every instance was rolled back (or it failed before anything
/// changed); 2 invalid configuration or instances that disagree on channel/window; 3 the update
/// failed and the rollback did not complete (manual intervention needed); 4 the release feed was
/// unreachable or invalid (nothing changed).
/// </remarks>
public static class AutoUpdateCommand
{
    public const int RolledBack = CliApp.Failure;
    public const int RollbackFailed = 3;
    public const int FeedUnavailable = 4;

    /// <summary>Install-script override for hosts without openssl; never honored by unattended updates.</summary>
    public const string InsecureSkipSignatureVariable = "SBOX_NS_INSECURE_SKIP_SIGNATURE";

    public static async Task<int> RunAsync(CliContext context)
    {
        if (context.Args.Flag("allow-downgrade"))
        {
            throw new CliException("unattended updates never downgrade; use `sbox-ns update --version X.Y.Z --allow-downgrade` instead.", CliApp.Usage);
        }

        if (Environment.GetEnvironmentVariable(InsecureSkipSignatureVariable) == "1")
        {
            throw new CliException($"{InsecureSkipSignatureVariable} is set; unattended updates never skip signature verification. Nothing was changed.", CliApp.Usage);
        }

        var state = UpdateState.ForHost();
        var instances = ResolveInstances(context);
        if (OperatingSystem.IsLinux())
        {
            // Hosts updated by an older updater still keep runtime files in the config folder; move them before anything else.
            var processPath = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
            var migrator = new LayoutMigrator(HostLayoutSystem.Instance, new SystemUpdateHost(), state, processPath, Console.WriteLine);
            if (await LayoutCommands.MigrateWhenNeededAsync(instances, migrator) is { } stop)
            {
                return stop;
            }

            instances = ResolveInstances(context);
        }

        AutoUpdateSettings settings;
        try
        {
            settings = AutoUpdatePolicy.Combine(instances.Select(i => (i.Name, AutoUpdateSettings.FromConfig(i.Config))).ToList());
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ex.Message, CliApp.Usage);
        }

        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"sbox-ns {BuildInfo.Version}; channel {settings.Channel}; window {settings.Window}; instances: {string.Join(", ", instances.Select(i => i.Name))}");
        if (await AutoRollbackIfRegressedAsync(instances, state, now) is { } rolledBack)
        {
            return rolledBack;
        }

        var schedule = AutoUpdatePolicy.EvaluateSchedule(settings, now);
        if (!schedule.Install)
        {
            Console.WriteLine($"Nothing to do: {schedule.Reason}.");
            return CliApp.Ok;
        }

        using var http = ReleaseFeed.CreateHttpClient();
        var feed = new ReleaseFeed(http, instances[0].Config);
        ReleaseInfo release;
        try
        {
            release = await feed.GetFromFeedAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidDataException)
        {
            // Never fall back to GitHub here: that would bypass channel promotion and holds.
            Console.Error.WriteLine($"error: release feed {feed.FeedUrl} is unavailable ({ex.Message}); nothing was changed.");
            return FeedUnavailable;
        }

        var failedVersion = instances.Select(i => state.Read(i.Name))
            .FirstOrDefault(r => r?.IsFailed == true && r.ToVersion == release.Version)?.ToVersion;
        var decision = AutoUpdatePolicy.EvaluateRelease(settings, BuildInfo.Version, release, failedVersion, now);
        if (!decision.Install)
        {
            Console.WriteLine($"Nothing to do: {decision.Reason}.");
            return CliApp.Ok;
        }

        Console.WriteLine($"Updating: {decision.Reason}.");
        if (UpdateCommands.IsDowngrade(BuildInfo.Version, release.Version))
        {
            // Defense in depth: the release policy already refuses anything that is not newer,
            // but an unattended run must never install a target it cannot prove is an upgrade.
            Console.WriteLine($"Nothing to do: {release.Version} is not newer than {BuildInfo.Version}; unattended updates never downgrade.");
            return CliApp.Ok;
        }

        UpdateCommands.RequireUpgradable(release);
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        UpdateCommands.RequireStandaloneExecutable(binary);
        using var updateLock = UpdateCommands.AcquireUpdateLock(binary);
        var review = await UpdatePreflightCommand.ReviewAsync(http, feed, release.Version, new SystemUpdateHost(), binary, instances);
        if (review?.Blocked == true)
        {
            // Held, not skipped: the next run re-checks, so new evidence or a changed library mix takes effect without a marker to clear.
            Console.WriteLine($"Holding {release.Version}: known to break {string.Join(", ", review.Failed)}. Nothing was changed.");
            return CliApp.Ok;
        }
        if (review is { Untested.Count: > 0 })
        {
            Console.WriteLine($"warning: no compatibility evidence for {string.Join(", ", review.Untested)}.");
        }
        var work = Directory.CreateTempSubdirectory("sbox-ns-update-");
        try
        {
            var staged = await UpdateCommands.StageReleaseAsync(http, feed, release.Version, binary, work.FullName);
            using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var updater = new AutoUpdater(new SystemUpdateHost(), state, health, Console.WriteLine);
            var result = await updater.InstallAsync(BuildInfo.Version, release.Version, staged, binary, instances, "auto", CancellationToken.None);
            switch (result.Outcome)
            {
                case InstallOutcome.Succeeded:
                    Console.WriteLine($"Updated sbox-ns {BuildInfo.Version} -> {release.Version} on {instances.Count} instance(s). Undo with: sbox-ns rollback{(instances.Count > 1 ? " --all-instances" : string.Empty)}");
                    return CliApp.Ok;
                case InstallOutcome.RolledBack:
                    Console.Error.WriteLine($"error: update to {release.Version} failed and was rolled back: {result.Reason}");
                    return RolledBack;
                default:
                    Console.Error.WriteLine($"error: update to {release.Version} failed and the ROLLBACK DID NOT COMPLETE: {result.Reason}");
                    Console.Error.WriteLine($"Check `sbox-ns doctor` and the service logs; backups are listed in each instance's last-update.json under {state.Root}.");
                    return RollbackFailed;
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Rolls back an unattended update when the census shows a per-library regression on it. Runs before the schedule, so
    /// a regression is never waited out. Null means nothing was rolled back and the run continues normally.
    /// </summary>
    private static async Task<int?> AutoRollbackIfRegressedAsync(IReadOnlyList<ServerInstance> instances, UpdateState state, DateTimeOffset now)
    {
        var records = new List<(ServerInstance Instance, UpdateRecord Record)>();
        foreach (var instance in instances)
        {
            var record = state.Read(instance.Name);
            if (record is null || record.Status != UpdateRecord.Succeeded || record.Mode != "auto"
                || record.ToVersion != BuildInfo.Version || now - record.UpdatedAt > TimeSpan.FromHours(PostUpdateVerifier.WatchWindowHours))
            {
                return null;
            }

            records.Add((instance, record));
        }

        if (records.Count == 0) return null;

        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        var host = new SystemUpdateHost();
        var regressions = new List<string>();
        foreach (var (instance, _) in records)
        {
            var verdict = await host.ReadUpdateVerdictAsync(binary, instance, PostUpdateVerifier.DefaultWindowHours, CancellationToken.None);
            if (verdict is null)
            {
                Console.Error.WriteLine($"warning: no post-update verdict for instance {instance.Name}; treating it as no evidence.");
                continue;
            }

            foreach (var library in verdict.Libraries.Where(library => library.Outcome == LibraryOutcome.Regressed))
            {
                regressions.Add($"{instance.Name}: {library.Library} error rate {Percent(library.PostErrorRate)} (was {Percent(library.BaselineErrorRate)} on {verdict.BaselineVersion})");
            }
        }

        if (regressions.Count == 0) return null;

        var reason = $"post-update regression on {BuildInfo.Version}: {string.Join("; ", regressions)}";
        Console.Error.WriteLine($"warning: {reason}");
        if (!instances.All(i => i.Config.GetBoolean("updates.auto_rollback")))
        {
            Console.Error.WriteLine("updates.auto_rollback is off; not rolling back. Roll back with `sbox-ns rollback` once you have confirmed the regression.");
            return null;
        }

        if (!File.Exists(state.PreviousBinaryPath))
        {
            Console.Error.WriteLine($"warning: cannot roll back automatically; the rollback binary is missing ({state.PreviousBinaryPath}).");
            return null;
        }

        var decision = await UpdateCommands.CheckDataPreservingAsync(host, binary, state.PreviousBinaryPath, instances);
        if (!decision.Safe)
        {
            Console.Error.WriteLine($"warning: cannot roll back automatically without losing data: {decision.Reason}. Roll back with `sbox-ns rollback` when ready.");
            return null;
        }

        UpdateCommands.RequireStandaloneExecutable(binary);
        using var updateLock = UpdateCommands.AcquireUpdateLock(binary);
        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var problems = await new AutoUpdater(host, state, health, Console.WriteLine).RollbackRecordedAsync(records, binary, CancellationToken.None, keepData: true);
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"error: {problem}");
            }

            Console.Error.WriteLine("error: automatic rollback did not complete; check `sbox-ns doctor` and the service logs.");
            return RollbackFailed;
        }

        // Hold the bad release: the failed record makes the release policy skip this version until a newer one ships.
        foreach (var (instance, record) in records)
        {
            state.Write(instance.Name, new UpdateRecord(record.FromVersion, record.ToVersion, record.BinaryPath, record.PreviousBinaryPath,
                record.BackupPath, DateTimeOffset.UtcNow, UpdateRecord.Failed, reason, "auto"));
        }

        Console.WriteLine($"Rolled back to {records[0].Record.FromVersion}; no data was lost. {BuildInfo.Version} is held until a newer release is published.");
        return RolledBack;
    }

    private static string Percent(decimal rate) => $"{rate * 100:0.0}%";

    public static async Task<int> RollbackAllAsync(CliContext context)
    {
        var state = UpdateState.ForHost();
        state.Require();
        var instances = ResolveInstances(context);
        var recorded = instances.Select(i => (Instance: i, Record: state.Read(i.Name))).ToList();
        var missing = recorded.Where(r => r.Record is null).Select(r => r.Instance.Name).ToList();
        if (missing.Count > 0)
        {
            throw new CliException($"no recorded update to roll back for: {string.Join(", ", missing)}");
        }

        var records = recorded.Select(r => (r.Instance, Record: r.Record!)).ToList();
        foreach (var (_, record) in records)
        {
            UpdateCommands.RequireRollbackable(record);
        }

        var first = records[0].Record;
        if (records.Any(r => r.Record.ToVersion != first.ToVersion))
        {
            throw new CliException("instances recorded different updates; roll them back one at a time with --config-dir/--data-dir");
        }

        // Rollback restores the fixed installed binary from the root-only state folder; paths in the records are never followed.
        var keepData = context.Args.Flag("keep-data");
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        UpdateCommands.RequireStandaloneExecutable(binary);
        if (!File.Exists(state.PreviousBinaryPath))
        {
            throw new CliException($"rollback binary is missing ({state.PreviousBinaryPath})");
        }

        if (!keepData)
        {
            foreach (var (instance, record) in records.Where(r => r.Record.BackupPath is not null && !File.Exists(r.Record.BackupPath)))
            {
                throw new CliException($"{instance.Name}: backup {record.BackupPath} is missing");
            }
        }

        var host = new SystemUpdateHost();
        if (keepData)
        {
            // Every instance shares the one installed binary, so one database that needs the restore path stops them all.
            await UpdateCommands.RequireDataPreservingRollbackAsync(host, binary, state.PreviousBinaryPath, records.Select(r => r.Instance).ToList(), UpdateCommands.RestoreAlternative);
            Console.WriteLine($"Rolling back {first.ToVersion} -> {first.FromVersion} on {string.Join(", ", records.Select(r => r.Instance.Name))}; databases are kept as they are.");
        }
        else
        {
            Console.WriteLine($"Rolling back {first.ToVersion} -> {first.FromVersion} on {string.Join(", ", records.Select(r => r.Instance.Name))}");
            UpdateCommands.ConfirmDataLoss(context);
        }

        using var updateLock = UpdateCommands.AcquireUpdateLock(binary);
        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var problems = await new AutoUpdater(host, state, health, Console.WriteLine).RollbackRecordedAsync(records, binary, CancellationToken.None, keepData);
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"error: rollback incomplete: {string.Join("; ", problems)}");
            return RollbackFailed;
        }

        Console.WriteLine(keepData ? $"Rolled back to {first.FromVersion}; no data was lost." : $"Rolled back to {first.FromVersion}.");
        return CliApp.Ok;
    }

    /// <summary>Every instance on this host with <c>--all-instances</c>, otherwise the one selected by <c>--config-dir</c>/<c>--data-dir</c>.</summary>
    private static IReadOnlyList<ServerInstance> ResolveInstances(CliContext context)
    {
        if (!context.Args.Flag("all-instances"))
        {
            var config = context.LoadValidConfig();
            var registered = OperatingSystem.IsLinux() ? ServerInstances.Enumerate() : [];
            if (registered.Any(i => !string.Equals(i.Config.ConfigDirectory, config.ConfigDirectory, StringComparison.Ordinal)))
            {
                throw new CliException("other instances share the installed binary; use --all-instances so every database is backed up and every service is restarted", CliApp.Usage);
            }

            return [registered.FirstOrDefault(i => string.Equals(i.Config.ConfigDirectory, config.ConfigDirectory, StringComparison.Ordinal))
                ?? new ServerInstance(ServerInstance.DefaultName, ServerInstances.DefaultUnit, config)];
        }

        var instances = ServerInstances.Enumerate();
        if (instances.Count == 0)
        {
            throw new CliException($"no instances found ({ConfigPaths.LinuxServiceConfigDir}/server.toml or {ConfigPaths.LinuxServiceConfigDir}/<name>/server.toml)", CliApp.Usage);
        }

        var invalid = instances.Where(i => !i.Config.IsValid).ToList();
        foreach (var instance in invalid)
        {
            foreach (var issue in instance.Config.Issues)
            {
                Console.Error.WriteLine($"  {instance.Name}: {issue}");
            }
        }

        return invalid.Count == 0
            ? instances
            : throw new CliException($"configuration of {string.Join(", ", invalid.Select(i => i.Name))} is invalid; nothing was changed", CliApp.Usage);
    }
}
