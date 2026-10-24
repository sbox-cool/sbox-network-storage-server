using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Npgsql;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// PostgreSQL stage of <c>db verify-backup</c>. Always lists the archive with <c>pg_restore --list</c> and checks it
/// holds this server's <c>schema_version</c> table. A restore rehearsal runs only when a scratch database is named, and
/// only after the scratch database is proven to be a different, empty database on the live server. The live database
/// is never written; the restore and any migration target the scratch database alone.
/// </summary>
internal static class PostgresBackupRehearsal
{
    private static readonly string[] ReservedDatabases = ["postgres", "template0", "template1"];

    public static async Task RunAsync(EffectiveConfig config, VerificationRun run, string? scratchDatabase, CancellationToken ct)
    {
        var options = StoreRegistration.BuildPostgresOptions(config);
        var live = new NpgsqlConnectionStringBuilder(options.BuildConnectionString());
        var liveDatabase = live.Database ?? live.Username ?? throw new InvalidOperationException("The configured database name is missing.");
        run.Secrets.Add(live.Password ?? string.Empty);
        run.LiveDatabase = options.RedactedTarget;
        options.ValidateSchema();
        run.SupportedSchemaVersion = PostgresMigrations.All.Max(m => m.Version);

        var listing = await RunToolAsync("pg_restore", ["--list", run.BackupPath], connection: null, database: null, ct);
        if (listing.ExitCode != 0)
        {
            run.Fail("inspect", $"pg_restore --list failed (exit {listing.ExitCode}): {listing.Stderr.Trim()}");
            return;
        }

        var tablePattern = new Regex(
            @"^\d+;\s\d+\s\d+\sTABLE\s" + Regex.Escape(options.Schema) + @"\sschema_version\s",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!tablePattern.IsMatch(listing.Stdout))
        {
            run.Fail("inspect", $"the archive has no {options.Schema}.schema_version table; it was not written for schema '{options.Schema}' of this server");
            return;
        }

        var entries = listing.Stdout.Split('\n').Count(line => line.Length > 0 && !line.StartsWith(';'));
        run.Pass("inspect", $"pg_restore --list read {entries} TOC entries; {options.Schema}.schema_version is present");
        run.Level = "postgres-archive-inspected";

        if (scratchDatabase is null)
        {
            run.Skip("restore-rehearsal", "no --scratch-database given: the archive listing shows it is readable, not that it restores. Not restore-tested.");
            return;
        }

        run.ScratchDatabase = scratchDatabase;
        var scratch = new NpgsqlConnectionStringBuilder(live.ConnectionString) { Database = scratchDatabase };
        try
        {
            ProveScratchIsolated(liveDatabase, scratchDatabase);
            await ProveServerSideIsolationAsync(live, liveDatabase, scratch, scratchDatabase, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("isolation", "refusing to restore into the scratch database: " + ex.Message);
            return;
        }

        run.Pass("isolation", $"live database '{liveDatabase}' reports its own name; scratch '{scratchDatabase}' reports its own name and holds no user objects; the restore targets only the scratch database");

        try
        {
            // pg_restore --schema selects schema contents, not the namespace entry.
            // Create it only in the independently verified, empty scratch database.
            await using (var connection = new NpgsqlConnection(scratch.ConnectionString))
            {
                await connection.OpenAsync(ct);
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{options.Schema}\"";
                await create.ExecuteNonQueryAsync(ct);
            }
            var restore = await RunToolAsync(
                "pg_restore",
                ["--single-transaction", "--exit-on-error", "--no-owner", $"--schema={options.Schema}", run.BackupPath],
                scratch,
                scratchDatabase,
                ct);

            if (restore.ExitCode != 0)
            {
                run.Fail("restore-rehearsal", $"pg_restore into scratch database '{scratchDatabase}' failed (exit {restore.ExitCode}): {restore.Stderr.Trim()}");
                return;
            }

            run.Level = "postgres-restore-rehearsal";
            run.Pass("restore-rehearsal", $"archive restored into scratch database '{scratchDatabase}', schema {options.Schema}");
            var scratchOptions = options with { ConnectionString = scratch.ConnectionString };
            await using var store = new PostgresNetworkStorageStore(scratchOptions);
            await BackupVerifier.RehearseStoreAsync((INetworkStorageStoreAdmin)store, run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("restore-rehearsal", "rehearsal on the scratch database failed: " + ex.Message);
        }
        finally
        {
            await DropRestoredSchemaAsync(scratch, scratchDatabase, options.Schema, run);
        }
    }

    /// <summary>
    /// Name-level isolation, checked before any connection: the scratch name must be set, a valid identifier, and
    /// distinct from the live database and PostgreSQL's system databases. Throws <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static void ProveScratchIsolated(string liveDatabase, string? scratchDatabase)
    {
        if (string.IsNullOrWhiteSpace(scratchDatabase))
        {
            throw new InvalidOperationException("--scratch-database is empty");
        }

        if (scratchDatabase.Length > 63 || !(char.IsAsciiLetter(scratchDatabase[0]) || scratchDatabase[0] == '_')
            || scratchDatabase.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new InvalidOperationException("--scratch-database must use 1-63 ASCII letters, digits or underscores, not starting with a digit; connection strings and URLs are not database names");
        }

        if (string.Equals(scratchDatabase, liveDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{scratchDatabase}' is the configured live database; name a separate, disposable database");
        }

        if (ReservedDatabases.Contains(scratchDatabase, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{scratchDatabase}' is a PostgreSQL system database; name a separate, disposable database");
        }
    }

    /// <summary>
    /// Server-side isolation, checked before any write: the live database must be reachable and report its own name, and
    /// the scratch database must report the scratch name and hold no user objects (tables, views, types, functions, schemas).
    /// </summary>
    private static async Task ProveServerSideIsolationAsync(
        NpgsqlConnectionStringBuilder live,
        string liveDatabase,
        NpgsqlConnectionStringBuilder scratch,
        string scratchDatabase,
        CancellationToken ct)
    {
        await using (var liveConnection = new NpgsqlConnection(live.ConnectionString))
        {
            await liveConnection.OpenAsync(ct);
            var liveName = await CurrentDatabaseAsync(liveConnection, ct);
            if (liveName != liveDatabase)
            {
                throw new InvalidOperationException($"the configured database reports itself as '{liveName}', not '{liveDatabase}'");
            }
        }

        await using var scratchConnection = new NpgsqlConnection(scratch.ConnectionString);
        await scratchConnection.OpenAsync(ct);
        var scratchName = await CurrentDatabaseAsync(scratchConnection, ct);
        if (scratchName != scratchDatabase)
        {
            throw new InvalidOperationException($"the scratch connection reports database '{scratchName}', not '{scratchDatabase}'");
        }

        var objects = await UserObjectCountAsync(scratchConnection, ct);
        if (objects != 0)
        {
            throw new InvalidOperationException($"scratch database '{scratchDatabase}' already holds {objects} user object(s); use an empty, disposable database");
        }
    }

    /// <summary>
    /// Drops the schema the rehearsal restored and proves the scratch database is empty again. Runs on its own timeout, not
    /// the caller's token, so a cancelled verification still cleans up.
    /// </summary>
    private static async Task DropRestoredSchemaAsync(NpgsqlConnectionStringBuilder scratch, string scratchDatabase, string schema, VerificationRun run)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await using var connection = new NpgsqlConnection(scratch.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                await drop.ExecuteNonQueryAsync(timeout.Token);
            }

            var remaining = await UserObjectCountAsync(connection, timeout.Token);
            if (remaining != 0)
            {
                throw new InvalidOperationException($"{remaining} user object(s) remain");
            }

            run.Pass("cleanup", $"dropped schema {schema} from scratch database '{scratchDatabase}'; it is empty again");
        }
        catch (Exception ex)
        {
            run.Fail("cleanup", $"could not clean up scratch database '{scratchDatabase}'; drop schema {schema} manually: {VerificationRun.Redact(ex.Message, run.Secrets)}");
        }
    }

    private static async Task<string> CurrentDatabaseAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Counts tables, views, sequences, types, functions and non-system schemas outside pg_catalog and information_schema.</summary>
    private static async Task<long> UserObjectCountAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%')
            + (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
                   AND t.typtype IN ('e', 'd', 'r', 'm') AND t.typrelid = 0)
            + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%')
            + (SELECT count(*) FROM pg_namespace WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'public') AND nspname NOT LIKE 'pg\_%')
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ToolResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Runs a PostgreSQL client tool. The password travels in PGPASSWORD, never on the command line. With a connection,
    /// the tool is pointed at <paramref name="database"/>; without one (archive listing) it reads only the file.
    /// </summary>
    private static async Task<ToolResult> RunToolAsync(
        string tool,
        IEnumerable<string> arguments,
        NpgsqlConnectionStringBuilder? connection,
        string? database,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (connection is not null)
        {
            start.ArgumentList.Add($"--host={connection.Host}");
            start.ArgumentList.Add($"--port={connection.Port}");
            start.ArgumentList.Add($"--username={connection.Username}");
            start.ArgumentList.Add($"--dbname={database}");
            start.ArgumentList.Add("--no-password");
            if (!string.IsNullOrEmpty(connection.Password))
            {
                start.Environment["PGPASSWORD"] = connection.Password;
            }

            start.Environment["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant() switch
            {
                "verifyca" => "verify-ca",
                "verifyfull" => "verify-full",
                var mode => mode,
            };
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException($"{tool} was not found on PATH. Install the PostgreSQL client tools to verify PostgreSQL backups.");
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var killOnCancel = ct.Register(() => KillTree(process));
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }

            return new ToolResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
