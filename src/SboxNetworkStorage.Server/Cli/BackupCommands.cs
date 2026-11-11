using System.Text.Json;
using SboxNetworkStorage.Server.Backups;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns backup ...</c>: scheduled local backups and the opt-in sboxns Backups offsite copies.</summary>
public static class BackupCommands
{
    private const string UsageText = "Usage: sbox-ns backup status|now|list|download|delete|identity|sboxns enable|disable";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(CliContext context)
    {
        switch (context.Args.Positional(1))
        {
            case "status":
                return await StatusAsync(context);
            case "now":
                return await NowAsync(context);
            case "list":
                return await ListAsync(context);
            case "download":
                return await DownloadAsync(context);
            case "delete":
                return await DeleteAsync(context);
            case "identity":
                return IdentityCommand(context);
            case "sboxns":
                return await SboxNsAsync(context);
            default:
                throw new CliException(UsageText, CliApp.Usage);
        }
    }

    private static async Task<int> StatusAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        var remote = await Remote(config, client => client.StatusAsync(CancellationToken.None));
        var files = BackupFiles.LocalBackups(config).ToList();
        var local = new BackupLocalStatus(
            config.GetBoolean("backup.schedule.enabled"),
            config.GetInteger("backup.schedule.interval_hours"),
            config.GetInteger("backup.schedule.keep_local"),
            config.GetBoolean("backup.sboxns.enabled"),
            DatabaseBackup.BackupDirectory(config),
            files.Count,
            files.Count == 0 ? null : files.Max(f => f.LastWriteTimeUtc),
            BackupRunner.ReadState(config));

