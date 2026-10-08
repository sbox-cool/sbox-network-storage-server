using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Npgsql;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Consistent database backups: SQLite uses <c>VACUUM INTO</c> (safe while the
/// server runs); PostgreSQL uses <c>pg_dump</c>/<c>pg_restore</c> custom format
/// limited to the configured schema.
/// </summary>
public static class DatabaseBackup
{
    public static string DefaultBackupPath(EffectiveConfig config, string label = "backup")
    {
        var extension = config.GetString("database.provider") == "postgres" ? "dump" : "db";
        return Path.Combine(config.DataDirectory, "backups", $"sbox-ns-{label}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.{extension}");
    }

    public static async Task<string> BackupAsync(EffectiveConfig config, string? outputPath, CancellationToken ct)
    {
        var target = Path.GetFullPath(outputPath ?? DefaultBackupPath(config));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            throw new InvalidOperationException($"{target} already exists.");
        }

        if (config.GetString("database.provider") == "postgres")
        {
            await RunPgToolAsync(config, "pg_dump", ["--format=custom", $"--schema={config.GetString("database.postgres.schema")}", $"--file={target}"], ct);
        }
        else
        {
            var source = StoreRegistration.SqlitePath(config);
            if (!File.Exists(source))
            {
                throw new InvalidOperationException($"SQLite database {source} does not exist yet.");
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target";
            command.Parameters.AddWithValue("$target", target);
            await command.ExecuteNonQueryAsync(ct);
        }

        return target;
    }

    /// <summary>Restores a backup. The server must be stopped; SQLite keeps the replaced file as <c>.before-restore</c>.</summary>
    public static async Task RestoreAsync(EffectiveConfig config, string backupPath, CancellationToken ct)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"{source} does not exist.");
        }

        if (config.GetString("database.provider") == "postgres")
        {
            await RunPgToolAsync(config, "pg_restore", ["--clean", "--if-exists", "--no-owner", $"--schema={config.GetString("database.postgres.schema")}", source], ct);
            return;
        }

        var target = StoreRegistration.SqlitePath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        SqliteConnection.ClearAllPools();
        if (File.Exists(target))
        {
            File.Copy(target, target + ".before-restore", overwrite: true);
        }

        foreach (var sidecar in new[] { "-wal", "-shm" })
        {
            File.Delete(target + sidecar);
        }

        File.Copy(source, target, overwrite: true);
    }

    private static async Task RunPgToolAsync(EffectiveConfig config, string tool, IEnumerable<string> arguments, CancellationToken ct)
    {
        var options = StoreRegistration.BuildPostgresOptions(config);
        var builder = new NpgsqlConnectionStringBuilder(options.BuildConnectionString());
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add($"--host={builder.Host}");
        start.ArgumentList.Add($"--port={builder.Port}");
        start.ArgumentList.Add($"--username={builder.Username}");
        start.ArgumentList.Add($"--dbname={builder.Database}");
        start.ArgumentList.Add("--no-password");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(builder.Password))
        {
            start.Environment["PGPASSWORD"] = builder.Password;
        }

        start.Environment["PGSSLMODE"] = builder.SslMode.ToString().ToLowerInvariant() switch
        {
            "verifyca" => "verify-ca",
            "verifyfull" => "verify-full",
            var mode => mode
        };

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"{tool} was not found on PATH. Install the PostgreSQL client tools to back up or restore PostgreSQL.");
        }

        using (process)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{tool} failed (exit {process.ExitCode}): {stderr.Trim()}");
            }
        }
    }
}
