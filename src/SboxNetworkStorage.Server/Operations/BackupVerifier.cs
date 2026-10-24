using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// <c>sbox-ns db verify-backup</c>: checks a native database backup (written by <c>db backup</c>) without touching the
/// live database. SQLite backups are copied into a throwaway directory, integrity-checked, opened as a store, migrated
/// on the copy when older, and read. PostgreSQL archives are listed with <c>pg_restore --list</c>; a restore rehearsal
/// into an explicitly named scratch database runs only when one is given (see <see cref="PostgresBackupRehearsal"/>).
/// Scope: database content only. Media blobs, config files and key material are not part of a database backup.
/// </summary>
public static class BackupVerifier
{
    /// <summary>What a verdict does not cover; printed with every report.</summary>
    public const string Scope = "database-only backup: media blobs, config files, and signing/secret key material are not covered; verify those backups separately";

    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private static readonly byte[] PostgresCustomHeader = "PGDMP"u8.ToArray();

    /// <summary>
    /// Verifies <paramref name="backupPath"/> against the configured provider. Never throws for a bad backup: every
    /// failure becomes a failed step in the returned report. Only cancellation escapes as a report too (step "cancelled").
    /// </summary>
    public static async Task<BackupVerificationReport> VerifyAsync(EffectiveConfig config, string backupPath, string? scratchDatabase, CancellationToken ct)
    {
        var configured = config.GetString("database.provider") == "postgres" ? "postgres" : "sqlite";
        var run = new VerificationRun(configured, Path.GetFullPath(backupPath));
        var clock = Stopwatch.StartNew();
        try
        {
            await VerifyCoreAsync(config, run, scratchDatabase, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Fail("cancelled", "verification was cancelled; the backup has not been verified");
        }
        catch (Exception ex)
        {
            run.Fail("verify", VerificationRun.Redact(ex.Message, run.Secrets));
        }

        clock.Stop();
        return run.ToReport(clock.ElapsedMilliseconds);
    }

    /// <summary>Detects the native format of a backup file from its header: <c>sqlite</c>, <c>postgres</c> (pg_dump custom), or <c>unknown</c>.</summary>
    internal static string DetectProvider(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[SqliteHeader.Length];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == SqliteHeader.Length && header.AsSpan().SequenceEqual(SqliteHeader)) return "sqlite";
        if (read >= PostgresCustomHeader.Length && header.AsSpan(0, PostgresCustomHeader.Length).SequenceEqual(PostgresCustomHeader)) return "postgres";
        return "unknown";
    }

    /// <summary>
    /// Schema compatibility, then the forward migration a restored server would run at startup (on the rehearsal copy
    /// only), then the representative reads: the aggregate counts the usage job reads from the projects and player tables.
    /// Marks the run rehearsed only when every stage passes.
    /// </summary>
    internal static async Task RehearseStoreAsync(INetworkStorageStoreAdmin admin, VerificationRun run, CancellationToken ct)
    {
        var supported = admin.SupportedSchemaVersion;
        run.SupportedSchemaVersion = supported;
        var state = await admin.GetSchemaStateAsync(ct);
        run.SchemaVersion = state.Version;
        run.MinBinarySchema = state.MinBinarySchema;
        if (state.Version == 0)
        {
            run.Fail("schema", "no schema version is recorded: this is not an sbox-ns database, or it was never migrated");
            return;
        }

        if (state.IsTooNewFor(supported))
        {
            run.Fail("schema", $"schema version {state.Version} needs a binary of at least {state.MinBinarySchema}; this build supports {supported}");
            return;
        }

        run.Pass("schema", $"schema version {state.Version}, compatibility floor {state.MinBinarySchema}, this build supports {supported}");

        if (state.Version < supported)
        {
            try
            {
                var migrated = await admin.MigrateAsync(ct);
                if (migrated.ToVersion != supported)
                {
                    run.Fail("migrate-copy", $"migration on the copy stopped at version {migrated.ToVersion}, not {supported}");
                    return;
                }

                run.Pass("migrate-copy", $"a restored server would migrate this copy {migrated.FromVersion} -> {migrated.ToVersion}; applied to the rehearsal copy only");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Fail("migrate-copy", VerificationRun.Redact(ex.Message, run.Secrets));
                return;
            }
        }

        try
        {
            var usage = await admin.CountUsageAsync(0, ct);
            run.Pass("reads", $"projects {usage.Projects}, players {usage.Players}, active players {usage.ActivePlayers}");
            run.Rehearsed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail("reads", VerificationRun.Redact(ex.Message, run.Secrets));
        }
    }

