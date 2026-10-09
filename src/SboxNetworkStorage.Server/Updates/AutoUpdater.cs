using System.Net;
using System.Text.Json;
using SboxNetworkStorage.Server.Operations;

namespace SboxNetworkStorage.Server.Updates;

public enum InstallOutcome
{
    Succeeded,
    RolledBack,
    RollbackFailed,
}

public sealed record InstallResult(InstallOutcome Outcome, string? Reason);

/// <summary>
/// Installs a verified binary for every instance that shares it: stop the running
/// instances, back up every database, swap the binary, migrate every database, start
/// the instances that were running and wait for each <c>/health</c> to report the new
/// version. Any failure restores the binary and every backup taken in this run, starts
/// the instances again and records <c>failed</c> in each instance's last-update.json.
/// A recorded update is undone the same way, after moving the runtime files back into the config
/// folders (<c>layout migrate --revert</c>) because the binary it restores may predate the state layout.
/// </summary>
public sealed class AutoUpdater(IUpdateHost host, UpdateState state, HttpClient health, Action<string> log)
{
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<InstallResult> InstallAsync(string fromVersion, string toVersion, string stagedBinary, string binary,
        IReadOnlyList<ServerInstance> instances, string mode, CancellationToken ct)
    {
        state.Ensure();
        var active = new List<ServerInstance>();
        foreach (var instance in instances)
        {
            if (await host.IsActiveAsync(instance.Unit, ct))
            {
                active.Add(instance);
            }
        }

        var previous = state.PreviousBinaryPath;
        var backups = new List<(ServerInstance Instance, string? Path)>();
        var replaced = false;
        try
        {
            foreach (var instance in active)
            {
                log($"Stopping {instance.Unit}");
                Require(await host.StopAsync(instance.Unit, ct), $"stop {instance.Unit}");
            }

            // Back up after stopping, so no accepted write can be lost by a restore.
            foreach (var instance in instances)
            {
                var backup = await BackupAsync(instance, toVersion, binary, ct);
                backups.Add((instance, backup));
                log(backup is null ? $"{instance.Name}: no database yet, nothing to back up" : $"{instance.Name}: database backed up to {backup}");
            }

            File.Copy(binary, previous, overwrite: true);
            replaced = true;
            BinarySwap.Replace(stagedBinary, binary);
            log($"Installed sbox-ns {toVersion} at {binary}");

            foreach (var instance in instances)
            {
                Require(await host.MigrateAsync(binary, instance, ct), $"migrate {instance.Name}");
            }

            foreach (var instance in active)
            {
                log($"Starting {instance.Unit}");
                Require(await host.StartAsync(instance.Unit, ct), $"start {instance.Unit}");
                await RequireHealthyAsync(instance, toVersion, ct);
                log($"{instance.Name}: healthy on {toVersion}");
            }

            foreach (var (instance, backup) in backups)
            {
                state.Write(instance.Name, new UpdateRecord(fromVersion, toVersion, binary, previous, backup,
                    DateTimeOffset.UtcNow, UpdateRecord.Succeeded, null, mode));
            }
        }
        catch (Exception ex)
        {
            var reason = ex is UpdateStepException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            log($"Update to {toVersion} failed: {reason}. Rolling back {instances.Count} instance(s).");
            var problems = await RollbackAsync(fromVersion, binary, previous, replaced, active, backups, revertLayout: false);
            var fullReason = problems.Count == 0 ? reason : $"{reason}; rollback incomplete: {string.Join("; ", problems)}";
            foreach (var instance in instances)
            {
                var backup = backups.FirstOrDefault(b => ReferenceEquals(b.Instance, instance)).Path;
                state.Write(instance.Name, new UpdateRecord(fromVersion, toVersion, binary, previous, backup,
                    DateTimeOffset.UtcNow, UpdateRecord.Failed, fullReason, mode));
            }

            return new InstallResult(problems.Count == 0 ? InstallOutcome.RolledBack : InstallOutcome.RollbackFailed, fullReason);
        }


        return new InstallResult(InstallOutcome.Succeeded, null);
    }

    /// <summary>
    /// Undoes a recorded successful update for every instance: restores the previous
    /// binary once and every instance's backup, then starts the instances that were running.
    /// Only the fixed <paramref name="binary"/> is replaced and only <see cref="UpdateState.PreviousBinaryPath"/>
    /// is used as the source; the paths stored in the records are never followed.
    /// </summary>
    public async Task<IReadOnlyList<string>> RollbackRecordedAsync(IReadOnlyList<(ServerInstance Instance, UpdateRecord Record)> recorded,
        string binary, CancellationToken ct)
    {
        state.Require();
        var first = recorded[0].Record;
        var active = new List<ServerInstance>();
        foreach (var (instance, _) in recorded)
        {
            if (await host.IsActiveAsync(instance.Unit, ct))
            {
                active.Add(instance);
            }
        }

        var problems = await RollbackAsync(first.FromVersion, binary, state.PreviousBinaryPath, binaryReplaced: true, active,
            recorded.Select(r => (r.Instance, r.Record.BackupPath)).ToList(), revertLayout: true);
        if (problems.Count == 0)
        {
            foreach (var (instance, _) in recorded)
            {
                state.Delete(instance.Name);
            }
        }

        return problems;
    }