        if (context.Args.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { local, sboxns = remote }, Json));
            return CliApp.Ok;
        }

        Console.WriteLine($"Schedule: {(local.ScheduleEnabled ? $"every {local.IntervalHours} hour(s)" : "off")}, keep {local.KeepLocal} automatic local backup(s)");
        Console.WriteLine($"Offsite upload: {(local.OffsiteEnabled ? "on" : "off")}");
        Console.WriteLine($"Backup folder: {local.BackupDirectory}");
        Console.WriteLine($"Local backups: {local.LocalBackupCount}{(local.NewestLocalBackupAt is { } newest ? $", newest {newest:u}" : string.Empty)}");
        var last = local.LastRun;
        if (last.AttemptedAt is null)
            Console.WriteLine("Last run: none yet");
        else
            Console.WriteLine($"Last run: {last.Outcome} at {last.AttemptedAt:u}{(last.Error is null ? string.Empty : $" ({last.Error})")}");
        Console.WriteLine();
        Console.WriteLine($"sboxns Backups: {remote.Status}");
        Console.WriteLine($"  {remote.Message}");
        if (remote.Plan is { } plan)
            Console.WriteLine($"  Plan {plan.Label}: {remote.UsedBytes} of {plan.QuotaBytes} bytes used, {remote.BackupCount} backup(s)");
        return CliApp.Ok;
    }

    private static async Task<int> NowAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        BackupRunResult result;
        try
        {
            result = await BackupRunner.RunAsync(config, null, CancellationToken.None);
        }
        catch (BackupBusyException)
        {
            throw new CliException("Another backup is already running. Try again when it finishes.");
        }
        catch (Exception ex) when (ex is not CliException)
        {
            throw new CliException($"Backup failed: {BackupRunner.DescribeError(ex)}");
        }

        Console.WriteLine($"Local backup written: {result.LocalFile}");
        if (result.PrunedCount > 0)
            Console.WriteLine($"Removed {result.PrunedCount} older automatic backup(s) beyond backup.schedule.keep_local.");
        if (result.OffsiteError is { } error)
            throw new CliException($"Local backup kept at {result.LocalFile}, but the sboxns upload failed: {error}");
        if (result.Offsite is { } offsite)
            Console.WriteLine($"Uploaded offsite as {offsite.BackupId}.");
        else if (!config.GetBoolean("backup.sboxns.enabled"))
            Console.WriteLine("Offsite upload is off. Turn it on with: sbox-ns backup sboxns enable");
        return CliApp.Ok;
    }

    private static async Task<int> ListAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        var json = context.Args.Flag("json");
        if (context.Args.Flag("offsite"))
        {
            var backups = await Remote(config, client => client.ListAsync(CancellationToken.None));
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { backups }, Json));
            }
            else if (backups.Count == 0)
            {
                Console.WriteLine("No offsite backups yet.");
            }
            else
            {
                foreach (var backup in backups)
                    Console.WriteLine($"{backup.BackupId}  {backup.CreatedAt:u}  {backup.SizeBytes} bytes");
            }
            return CliApp.Ok;
        }

        var local = BackupFiles.LocalBackups(config)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new LocalBackupEntry(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { backups = local }, Json));
        }
        else if (local.Count == 0)
        {
            Console.WriteLine($"No local backups in {DatabaseBackup.BackupDirectory(config)}.");
        }
        else
        {
            foreach (var backup in local)
                Console.WriteLine($"{backup.Name}  {backup.ModifiedAt:u}  {backup.SizeBytes} bytes");
        }
        return CliApp.Ok;
    }

    private static async Task<int> DownloadAsync(CliContext context)
    {
        var id = context.RequirePositional(2, "backupId");
        if (!BackupFiles.IsValidBackupId(id))
            throw new CliException("Invalid backup ID. Run backup list --offsite.", CliApp.Usage);
        var config = context.LoadValidConfig();
        var extension = config.GetString("database.provider") == "postgres" ? "dump" : "db";
        var destination = Path.GetFullPath(context.Args.Option("output") ?? $"sbox-ns-backup-{id}.{extension}.gz");
        if (File.Exists(destination))
            throw new CliException($"{destination} already exists. Choose another file with --output.");

        var backups = await Remote(config, client => client.ListAsync(CancellationToken.None));
        var backup = backups.FirstOrDefault(b => b.BackupId == id)
            ?? throw new CliException("No offsite backup has that ID. Run backup list --offsite.");
        await RemoteVoid(config, client => client.DownloadAsync(backup, destination, CancellationToken.None));

        Console.WriteLine($"Downloaded {destination} (checksum verified).");
        Console.WriteLine($"Decompress with: gunzip -k {Path.GetFileName(destination)}");
        Console.WriteLine("Then restore with `sbox-ns db restore <file>` while the server is stopped.");
        return CliApp.Ok;
    }

    private static async Task<int> DeleteAsync(CliContext context)
    {
        var id = context.RequirePositional(2, "backupId");
        if (!BackupFiles.IsValidBackupId(id))
            throw new CliException("Invalid backup ID. Run backup list --offsite.", CliApp.Usage);
        var config = context.LoadValidConfig();
        await RemoteVoid(config, client => client.DeleteAsync(id, CancellationToken.None));
        Console.WriteLine($"Deleted offsite backup {id}.");
        return CliApp.Ok;
    }

    private static int IdentityCommand(CliContext context)
    {
        var config = context.LoadValidConfig();
        using var scope = RuntimeIdentity.Enter(config);
        using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(config));
        var info = new BackupIdentityInfo(identity.Name, Convert.ToBase64String(identity.PublicKeySpki));
        if (context.Args.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(info, Json));
        }
        else
        {
            Console.WriteLine($"Name: {info.Name}");
            Console.WriteLine($"Public key: {info.PublicKey}");
        }
        return CliApp.Ok;
    }

    private static async Task<int> SboxNsAsync(CliContext context)
    {
        var action = context.RequirePositional(2, "enable|disable");
        var config = context.LoadValidConfig();
        var definition = SettingDefinitions.Find("backup.sboxns.enabled")!;
        switch (action)
        {
            case "enable":
            {
                var status = await Remote(config, client => client.StatusAsync(CancellationToken.None));
                if (!status.Enabled)
                    throw new CliException(string.IsNullOrWhiteSpace(status.Message)
                        ? "sboxns Backups is not available for this install yet."
                        : status.Message);
                ConfigFiles.SetValue(config.ConfigDirectory, definition, true);
                Console.WriteLine("sboxns Backups enabled. Restart the server to apply: sbox-ns service restart");
                return CliApp.Ok;
            }
            case "disable":
                ConfigFiles.SetValue(config.ConfigDirectory, definition, false);
                Console.WriteLine("sboxns Backups disabled. Restart the server to apply: sbox-ns service restart");
                return CliApp.Ok;
            default:
                throw new CliException("Usage: sbox-ns backup sboxns enable|disable", CliApp.Usage);
        }
    }

    /// <summary>Runs one sboxns request with this install's identity. Remote text is mapped to fixed messages.</summary>
    private static async Task<T> Remote<T>(EffectiveConfig config, Func<SboxNsBackupClient, Task<T>> action)
    {
        var endpoint = UsageTelemetry.ValidateEndpoint(config.GetString("backup.sboxns.endpoint"))
            ?? throw new CliException("backup.sboxns.endpoint must be an HTTPS URL.");
        try
        {
            using var scope = RuntimeIdentity.Enter(config);
            using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(config));
            using var client = new SboxNsBackupClient(endpoint, identity);
            return await action(client);
        }
        catch (Exception ex) when (ex is BackupApiException or HttpRequestException or TaskCanceledException
                                       or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CliException(BackupRunner.DescribeError(ex));
        }
    }

    private static Task RemoteVoid(EffectiveConfig config, Func<SboxNsBackupClient, Task> action)
        => Remote<bool>(config, async client =>
        {
            await action(client);
            return true;
        });
}

public sealed record BackupLocalStatus(bool ScheduleEnabled, long IntervalHours, long KeepLocal, bool OffsiteEnabled,
    string BackupDirectory, int LocalBackupCount, DateTime? NewestLocalBackupAt, BackupRunState LastRun);

public sealed record LocalBackupEntry(string Name, long SizeBytes, DateTime ModifiedAt);

public sealed record BackupIdentityInfo(string Name, string PublicKey);
