using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// The decision behind <c>sbox-ns rollback --keep-data</c> and <c>sbox-ns update --version X --allow-downgrade</c>:
/// pure rule tests, then the gate that the commands call, with a fake host in place of the service user's binary.
/// </summary>
public sealed class SchemaRollbackTests
{
    private static SchemaMigration M(int version, SchemaChange compat, string description = "m") => new(version, description, compat, _ => string.Empty);

    // v1 baseline (breaking), v2 additive, v3 breaking, v4 additive, v5 additive.
    private static readonly IReadOnlyList<SchemaMigration> History =
    [
        M(1, SchemaChange.Breaking, "baseline"), M(2, SchemaChange.Additive), M(3, SchemaChange.Breaking, "drop column x"), M(4, SchemaChange.Additive), M(5, SchemaChange.Additive),
    ];

    private static BinarySchemaInfo Binary(int schema) => new($"0.{schema}.0", schema);

    [Fact]
    public void Additive_only_gap_is_safe_and_names_the_migrations_since()
    {
        // Database at 5, floor 3; the target supports 3, so v4 and v5 (both additive) are the whole gap.
        var decision = SchemaRollback.Evaluate(new SchemaState(5, 3), Binary(3), History);

        Assert.True(decision.Safe);
        Assert.Contains("v4, v5", decision.Reason);
        Assert.Contains("additive", decision.Reason);
    }

    [Fact]
    public void Breaking_migration_in_the_gap_is_refused_with_the_reason()
    {
        // Same database, but the target only supports 2: v3 ("drop column x") is breaking.
        var decision = SchemaRollback.Evaluate(new SchemaState(5, 3), Binary(2), History);

        Assert.False(decision.Safe);
        Assert.Contains("migration v3 (\"drop column x\") is breaking", decision.Reason);
        Assert.Contains("0.2.0 supports 2", decision.Reason);
    }

    [Fact]
    public void A_target_at_or_ahead_of_the_database_has_nothing_to_undo()
    {
        Assert.True(SchemaRollback.Evaluate(new SchemaState(3, 3), Binary(3), History).Safe);
        Assert.True(SchemaRollback.Evaluate(new SchemaState(3, 3), Binary(5), History).Safe);
    }

    [Fact]
    public void A_target_that_predates_schema_info_always_needs_the_restore_path()
    {
        var decision = SchemaRollback.Evaluate(new SchemaState(5, 1), null, History);

        Assert.False(decision.Safe);
        Assert.Contains("predates rollback-safe migrations", decision.Reason);
    }

    [Fact]
    public void A_breaking_version_this_binary_does_not_know_is_still_named()
    {
        var decision = SchemaRollback.Evaluate(new SchemaState(9, 8), Binary(5), History);

        Assert.False(decision.Safe);
        Assert.Contains("migration v8 is breaking", decision.Reason);
    }

    [Fact]
    public async Task Every_instance_must_pass_and_the_first_refusal_names_its_instance()
    {
        var host = new StateHost(new Dictionary<string, SchemaState> { ["alpha"] = new(5, 3), ["beta"] = new(5, 5) });
        var instances = new[] { Instance("alpha"), Instance("beta") };

        var decision = await SchemaRollback.CheckAsync(host, "sbox-ns", Binary(3), instances, History, CancellationToken.None);

        Assert.False(decision.Safe);
        Assert.StartsWith("beta: ", decision.Reason, StringComparison.Ordinal);

        host.States["beta"] = new SchemaState(5, 3);
        Assert.True((await SchemaRollback.CheckAsync(host, "sbox-ns", Binary(3), instances, History, CancellationToken.None)).Safe);
    }

    [Fact]
    public async Task An_unreadable_database_state_refuses_rather_than_guessing()
    {
        var decision = await SchemaRollback.CheckAsync(new StateHost([]), "sbox-ns", Binary(3), [Instance("alpha")], History, CancellationToken.None);

        Assert.False(decision.Safe);
        Assert.Contains("could not read the database schema state", decision.Reason);
    }

    [Fact]
    public async Task Gate_refuses_across_a_breaking_migration_and_offers_the_restore_path()
    {
        var host = new StateHost(new Dictionary<string, SchemaState> { ["default"] = new(99, 99) }) { Target = Binary(3) };

        var ex = await Assert.ThrowsAsync<CliException>(() =>
            UpdateCommands.RequireDataPreservingRollbackAsync(host, "sbox-ns", "previous", [Instance("default")], UpdateCommands.RestoreAlternative));

        Assert.Contains("cannot do this without losing data", ex.Message);
        Assert.Contains("migration v99 is breaking", ex.Message);
        Assert.Contains("`sbox-ns rollback` (without --keep-data) restores the database backup", ex.Message);
    }

    [Fact]
    public async Task Gate_passes_when_the_target_runs_on_the_current_schema()
    {
        // The real SQLite history: this build's own supported version against a database at that version.
        var current = BinarySchemaInfo.ForThisBuild();
        var host = new StateHost(new Dictionary<string, SchemaState> { ["default"] = new(current.SupportedSchemaVersion, 1) }) { Target = current };

        await UpdateCommands.RequireDataPreservingRollbackAsync(host, "sbox-ns", "previous", [Instance("default")], UpdateCommands.RestoreAlternative);
    }

