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

    public static async Task<int> RunAsync(CliContext context)
    {
        var instances = ResolveInstances(context);
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

        var failedVersion = instances.Select(i => UpdateRecord.Read(i.Config))
            .FirstOrDefault(r => r?.IsFailed == true && r.ToVersion == release.Version)?.ToVersion;
        var decision = AutoUpdatePolicy.EvaluateRelease(settings, BuildInfo.Version, release, failedVersion, now);
        if (!decision.Install)
        {
            Console.WriteLine($"Nothing to do: {decision.Reason}.");
            return CliApp.Ok;
        }

        Console.WriteLine($"Updating: {decision.Reason}.");
        UpdateCommands.RequireUpgradable(release);
        var binary = Environment.ProcessPath ?? throw new CliException("cannot determine the sbox-ns executable path");
        UpdateCommands.RequireStandaloneExecutable(binary);
        using var updateLock = UpdateCommands.AcquireUpdateLock(binary);
        var work = Directory.CreateTempSubdirectory("sbox-ns-update-");
        try
        {
            var staged = await UpdateCommands.StageReleaseAsync(http, feed, release.Version, binary, work.FullName);
            using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var updater = new AutoUpdater(new SystemUpdateHost(), health, Console.WriteLine);
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
                    Console.Error.WriteLine("Check `sbox-ns doctor` and the service logs; backups are listed in each instance's updates/last-update.json.");
                    return RollbackFailed;
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    public static async Task<int> RollbackAllAsync(CliContext context)
    {
        var instances = ResolveInstances(context);
        var recorded = instances.Select(i => (Instance: i, Record: UpdateRecord.Read(i.Config))).ToList();
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
        if (records.Any(r => r.Record.ToVersion != first.ToVersion || r.Record.PreviousBinaryPath != first.PreviousBinaryPath))
        {
            throw new CliException("instances recorded different updates; roll them back one at a time with --config-dir/--data-dir");
        }

        if (!File.Exists(first.PreviousBinaryPath))
        {
            throw new CliException($"rollback binary is missing ({first.PreviousBinaryPath})");
        }

        foreach (var (instance, record) in records.Where(r => r.Record.BackupPath is not null && !File.Exists(r.Record.BackupPath)))
        {
            throw new CliException($"{instance.Name}: backup {record.BackupPath} is missing");
        }

        Console.WriteLine($"Rolling back {first.ToVersion} -> {first.FromVersion} on {string.Join(", ", records.Select(r => r.Instance.Name))}");
        UpdateCommands.ConfirmDataLoss(context);
        using var updateLock = UpdateCommands.AcquireUpdateLock(first.BinaryPath);
        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var problems = await new AutoUpdater(new SystemUpdateHost(), health, Console.WriteLine).RollbackRecordedAsync(records, CancellationToken.None);
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"error: rollback incomplete: {string.Join("; ", problems)}");
            return RollbackFailed;
        }

        Console.WriteLine($"Rolled back to {first.FromVersion}.");
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
