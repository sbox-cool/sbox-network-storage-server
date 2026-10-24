using System.Text.Json.Nodes;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

public static class DatabaseCommands
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "test|status|migrate|backup|restore|verify-backup");
        var config = context.LoadValidConfig();
        if (sub == "migrate" && context.Args.Flag("plan"))
        {
            var plan = await DatabaseMigrationPlan.ReadAsync(config, CancellationToken.None);
            if (context.Args.Flag("json")) Console.WriteLine(plan.ToJson().ToJsonString());
            else
            {
                Console.WriteLine($"Database: {plan.Provider}; {(plan.Initialized ? $"schema {plan.State.Version}" : "uninitialized (nothing created)")}");
                Console.WriteLine($"This binary supports schema {plan.Supported}; rollback floor {plan.State.MinBinarySchema} -> {plan.ProjectedFloor}.");
                foreach (var migration in plan.Pending)
                    Console.WriteLine($"  v{migration.Version}: {(migration.Compat == SchemaChange.Additive ? "additive" : "breaking")} — {migration.Description}");
                if (!plan.Compatible) Console.WriteLine("This database is newer than this binary can use; no migration is allowed.");
                else if (plan.Pending.Count == 0) Console.WriteLine("No pending migrations; the database will be left as is.");
                Console.WriteLine("Plan only: nothing changed. Take a backup before migrating. Additive does not guarantee zero downtime or business-level rollback.");
                Console.WriteLine("rollback --keep-data also requires the target binary to support the projected floor and pass its compatibility probe.");
            }
            return plan.Compatible ? CliApp.Ok : CliApp.Failure;
        }

        switch (sub)
        {
            case "backup":
            {
                var path = await DatabaseBackup.BackupAsync(config, context.Args.Option("output"), CancellationToken.None);
                Console.WriteLine($"Backup written to {path}");
                return CliApp.Ok;
            }
            case "restore":
            {
                var path = context.RequirePositional(2, "backup file");
                Console.WriteLine("Stop the server before restoring (sbox-ns service stop).");
                await DatabaseBackup.RestoreAsync(config, path, CancellationToken.None);
                Console.WriteLine($"Restored {path}. Start the server again: sbox-ns service start");
                return CliApp.Ok;
            }
            case "verify-backup":
            {
                var path = context.RequirePositional(2, "backup file");
                var scratch = context.Args.Option("scratch-database");
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler onCancel = (_, e) =>
                {
                    e.Cancel = !cancel.IsCancellationRequested;
                    cancel.Cancel();
                };
                Console.CancelKeyPress += onCancel;
                BackupVerificationReport report;
                try
                {
                    report = await BackupVerifier.VerifyAsync(config, path, scratch, cancel.Token);
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }

                if (context.Args.Flag("json"))
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
                else
                    PrintVerification(report);
                return report.Result == "failed" ? CliApp.Failure : CliApp.Ok;
            }
        }

        await using var services = CliServices.Build(config);
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        switch (sub)
        {
            case "test":
            {
                try
                {
                    var ping = await admin.PingAsync(CancellationToken.None);
                    Console.WriteLine($"OK: connected to {admin.ProviderName} at {admin.RedactedTarget} ({ping.ServerVersion}, {ping.RoundTrip.TotalMilliseconds:F0} ms)");
                    return CliApp.Ok;
                }
                catch (Exception ex)
                {
                    throw new CliException($"cannot connect to {admin.ProviderName} at {admin.RedactedTarget}: {ex.Message}");
                }
            }
            case "status":
            {
                var state = await admin.GetSchemaStateAsync(CancellationToken.None);
                var version = state.Version;
                var supported = admin.SupportedSchemaVersion;
                if (context.Args.Flag("json"))
                {
                    // Read by the update/rollback checks (SchemaRollback.TryParseState); the verdict is the caller's.
                    Console.WriteLine(new System.Text.Json.Nodes.JsonObject
                    {
                        ["provider"] = admin.ProviderName,
                        ["schemaVersion"] = version,
                        ["minBinarySchema"] = state.MinBinarySchema,
                        ["supportedSchemaVersion"] = supported,
                    }.ToJsonString());
                    return CliApp.Ok;
                }

                Console.WriteLine($"Database:        {admin.ProviderName} at {admin.RedactedTarget}");
                Console.WriteLine($"Schema version:  {version} (this build supports {supported})");
                Console.WriteLine($"Compat floor:    {state.MinBinarySchema} (min_binary_schema: the oldest schema version a binary must support to run on this database)");
                Console.WriteLine(version switch
                {
                    _ when version == supported => "Status:          up to date",
                    _ when version < supported => "Status:          migration pending (sbox-ns db migrate, or start the server)",
                    _ when state.IsNewerButCompatibleWith(supported) => "Status:          database is newer than this binary, but only through additive migrations; this binary can run on it",
                    _ => "Status:          database is newer than this binary; upgrade sbox-ns"
                });
                return state.IsTooNewFor(supported) ? CliApp.Failure : CliApp.Ok;
            }
            case "migrate":
            {
                var result = await admin.MigrateAsync(CancellationToken.None);
                Console.WriteLine(result.AppliedVersions.Count == 0
                    ? $"Schema already at version {result.ToVersion}."
                    : $"Migrated schema from version {result.FromVersion} to {result.ToVersion}.");
                return CliApp.Ok;
            }
            default:
                throw new CliException($"unknown db command '{sub}'", CliApp.Usage);
        }
    }

    private static void PrintVerification(BackupVerificationReport report)
    {
        var verdict = report.Result switch
        {
            "verified" => "VERIFIED (restore rehearsed)",
            "inspected" => "INSPECTED ONLY (archive readable; not restore-tested)",
            _ => "FAILED",
        };
        Console.WriteLine($"Backup {verdict}: {report.File}");
        Console.WriteLine($"  provider: {report.Provider} (configured: {report.ConfiguredProvider})");
        Console.WriteLine($"  level: {report.VerificationLevel}");
        if (report.SchemaVersion is { } version)
            Console.WriteLine($"  schema: version {version}, compatibility floor {report.MinBinarySchema}, this build supports {report.SupportedSchemaVersion}");
        Console.WriteLine($"  live database: {report.LiveDatabase}");
        if (report.ScratchDatabase is not null)
            Console.WriteLine($"  scratch database: {report.ScratchDatabase}");
        Console.WriteLine("  steps:");
        foreach (var step in report.Steps)
            Console.WriteLine($"    [{step.Status}] {step.Name}: {step.Detail}");
        Console.WriteLine($"  source sha256: {report.SourceSha256}");
        Console.WriteLine($"  elapsed: {report.ElapsedMs} ms");
        Console.WriteLine($"  scope: {report.Scope}");
    }

    /// <summary>
    /// Non-mutating preflight for this binary against the configured database: exits 0 when this
    /// binary can run on it (equal schema, an older schema it migrates forward, or a newer schema
    /// reachable only through additive migrations). Rollback and downgrade checks run the target
    /// binary's copy of this command, so the target's own migration knowledge is used. Binaries that
    /// predate this command fail with an unknown command instead, which the gate treats as incompatible.
    /// </summary>
    public static async Task<int> CheckCompatibilityAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        await using var services = CliServices.Build(config);
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        var state = await admin.GetSchemaStateAsync(CancellationToken.None);
        var supported = admin.SupportedSchemaVersion;
        var compatible = !state.IsTooNewFor(supported);
        var migrationRequired = state.Version < supported;
        if (context.Args.Flag("json"))
        {
            // Field names follow db status --json (camelCase). protocol versions this payload;
            // layoutGeneration is StateLayout.CurrentVersion, for future rollbacks to gate on.
            Console.WriteLine(new JsonObject
            {
                ["protocol"] = 1,
                ["version"] = BuildInfo.Version,
                ["schemaVersion"] = state.Version,
                ["supportedSchemaVersion"] = supported,
                ["minBinarySchema"] = state.MinBinarySchema,
                ["compatible"] = compatible,
                ["migrationRequired"] = migrationRequired,
                ["layoutGeneration"] = StateLayout.CurrentVersion,
            }.ToJsonString());
            return compatible ? CliApp.Ok : CliApp.Failure;
        }

        Console.WriteLine($"Schema {state.Version}; this binary supports {supported} (floor {state.MinBinarySchema}); layout generation {StateLayout.CurrentVersion}; compatible: {compatible}.");
        return compatible ? CliApp.Ok : CliApp.Failure;
    }
}