    private static async Task VerifyCoreAsync(EffectiveConfig config, VerificationRun run, string? scratchDatabase, CancellationToken ct)
    {
        if (!File.Exists(run.BackupPath))
        {
            run.Fail("file", $"{run.BackupPath} does not exist or is not a file");
            return;
        }

        run.Provider = DetectProvider(run.BackupPath);
        if (run.Provider == "unknown")
        {
            run.Fail("format", "not a SQLite database and not a PostgreSQL custom-format (pg_dump -Fc) archive; the file is empty, truncated, or not a database backup");
            return;
        }

        if (run.Provider != run.ConfiguredProvider)
        {
            run.Fail("provider", $"the file is a {run.Provider} backup but this server is configured for {run.ConfiguredProvider}; it cannot be restored here");
            return;
        }

        run.Pass("format", $"{run.Provider} backup");
        run.SourceSha256 = await Sha256Async(run.BackupPath, ct);

        if (run.Provider == "sqlite")
        {
            if (scratchDatabase is not null)
            {
                run.Fail("arguments", "--scratch-database applies only to PostgreSQL archives; SQLite verification always runs on a disposable copy");
            }
            else
            {
                await RehearseSqliteAsync(config, run, ct);
            }
        }
        else
        {
            await PostgresBackupRehearsal.RunAsync(config, run, scratchDatabase, ct);
        }

        var after = await Sha256Async(run.BackupPath, ct);
        if (after == run.SourceSha256)
        {
            run.Pass("source-unchanged", "the backup file is byte-identical before and after verification");
        }
        else
        {
            run.Fail("source-unchanged", "the backup file changed while it was being verified");
        }
    }

