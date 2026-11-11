using System.IO.Compression;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Backups;

/// <summary>Another backup run holds the backup lock.</summary>
public sealed class BackupBusyException() : InvalidOperationException("Another backup is already running.");

/// <summary>Last run recorded in backup-state.json. A missing or damaged file reads as all-null ("never run").</summary>
public sealed record BackupRunState(DateTimeOffset? AttemptedAt, DateTimeOffset? SucceededAt, string? Outcome, string? Error, string? BackupFile);

/// <summary>
/// A run that wrote a local backup. An offsite failure is reported here rather than thrown, because the local copy
/// is kept and that is the part that must not be lost.
/// </summary>
public sealed record BackupRunResult(string LocalFile, int PrunedCount, RemoteBackup? Offsite, string? OffsiteError);

public static class BackupRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly BackupRunState NeverRun = new(null, null, null, null, null);

    /// <summary>
    /// Makes one automatic local backup, deletes automatic copies beyond backup.schedule.keep_local, then uploads the
    /// new copy when backup.sboxns.enabled is true. Holds the backup lock for the whole run.
    /// </summary>
    public static async Task<BackupRunResult> RunAsync(EffectiveConfig config, HttpMessageHandler? handler, CancellationToken ct)
    {
        using var scope = RuntimeIdentity.Enter(config);
        config.EnsureRuntimeDirectory();
        using var _ = AcquireLock(config);

        var attempted = DateTimeOffset.UtcNow;
        var previous = ReadState(config);
        try
        {
            var local = await DatabaseBackup.BackupAsync(config, DatabaseBackup.DefaultBackupPath(config, BackupFiles.AutoLabel), ct);
            var pruned = Prune(config, local);

            RemoteBackup? offsite = null;
            string? offsiteError = null;
            if (config.GetBoolean("backup.sboxns.enabled"))
            {
                try
                {
                    offsite = await UploadAsync(config, local, handler, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    offsiteError = DescribeError(ex);
                }
            }

            var outcome = offsiteError is null ? "ok" : "offsite-failed";
            Save(config, new BackupRunState(attempted, offsiteError is null ? attempted : previous.SucceededAt,
                outcome, offsiteError, Path.GetFileName(local)));
            return new BackupRunResult(local, pruned, offsite, offsiteError);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Save(config, new BackupRunState(attempted, previous.SucceededAt, "failed", DescribeError(ex), null));
            throw;
        }
    }

    /// <summary>Plain message for the run record and the CLI. Remote bodies and secrets are never included.</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        BackupApiException api => api.Message,
        HttpRequestException => "sboxns Backups could not be reached.",
        TaskCanceledException => "sboxns Backups did not respond in time.",
        JsonException => "sboxns Backups returned an unexpected response.",
        _ => ex.Message,
    };

    public static BackupRunState ReadState(EffectiveConfig config)
    {
        try
        {
            return JsonSerializer.Deserialize<BackupRunState>(File.ReadAllText(config.BackupStatePath), Json) ?? NeverRun;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return NeverRun;
        }
    }

    private static FileStream AcquireLock(EffectiveConfig config)
    {
        try
        {
            return new FileStream(Path.Combine(config.RuntimeDirectory, StateLayout.BackupLockFile),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new BackupBusyException();
        }
    }

    /// <summary>Keeps the newest keep_local automatic copies. The file just written always counts as one of them.</summary>
    private static int Prune(EffectiveConfig config, string newest)
    {
        var keep = (int)config.GetInteger("backup.schedule.keep_local");
        var newestFull = Path.GetFullPath(newest);
        var stale = BackupFiles.AutoBackups(config)
            .Where(f => f.FullName != newestFull)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep - 1)
            .ToList();
        foreach (var file in stale)
            file.Delete();
        return stale.Count;
    }

    /// <summary>Uploads a gzip of the backup. The compressed copy is temporary; the local backup stays as written.</summary>
    private static async Task<RemoteBackup> UploadAsync(EffectiveConfig config, string localFile, HttpMessageHandler? handler, CancellationToken ct)
    {
        var endpoint = UsageTelemetry.ValidateEndpoint(config.GetString("backup.sboxns.endpoint"))
            ?? throw new InvalidOperationException("backup.sboxns.endpoint must be an HTTPS URL.");
        var packed = localFile + ".gz";
        try
        {
            await using (var source = File.OpenRead(localFile))
            await using (var target = File.Create(packed))
            await using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
            {
                await source.CopyToAsync(gzip, ct);
            }

            var (size, sha256) = BackupFiles.Digest(packed);
            using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(config));
            using var client = new SboxNsBackupClient(endpoint, identity, handler);
            var ticket = await client.RequestUploadAsync(size, sha256, ct);
            await using (var body = File.OpenRead(packed))
            {
                await client.UploadAsync(ticket, body, size, ct);
            }
            return await client.CompleteAsync(ticket.BackupId, ct);
        }
        finally
        {
            File.Delete(packed);
        }
    }

    private static void Save(EffectiveConfig config, BackupRunState state)
        => ConfigFiles.WriteAtomically(config.BackupStatePath, JsonSerializer.Serialize(state, Json));
}

/// <summary>Shared helpers for backup files, IDs and digests.</summary>
public static class BackupFiles
{
    /// <summary>Label for automatic copies (schedule and backup now). Only these are rotated by keep_local.</summary>
    public const string AutoLabel = "auto";

    public static bool IsValidBackupId(string value)
        => value.Length is >= 16 and <= 40 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>Every local backup file written by <c>db backup</c> or the backup runner.</summary>
    public static IEnumerable<FileInfo> LocalBackups(EffectiveConfig config)
    {
        var directory = DatabaseBackup.BackupDirectory(config);
        if (!Directory.Exists(directory))
            return [];
        return new DirectoryInfo(directory).EnumerateFiles("sbox-ns-*")
            .Where(f => f.Extension is ".db" or ".dump");
    }

    public static IEnumerable<FileInfo> AutoBackups(EffectiveConfig config)
        => LocalBackups(config).Where(f => f.Name.StartsWith($"sbox-ns-{AutoLabel}-", StringComparison.Ordinal));

    public static (long SizeBytes, string Sha256) Digest(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }
}