    [Fact]
    public async Task Gate_refuses_a_binary_without_schema_info()
    {
        var host = new StateHost(new Dictionary<string, SchemaState> { ["default"] = new(3, 1) }) { Target = null };

        var ex = await Assert.ThrowsAsync<CliException>(() =>
            UpdateCommands.RequireDataPreservingRollbackAsync(host, "sbox-ns", "previous", [Instance("default")], "restore instead"));

        Assert.Contains("predates rollback-safe migrations", ex.Message);
        Assert.EndsWith("restore instead", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_refuses_when_the_target_probe_disagrees_with_the_floor_rule()
    {
        // The floor rule passes, but the target binary itself says it cannot run on this database.
        var current = BinarySchemaInfo.ForThisBuild();
        var host = new StateHost(new Dictionary<string, SchemaState> { ["default"] = new(current.SupportedSchemaVersion, 1) })
        {
            Target = current,
            ProbeExitCode = 1,
        };

        var ex = await Assert.ThrowsAsync<CliException>(() =>
            UpdateCommands.RequireDataPreservingRollbackAsync(host, "sbox-ns", "previous", [Instance("default")], UpdateCommands.RestoreAlternative));

        Assert.Contains("compatibility probe", ex.Message);
    }

    [Fact]
    public void Gate_reads_reasons_from_the_instances_configured_provider_migrations()
    {
        Assert.Same(SqliteMigrations.All, SchemaRollback.KnownMigrationsFor(Instance("default")));
        Assert.Same(PostgresMigrations.All, SchemaRollback.KnownMigrationsFor(PostgresInstance("pg")));
    }

    [Fact]
    public void Schema_info_round_trips_and_rejects_what_an_older_binary_prints()
    {
        var info = BinarySchemaInfo.ForThisBuild();

        Assert.Equal(info, BinarySchemaInfo.TryParse(info.ToJson()));
        Assert.Null(BinarySchemaInfo.TryParse("error: unknown command 'schema-info'. Run `sbox-ns help`."));
        Assert.Null(BinarySchemaInfo.TryParse("""{"supportedSchemaVersion":3}""")); // no floor marker: not an S5 answer
        Assert.Null(BinarySchemaInfo.TryParse(""));
    }

    [Fact]
    public void Database_state_parses_from_db_status_json_only()
    {
        Assert.Equal(new SchemaState(4, 1), SchemaRollback.TryParseState("""{"provider":"sqlite","schemaVersion":4,"minBinarySchema":1,"supportedSchemaVersion":4}"""));
        Assert.Null(SchemaRollback.TryParseState("Schema version:  4"));
        Assert.Null(SchemaRollback.TryParseState("""{"schemaVersion":4}"""));
    }

    private static ServerInstance Instance(string name)
        => new(name, "sbox-ns.service", ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "sbox-ns-schema-rollback", "config"), Path.Combine(Path.GetTempPath(), "sbox-ns-schema-rollback", "data"), null, _ => null));

    private static ServerInstance PostgresInstance(string name)
        => new(name, "sbox-ns.service", ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "sbox-ns-schema-rollback", "config"), Path.Combine(Path.GetTempPath(), "sbox-ns-schema-rollback", "data"),
            new Dictionary<string, string> { ["database.provider"] = "postgres" }, _ => null));

    private sealed class StateHost(Dictionary<string, SchemaState> states) : IUpdateHost
    {
        public Dictionary<string, SchemaState> States { get; } = states;

        public BinarySchemaInfo? Target { get; init; }

        public Task<SchemaState?> ReadSchemaStateAsync(string binary, ServerInstance instance, CancellationToken ct)
            => Task.FromResult(States.TryGetValue(instance.Name, out var state) ? state : null);

        public Task<BinarySchemaInfo?> ProbeSchemaInfoAsync(string binary, CancellationToken ct) => Task.FromResult(Target);

        public Task<bool> IsActiveAsync(string unit, CancellationToken ct) => throw new NotSupportedException();

        public Task<PostUpdateVerdict?> ReadUpdateVerdictAsync(string binary, ServerInstance instance, int hours, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ObservedLibrary>?> ReadObservedLibrariesAsync(string binary, ServerInstance instance, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> StartAsync(string unit, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> StopAsync(string unit, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MigrateAsync(string binary, ServerInstance instance, CancellationToken ct) => throw new NotSupportedException();

        public int ProbeExitCode { get; init; }

        public Task<int> CheckCompatibilityAsync(string targetBinary, string installedBinary, ServerInstance instance, CancellationToken ct)
            => Task.FromResult(ProbeExitCode);

        public Task<int> BackupAsync(string binary, ServerInstance instance, string outputPath, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RestoreAsync(string binary, ServerInstance instance, string backupPath, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RevertLayoutAsync(string binary, IReadOnlyList<ServerInstance> instances, CancellationToken ct) => throw new NotSupportedException();
    }
}