    private async Task<List<string>> RollbackAsync(string fromVersion, string binary, string previous, bool binaryReplaced,
        IReadOnlyList<ServerInstance> active, IReadOnlyList<(ServerInstance Instance, string? Path)> backups, bool revertLayout)
    {
        var problems = new List<string>();
        async Task TryAsync(string step, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                problems.Add($"{step}: {ex.Message}");
                log($"Rollback step failed ({step}): {ex.Message}");
            }
        }

        foreach (var instance in active)
        {
            await TryAsync($"stop {instance.Unit}", async () => Require(await host.StopAsync(instance.Unit, CancellationToken.None), $"stop {instance.Unit}"));
        }

        // Never restore a database underneath a process that may still be writing it.
        // Leave the binary and backups intact for an operator if any stop failed.
        if (problems.Count > 0)
        {
            return problems;
        }

        // A binary older than the state layout expects its secrets in the config folder. Without them it would
        // generate new ones, so a failed revert stops here with the old binary still in place.
        if (revertLayout && binaryReplaced)
        {
            await TryAsync("revert layout", async () =>
                Require(await host.RevertLayoutAsync(binary, backups.Select(b => b.Instance).ToList(), CancellationToken.None), "revert layout"));
            if (problems.Count > 0)
            {
                return problems;
            }
        }

        if (binaryReplaced)
        {
            await TryAsync("restore binary", () =>
            {
                BinarySwap.Replace(previous, binary);
                log($"Restored sbox-ns {fromVersion} at {binary}");
                return Task.CompletedTask;
            });
        }

        foreach (var (instance, backup) in backups)
        {
            await TryAsync($"restore {instance.Name} database", async () =>
            {
                if (backup is not null)
                {
                    Require(await host.RestoreAsync(binary, instance, backup, CancellationToken.None), $"restore {instance.Name} database");
                    log($"{instance.Name}: database restored from {backup}");
                }
                else if (instance.Config.GetString("database.provider") == "sqlite")
                {
                    // The database did not exist before this run; remove the one the migration created.
                    var created = Hosting.StoreRegistration.SqlitePath(instance.Config);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    foreach (var file in new[] { created, created + "-wal", created + "-shm" })
                    {
                        File.Delete(file);
                    }
                }
            });
        }

        foreach (var instance in active)
        {
            await TryAsync($"start {instance.Unit}", async () =>
            {
                Require(await host.StartAsync(instance.Unit, CancellationToken.None), $"start {instance.Unit}");
                await RequireHealthyAsync(instance, fromVersion, CancellationToken.None);
                log($"{instance.Name}: healthy on {fromVersion} again");
            });
        }

        return problems;
    }

    private async Task<string?> BackupAsync(ServerInstance instance, string toVersion, string binary, CancellationToken ct)
    {
        var config = instance.Config;
        if (config.GetString("database.provider") == "sqlite" && !File.Exists(Hosting.StoreRegistration.SqlitePath(config)))
        {
            return null;
        }

        var target = DatabaseBackup.DefaultBackupPath(config, $"pre-{toVersion}");
        Require(await host.BackupAsync(binary, instance, target, ct), $"back up {instance.Name} database");
        return target;
    }

    /// <summary>Polls <c>/health</c> until it answers 200 with <paramref name="version"/>, or fails after <see cref="HealthTimeout"/>.</summary>
    private async Task RequireHealthyAsync(ServerInstance instance, string version, CancellationToken ct)
    {
        var url = instance.HealthUrl;
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        var last = "no response";
        while (true)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await health.GetAsync(url, attempt.Token);
                var body = await response.Content.ReadAsStringAsync(attempt.Token);
                string? reported = null;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    reported = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                }
                catch (JsonException)
                {
                }

                if (response.StatusCode == HttpStatusCode.OK && reported == version)
                {
                    return;
                }

                last = $"HTTP {(int)response.StatusCode}, version {reported ?? "unknown"}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex.Message;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new UpdateStepException($"{instance.Name} did not report healthy {version} at {url} within {HealthTimeout.TotalSeconds:F0} s ({last})");
            }

            await Task.Delay(HealthPollInterval, ct);
        }
    }

    private static void Require(int exitCode, string step)
    {
        if (exitCode != 0)
        {
            throw new UpdateStepException($"{step} exited with code {exitCode}");
        }
    }

    private sealed class UpdateStepException(string message) : Exception(message);
}