    private static async Task RehearseSqliteAsync(EffectiveConfig config, VerificationRun run, CancellationToken ct)
    {
        var live = Path.GetFullPath(StoreRegistration.SqlitePath(config));
        run.LiveDatabase = live;
        if (string.Equals(run.BackupPath, live, StringComparison.OrdinalIgnoreCase))
        {
            run.Fail("live-isolation", "the backup path is the live database file; verification refuses to read it in place");
            return;
        }

        run.Level = "sqlite-copy-rehearsal";
        var scratchDirectory = Directory.CreateTempSubdirectory("sbox-ns-verify-backup-").FullName;
        try
        {
            var copy = Path.Combine(scratchDirectory, "rehearsal.db");
            File.Copy(run.BackupPath, copy, overwrite: false);
            run.Pass("copy", "backup copied into a throwaway directory; the backup and the live database are never opened in place");
            await CheckSqliteIntegrityAsync(copy, run, ct);
            if (run.HasFailure)
            {
                return;
            }

            await using var store = new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = copy });
            await RehearseStoreAsync((INetworkStorageStoreAdmin)store, run, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Fail("copy", $"could not copy the backup into a throwaway directory: {ex.Message}");
        }
        finally
        {
            DeleteScratchDirectory(run, scratchDirectory);
        }
    }

    private static async Task CheckSqliteIntegrityAsync(string copy, VerificationRun run, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(0));
            }

            if (rows.Count == 1 && rows[0] == "ok")
            {
                run.Pass("integrity", "PRAGMA integrity_check: ok");
            }
            else
            {
                run.Fail("integrity", "PRAGMA integrity_check: " + string.Join("; ", rows.Take(3)));
            }
        }
        catch (SqliteException ex)
        {
            run.Fail("integrity", $"SQLite cannot read the backup (truncated or corrupt): {ex.Message}");
        }
    }

    /// <summary>Removes the rehearsal directory. Records a failed "cleanup" step instead of throwing, so the report says what is left on disk.</summary>
    internal static void DeleteScratchDirectory(VerificationRun run, string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            run.Pass("cleanup", "throwaway rehearsal copy removed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Fail("cleanup", $"could not remove the throwaway directory {directory}; delete it manually: {ex.Message}");
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}

/// <summary>One stage of a verification and its outcome: <c>passed</c>, <c>failed</c>, or <c>skipped</c>.</summary>
public sealed record VerificationStep(string Name, string Status, string Detail);

/// <summary>
/// The verdict of <c>db verify-backup</c>. <c>Result</c>: <c>verified</c> (restore rehearsed, every step passed),
/// <c>inspected</c> (archive read, no restore rehearsal; PostgreSQL without a scratch database), or <c>failed</c>.
/// </summary>
public sealed record BackupVerificationReport(
    string File,
    string ConfiguredProvider,
    string Provider,
    string Result,
    string VerificationLevel,
    bool RestoreTested,
    int? SchemaVersion,
    int? MinBinarySchema,
    int? SupportedSchemaVersion,
    string? ScratchDatabase,
    string LiveDatabase,
    string SourceSha256,
    long ElapsedMs,
    string Scope,
    IReadOnlyList<VerificationStep> Steps)
{
    public IReadOnlyList<string> Failures => Steps.Where(s => s.Status == "failed").Select(s => $"{s.Name}: {s.Detail}").ToList();
}

/// <summary>Mutable state of one verification, shared by the SQLite and PostgreSQL stages.</summary>
internal sealed class VerificationRun
{
    private readonly List<VerificationStep> _steps = [];

    public VerificationRun(string configuredProvider, string backupPath)
    {
        ConfiguredProvider = configuredProvider;
        BackupPath = backupPath;
    }

    public string ConfiguredProvider { get; }
    public string BackupPath { get; }
    public string Provider { get; set; } = "unknown";
    public string Level { get; set; } = "none";
    public bool Rehearsed { get; set; }
    public int? SchemaVersion { get; set; }
    public int? MinBinarySchema { get; set; }
    public int? SupportedSchemaVersion { get; set; }
    public string? ScratchDatabase { get; set; }
    public string LiveDatabase { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;

    /// <summary>Secret values that must never appear in a report (the live database password).</summary>
    public List<string> Secrets { get; } = [];

    public IReadOnlyList<VerificationStep> Steps => _steps;
    public bool HasFailure => _steps.Any(s => s.Status == "failed");

    public void Pass(string name, string detail) => _steps.Add(new VerificationStep(name, "passed", detail));
    public void Fail(string name, string detail) => _steps.Add(new VerificationStep(name, "failed", Redact(detail, Secrets)));
    public void Skip(string name, string detail) => _steps.Add(new VerificationStep(name, "skipped", detail));

    public BackupVerificationReport ToReport(long elapsedMs)
    {
        var result = HasFailure ? "failed" : Rehearsed ? "verified" : "inspected";
        return new BackupVerificationReport(
            File: BackupPath,
            ConfiguredProvider: ConfiguredProvider,
            Provider: Provider,
            Result: result,
            VerificationLevel: Level,
            RestoreTested: result == "verified",
            SchemaVersion: SchemaVersion,
            MinBinarySchema: MinBinarySchema,
            SupportedSchemaVersion: SupportedSchemaVersion,
            ScratchDatabase: ScratchDatabase,
            LiveDatabase: LiveDatabase,
            SourceSha256: SourceSha256,
            ElapsedMs: elapsedMs,
            Scope: BackupVerifier.Scope,
            Steps: _steps.ToList());
    }

    public static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }
}
