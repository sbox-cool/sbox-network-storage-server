using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>Whole-endpoint JavaScript handlers (<c>runtime: javascript</c>) run through the same executor as YAML.</summary>
public sealed class ScriptedEndpointExecutorTests
{
    [Fact]
    public async Task Script_HappyPath_ReadsRecordAndReturnsBody()
    {
        var script = """
            export default function handle(req, ns) {
              const player = ns.records.get("players", req.player.steamId);
              return { ok: true, coins: player.coins + req.input.price };
            }
            """;
        var executor = Executor(script, records: PlayerRecord(250d));

        var result = await Run(executor, input: new Dictionary<string, object?> { ["price"] = 5d });

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal(200, result.Status);
        Assert.Contains("\"coins\":255", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Script_YamlAndScriptReadTheSameRecords()
    {
        // The same record, read through a YAML read step and through ns.records.get, must agree.
        var records = PlayerRecord(42d);
        var yaml = new Dictionary<string, object?>
        {
            ["proj:ep:yaml"] = new Dictionary<string, object?>
            {
                ["steps"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "p", ["type"] = "read", ["collection"] = "players", ["key"] = "{{steamId}}" },
                },
                ["response"] = new Dictionary<string, object?>
                {
                    ["status"] = 200d,
                    ["body"] = new Dictionary<string, object?> { ["ok"] = true, ["coins"] = "{{p.coins}}" },
                },
            },
        };
        var yamlResult = await Run(new EndpointExecutor(new FakeDataSource(yaml, PlayerRecord(42d))), endpoint: "yaml");

        var scripted = Executor("export default function handle(req, ns) { return { ok: true, coins: ns.records.get(\"players\", req.player.steamId).coins }; }", records);
        var scriptedResult = await Run(scripted);

        Assert.True(yamlResult.Ok);
        Assert.True(scriptedResult.Ok);
        Assert.Contains("\"coins\":42", JsonSerializer.Serialize(yamlResult.Body));
        Assert.Contains("\"coins\":42", JsonSerializer.Serialize(scriptedResult.Body));
    }

    [Fact]
    public async Task Script_KillSwitchOff_Returns503WithoutRunning()
    {
        var executor = Executor("export default function handle() { return { ok: true }; }", scriptsEnabled: false);

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(503, result.Status);
        Assert.Contains("SCRIPTS_DISABLED", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Script_InfiniteLoop_HitsLimitAndReturns500()
    {
        var executor = Executor("export default function handle() { while (true) {} }");

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public void Engine_Guards_AreSetOnEveryEngine()
    {
        var options = new Jint.Options();
        ScriptEngineGuards.Configure(options, TimeSpan.FromSeconds(1));

        Assert.False(options.AgentCanSuspend);
        Assert.True(options.Constraints.StackOverflowGuard);
        Assert.Equal(ScriptEngineGuards.MaxArrayLength, options.Constraints.MaxArraySize);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Constraints.RegexTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Constraints.PromiseTimeout);
    }

    [Fact]
    public async Task Script_StatementBudget_IsSharedAcrossModuleScopeHandlerAndToJson()
    {
        // Each part runs about 20k loop iterations: under a 50k limit alone, over it together. Jint resets its own
        // statement counter at every engine entry, so only the request budget stops this run.
        var script = """
            const warm = (() => { for (let i = 0; i < 20000; i++) {} return 1; })();
            export default function handle() {
              for (let i = 0; i < 20000; i++) {}
              return { ok: warm === 1, toJSON() { for (let i = 0; i < 20000; i++) {} return { ok: true }; } };
            }
            """;

        var limited = await Run(ExecutorWithLimits(script, EndpointScriptLimits.Defaults with { MaxStatements = 50_000 }));
        Assert.False(limited.Ok);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(limited.Body));

        // The same script passes under the default budget, so the failure above comes from the shared budget alone.
        var unlimited = await Run(Executor(script));
        Assert.True(unlimited.Ok);
    }

    [Fact]
    public async Task StoredTypeScriptEndpoint_RunsWithoutStripping_WhenSaveAlreadyCompiledIt()
    {
        const string source = """
            export default function handle(req: { input: { n: number } }): { doubled: number } {
              return { doubled: req.input.n * 2 };
            }
            """;
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults);
        var typeScript = new TypeScriptEndpointRuntime(javaScript);
        // Save-time validation compiles the source once; the request then reuses that result.
        Assert.Empty(typeScript.Check(source));
        var compiledAtSave = typeScript.StripInvocations;

        var definitions = new Dictionary<string, object?> { ["proj:ep:script"] = TypeScript(source) };
        var executor = new EndpointExecutor(
            new FakeDataSource(definitions, new Dictionary<string, object?>()),
            scriptRuntimes: [javaScript, typeScript]);

        var result = await Run(executor, input: new Dictionary<string, object?> { ["n"] = 4d });

        Assert.True(result.Ok);
        Assert.Contains("8", JsonSerializer.Serialize(result.Body));
        Assert.Equal(compiledAtSave, typeScript.StripInvocations);
    }

    [Fact]
    public void CompiledCache_EvictsTheLeastRecentlyUsedEntry_NotEverything()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _)); // a is now the most recently used
        cache.Set("c", 3);                     // evicts b, the least recently used

        Assert.True(cache.TryGet("a", out var a) && a == 1);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out var c) && c == 3);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Gate_PerProjectLimit_RefusesExtraSlotForSameProjectOnly()
    {
        var gate = new ScriptConcurrencyGate(globalLimit: 8, perProjectLimit: 2);
        using var first = gate.TryEnter("proj");
        using var second = gate.TryEnter("proj");
        Assert.NotNull(first);
        Assert.NotNull(second);

        using var third = gate.TryEnter("proj");
        Assert.Null(third);
        using var otherProject = gate.TryEnter("other");
        Assert.NotNull(otherProject);
    }

    [Fact]
    public void Gate_GlobalLimit_RefusesWhenTheServerIsFull()
    {
        var gate = new ScriptConcurrencyGate(globalLimit: 2, perProjectLimit: 2);
        using var a = gate.TryEnter("a");
        using var b = gate.TryEnter("b");
        Assert.NotNull(a);
        Assert.NotNull(b);

        using var c = gate.TryEnter("c");
        Assert.Null(c);
    }

    [Fact]
    public void Gate_DisposedSlot_FreesCapacityAndDoubleDisposeReleasesOnce()
    {
        var gate = new ScriptConcurrencyGate(globalLimit: 1, perProjectLimit: 1);
        var slot = gate.TryEnter("proj");
        Assert.NotNull(slot);
        Assert.Null(gate.TryEnter("proj"));

        slot.Dispose();
        slot.Dispose(); // a second release would overflow the one-permit semaphore and throw

        using var again = gate.TryEnter("proj");
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Script_GateFull_ReturnsScriptBusy503_AndRecoversWhenASlotFrees()
    {
        var gate = new ScriptConcurrencyGate(globalLimit: 4, perProjectLimit: 1);
        var executor = ExecutorWithGate("export default function handle() { return { ok: true }; }", gate);

        using var held = gate.TryEnter("proj");
        Assert.NotNull(held);
        var busy = await Run(executor);
        Assert.False(busy.Ok);
        Assert.Equal(503, busy.Status);
        Assert.Contains("SCRIPT_BUSY", JsonSerializer.Serialize(busy.Body));

        held.Dispose();
        var ok = await Run(executor);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Script_AtomicsWait_FailsFastWithTypedError()
    {
        // Atomics.wait would park the host thread for the full 5 s; the global is removed, so it fails at once.
        var executor = Executor("""
            export default function handle() {
              const cell = new Int32Array(new SharedArrayBuffer(4));
              return { ok: Atomics.wait(cell, 0, 0, 5000) };
            }
            """);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(executor);
        clock.Stop();

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_ERROR", JsonSerializer.Serialize(result.Body));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Script_DeepNestedResult_YieldsTypedErrorAndProcessSurvives()
    {
        // Prove the guard is on for the engine the runtime will build, before running anything. Without it, the deep
        // array below ends the test host instead of producing an error.
        using var budget = new ScriptRunBudget(EndpointScriptLimits.Defaults, CancellationToken.None);
        var runtimeOptions = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults)
            .CreateOptions(budget, loader: null);
        Assert.True(runtimeOptions.Constraints.StackOverflowGuard);

        var executor = Executor("""
            export default function handle() {
              const deep = new Array(300000).fill(0).reduce(acc => [acc], []);
              return { ok: true, deep };
            }
            """);

        var result = await Run(executor);

        Assert.False(result.Ok);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.True(json.Contains("SCRIPT_ERROR", StringComparison.Ordinal) || json.Contains("SCRIPT_LIMIT_EXCEEDED", StringComparison.Ordinal), json);
    }

    [Fact]
    public async Task Script_CatastrophicRegex_GivesUpWithinTheScriptTimeout()
    {
        var executor = Executor("""
            export default function handle(req) {
              return { ok: /^(a+)+$/.test(req.input.text) };
            }
            """);
        var input = new Dictionary<string, object?> { ["text"] = new string('a', 40) + "!" };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(executor, input: input);
        clock.Stop();

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Script_RequestInput_IsBuiltWithJsonParse_KeepsProtoAsOwnProperty()
    {
        var executor = Executor("""
            export default function handle(req) {
              return {
                ownProto: Object.prototype.hasOwnProperty.call(req.input, "__proto__"),
                inheritedAdmin: req.input.admin === true,
              };
            }
            """);
        var input = new Dictionary<string, object?> { ["__proto__"] = new Dictionary<string, object?> { ["admin"] = true } };

        var result = await Run(executor, input: input);

        Assert.True(result.Ok, result.OwnerDetail);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"ownProto\":true", json);
        Assert.Contains("\"inheritedAdmin\":false", json);
    }

    [Theory]
    [InlineData("'x'.repeat(2000000)")]
    [InlineData("'x'.padStart(2000000, 'y')")]
    [InlineData("'x'.padEnd(2000000, 'y')")]
    [InlineData("Array.from({ length: 2000000 })")]
    [InlineData("new Array(2000000).fill(0)")]
    [InlineData("new Array(2000000).join('ab')")]
    public async Task Script_SingleCallAllocators_AreCapped(string expression)
    {
        var executor = Executor($"export default function handle() {{ return {{ ok: {expression}.length > 0 }}; }}");

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Script_CappedAllocators_StillAllowNormalSizes()
    {
        var executor = Executor("""
            export default function handle() {
              return { ok: true, text: 'x'.repeat(1000).padStart(1200, '-').length, items: Array.from({ length: 1000 }).length };
            }
            """);

        var result = await Run(executor);

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Contains("\"text\":1200", JsonSerializer.Serialize(result.Body));
    }

    private const string BankScript = """
        export function coinsOf() { return ns.records.get("players", req.player.steamId).coins; }
        export function whoAmI() { return req.player.steamId; }
        export const VERSION = 2;
        """;

    [Fact]
    public async Task Global_NamedImport_ReadsTheCallersRecordThroughTheGlobal()
    {
        var executor = Project(PlayerRecord(250d),
            ("bank", Global(BankScript)),
            ("caller", Scripted("""
                import { coinsOf, whoAmI } from "globals/bank";
                export default function handle(req, ns) { return { ok: true, coins: coinsOf(), who: whoAmI() }; }
                """)));

        var result = await Run(executor, endpoint: "caller");

        Assert.True(result.Ok, result.OwnerDetail);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"coins\":250", json);
        Assert.Contains("\"who\":\"steam1\"", json);
    }

    [Fact]
    public async Task Global_GlobalsNamespace_ExposesExportsByEndpointId()
    {
        var executor = Project(PlayerRecord(7d),
            ("bank", Global(BankScript)),
            ("caller", Scripted("""
                export default function handle(req, ns) { return { ok: true, coins: globals.bank.coinsOf(), version: globals.bank.VERSION }; }
                """)));

        var result = await Run(executor, endpoint: "caller");

        Assert.True(result.Ok, result.OwnerDetail);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"coins\":7", json);
        Assert.Contains("\"version\":2", json);
    }

    [Fact]
    public async Task Global_OverHttpGate_IsAnsweredLikeAnUnknownEndpoint()
    {
        var executor = Project(PlayerRecord(1d), ("bank", Global(BankScript)));

        var global = await TryPublic(executor, "bank");
        var unknown = await TryPublic(executor, "missing");

        Assert.Null(unknown);
        Assert.NotNull(global);
        Assert.False(global.Ok);
        Assert.Equal(404, global.Status);
        Assert.Equal(NotFoundBody("bank"), JsonSerializer.Serialize(global.Body));
    }

    [Fact]
    public async Task Global_LoopInside_StopsTheCallerWithTheLimitError()
    {
        var executor = Project(new Dictionary<string, object?>(),
            ("spin", Global("export function spin() { while (true) {} }")),
            ("caller", Scripted("""
                import { spin } from "globals/spin";
                export default function handle() { spin(); return { ok: true }; }
                """)));

        var result = await Run(executor, endpoint: "caller");

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Workflow_RunFromScript_AppliesItsWritesAndReturnsItsResult()
    {
        var executor = ProjectWithWorkflows(PlayerRecord(250d),
            [("grant_coins", GrantCoinsWorkflow())],
            ("caller", Scripted("""
                export default function handle() {
                    const r = ns.workflows.run("grant_coins", { amount: 5 });
                    return { ok: true, granted: r.granted };
                }
                """)),
            ("read", Scripted("""
                export default function handle() {
                    return { coins: ns.records.get("players", "steam1").coins };
                }
                """)));

        var run = await Run(executor, endpoint: "caller", liveServe: true);

        Assert.True(run.Ok, run.OwnerDetail);
        Assert.Contains("\"granted\":5", JsonSerializer.Serialize(run.Body));
        var read = await Run(executor, endpoint: "read");
        var readBody = JsonSerializer.Serialize(read.Body);
        Assert.True(readBody.Contains("\"coins\":255"), readBody);
    }

    [Fact]
    public async Task Workflow_CamelCaseAlias_IsCallableAsAMethodOfWorkflows()
    {
        var executor = ProjectWithWorkflows(PlayerRecord(250d),
            [("grant_coins", GrantCoinsWorkflow())],
            ("caller", Scripted("""
                export default function handle() {
                    return { ok: true, granted: workflows.grantCoins({ amount: 7 }).granted };
                }
                """)));

        var run = await Run(executor, endpoint: "caller");

        Assert.True(run.Ok, run.OwnerDetail);
        Assert.Contains("\"granted\":7", JsonSerializer.Serialize(run.Body));
    }

    [Fact]
    public async Task Workflow_Failure_ThrowsATypedScriptErrorAndAppliesNoWrites()
    {
        var executor = ProjectWithWorkflows(PlayerRecord(250d),
            [("spend", SpendWorkflow())],
            ("caller", Scripted("""
                export default function handle() {
                    ns.workflows.run("spend", { amount: 50 });
                    return { ok: true };
                }
                """)),
            ("read", Scripted("""
                export default function handle() {
                    return { coins: ns.records.get("players", "steam1").coins };
                }
                """)));

        var run = await Run(executor, endpoint: "caller", liveServe: true);

        Assert.False(run.Ok);
        Assert.Equal(409, run.Status);
        Assert.Contains("NO_FUNDS", JsonSerializer.Serialize(run.Body));
        var read = await Run(executor, endpoint: "read");
        Assert.Contains("\"coins\":250", JsonSerializer.Serialize(read.Body));
    }

    [Fact]
    public async Task Workflow_UnknownId_ThrowsATypedNotFoundError()
    {
        var executor = ProjectWithWorkflows(PlayerRecord(250d), [],
            ("caller", Scripted("""
                export default function handle() {
                    ns.workflows.run("missing", {});
                    return { ok: true };
                }
                """)));

        var run = await Run(executor, endpoint: "caller");

        Assert.False(run.Ok);
        Assert.Contains("WORKFLOW_NOT_FOUND", JsonSerializer.Serialize(run.Body));
    }

    [Fact]
    public async Task Workflow_NestingDeeperThanTheLimit_StopsTheScriptWithTheLimitError()
    {
        var executor = ProjectWithWorkflows(PlayerRecord(250d),
            [("recurse", RecursiveWorkflow())],
            ("caller", Scripted("""
                export default function handle() {
                    ns.workflows.run("recurse", {});
                    return { ok: true };
                }
                """)));

        var run = await Run(executor, endpoint: "caller");

        Assert.False(run.Ok);
        Assert.Equal(500, run.Status);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(run.Body));
    }

    [Fact]
    public async Task Script_MoreStagedWritesThanTheCap_YieldsScriptLimitExceeded()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              for (let i = 0; i <= 1000; i++) ns.records.put("players", "p" + i, { coins: i });
              return { ok: true };
            }
            """));

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Script_StagedBytesOverTheCap_YieldsScriptLimitExceeded()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              const blob = "x".repeat(600000);
              ns.records.put("players", "a", { blob });
              ns.records.put("players", "b", { blob });
              return { ok: true };
            }
            """));

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Script_ScanOverTheRowCap_IsRefusedBeforeAnyRowIsRead()
    {
        var records = new Dictionary<string, object?>();
        for (var i = 0; i <= 1000; i++) records[$"proj:players:p{i}"] = new Dictionary<string, object?> { ["coins"] = (double)i };
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?>
            {
                ["runtime"] = "javascript",
                ["script"] = "export default function handle(req, ns) { return { n: ns.records.list(\"players\").length }; }",
            },
        };
        var source = new FakeDataSource(definitions, records);
        var executor = new EndpointExecutor(source, scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));

        var result = await Run(executor);

        Assert.Equal(400, result.Status);
        Assert.Contains("SCAN_TOO_LARGE", JsonSerializer.Serialize(result.Body));
        Assert.Equal(0, source.ScanCalls);
    }

    [Fact]
    public async Task ScriptError_OwnerDetailHasMessageAndLocation_NotAStackTrace()
    {
        var result = await Run(Executor("export default function handle(req, ns) {\n  throw new Error(\"bad\");\n}"));

        Assert.False(result.Ok);
        Assert.Contains("SCRIPT_ERROR", JsonSerializer.Serialize(result.Body));
        Assert.Contains("bad", result.OwnerDetail);
        Assert.Contains("line 2", result.OwnerDetail);
        Assert.DoesNotContain("   at ", result.OwnerDetail);
    }

    [Fact]
    public void UnclassifiedFault_OwnerGetsTypeAndMessage_FullExceptionGoesToTheLog()
    {
        var logger = new RecordingLogger<JavaScriptEndpointRuntime>();
        var runtime = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults, logger);
        Exception fault;
        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException error) { fault = error; }

        var outcome = runtime.Classify(fault);

        Assert.Equal(EndpointScriptOutcomeKind.ScriptError, outcome.Kind);
        Assert.Equal("System.InvalidOperationException: boom", outcome.OwnerDetail);
        Assert.Same(fault, Assert.Single(logger.Errors));
    }

    /// <summary>Keeps the exceptions a runtime logs, so a test can check the full trace reached the log.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception);
        }
    }

    [Fact]
    public async Task Script_ReadsBeyondTheLoadCap_YieldLimitExceeded()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              for (let i = 0; i < 65; i++) ns.records.get("players", "p" + i);
              return { ok: true };
            }
            """));

        Assert.Equal(500, result.Status);
        Assert.Contains("SCRIPT_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
    }


    private static EndpointExecutor Project(Dictionary<string, object?> records,
        params (string Slug, Dictionary<string, object?> Definition)[] endpoints) =>
        ProjectWithWorkflows(records, [], endpoints);

    private static EndpointExecutor ProjectWithWorkflows(Dictionary<string, object?> records,
        (string Id, Dictionary<string, object?> Definition)[] workflows,
        params (string Slug, Dictionary<string, object?> Definition)[] endpoints)
    {
        var definitions = endpoints.ToDictionary(e => $"proj:ep:{e.Slug}", e => (object?)e.Definition);
        foreach (var (id, definition) in workflows)
            definitions[$"proj:wf:{id}"] = definition;
        return new EndpointExecutor(new FakeDataSource(definitions, records),
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

    private static List<object?> Arr(params object?[] items) => [.. items];

    /// <summary>Adds 'amount' to the player's coins and returns it as 'granted'.</summary>
    private static Dictionary<string, object?> GrantCoinsWorkflow() => Obj(
        ("steps", Arr(Obj(
            ("id", "grant"), ("type", "write"), ("collection", "players"), ("key", "steam1"),
            ("ops", Arr(Obj(("op", "inc"), ("path", "coins"), ("value", "{{input.amount}}"))))))),
        ("returns", Obj(("granted", "{{input.amount}}"))));

    /// <summary>Sets the coins to 'amount' first, then fails when 'amount' is above 10. The write must not apply.</summary>
    private static Dictionary<string, object?> SpendWorkflow() => Obj(
        ("steps", Arr(
            Obj(("id", "spend"), ("type", "write"), ("collection", "players"), ("key", "steam1"),
                ("ops", Arr(Obj(("op", "set"), ("path", "coins"), ("value", "{{input.amount}}"))))),
            Obj(("id", "funds"), ("type", "condition"),
                ("check", Obj(("field", "input.amount"), ("op", "<="), ("value", 10d))),
                ("onFail", Obj(("status", 409d), ("error", "NO_FUNDS"), ("message", "Not enough coins.")))))));

    /// <summary>Calls itself through a workflow step, so nesting only stops at the depth limit.</summary>
    private static Dictionary<string, object?> RecursiveWorkflow() => Obj(
        ("steps", Arr(Obj(("id", "again"), ("type", "workflow"), ("workflow", "recurse")))));

    private static Dictionary<string, object?> Global(string script) =>
        new() { ["runtime"] = "javascript", ["scope"] = "global", ["script"] = script };

    private static Dictionary<string, object?> Scripted(string script) =>
        new() { ["runtime"] = "javascript", ["script"] = script };

    private static Dictionary<string, object?> TypeScript(string script) =>
        new() { ["runtime"] = "typescript", ["script"] = script };

    private static Task<EndpointExecutionResult?> TryPublic(EndpointExecutor executor, string endpoint) =>
        executor.TryExecuteAsync("proj", endpoint,
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None,
            enforcePublicAccessGates: true);

    /// <summary>The body the HTTP layer writes for an unknown slug (<c>WriteEndpointErrorAsync</c>).</summary>
    private static string NotFoundBody(string slug) =>
        JsonSerializer.Serialize(new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = $"Endpoint \"{slug}\" was not found." } });

    [Fact]
    public async Task Script_NoHostOrClrAccess()
    {
        var executor = Executor("""
            export default function handle() {
              return { system: typeof System, fetch: typeof fetch, require: typeof require, process: typeof process };
            }
            """);

        var result = await Run(executor);

        Assert.True(result.Ok);
        var body = Assert.IsType<Dictionary<string, object?>>(result.Body);
        Assert.Equal("undefined", body["system"]);
        Assert.Equal("undefined", body["fetch"]);
        Assert.Equal("undefined", body["require"]);
        Assert.Equal("undefined", body["process"]);
    }

    [Fact]
    public async Task Script_ClrTypeReference_FailsAsScriptError()
    {
        var executor = Executor("export default function handle() { return System.Environment.MachineName; }");

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("SCRIPT_ERROR", json);
        Assert.DoesNotContain("MachineName", json);
    }

    [Fact]
    public async Task TypeScript_EndpointRunsTheStrippedHandler()
    {
        var executor = TypeScriptExecutor("""
            interface Reward { coins: number }
            enum Tier { Bronze = "bronze", Gold = "gold" }
            function twice<T extends number>(value: T): number { return value * 2; }
            export default function handle(req: { input: { amount: number } }): { ok: boolean; tier: string; coins: number } {
                const reward: Reward = { coins: twice<number>(req.input.amount) };
                return { ok: true, tier: Tier.Gold, coins: reward.coins };
            }
            """);

        var result = await Run(executor, input: new Dictionary<string, object?> { ["amount"] = 5d });

        Assert.True(result.Ok, result.OwnerDetail);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"tier\":\"gold\"", json);
        Assert.Contains("\"coins\":10", json);
    }

    [Fact]
    public async Task TypeScript_RuntimeError_ReportsTheTypeScriptLine()
    {
        var executor = TypeScriptExecutor("""
            export default function handle(req: unknown): unknown {
                const message: string = "boom";
                throw new Error(message);
            }
            """);

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("line 3", result.OwnerDetail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static EndpointExecutor Executor(string script, Dictionary<string, object?>? records = null, bool scriptsEnabled = true)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        return new EndpointExecutor(
            new FakeDataSource(definitions, records ?? new Dictionary<string, object?>()),
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled));
    }

    private static EndpointExecutor ExecutorWithLimits(string script, EndpointScriptLimits limits)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, limits);
        return new EndpointExecutor(
            new FakeDataSource(definitions, new Dictionary<string, object?>()),
            scriptRuntimes: [javaScript, new TypeScriptEndpointRuntime(javaScript)]);
    }

    private static EndpointExecutor ExecutorWithGate(string script, ScriptConcurrencyGate gate)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        return new EndpointExecutor(
            new FakeDataSource(definitions, new Dictionary<string, object?>()),
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true),
            scriptGate: gate);
    }

    private static EndpointExecutor TypeScriptExecutor(string script)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = TypeScript(script),
        };
        return new EndpointExecutor(
            new FakeDataSource(definitions, new Dictionary<string, object?>()),
            scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));
    }

    private static async Task<EndpointExecutionResult> Run(
        EndpointExecutor executor, string endpoint = "script", Dictionary<string, object?>? input = null,
        bool liveServe = false)
    {
        var result = await executor.TryExecuteAsync(
            "proj", endpoint,
            input: input ?? new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, liveServe: liveServe);
        return result!;
    }

    private static Dictionary<string, object?> PlayerRecord(double coins) =>
        new() { ["proj:players:steam1"] = new Dictionary<string, object?> { ["coins"] = coins } };

    /// <summary>Minimal store: endpoint definitions by "proj:ep:slug" and records by "proj:collection:key".</summary>
    internal sealed class FakeDataSource : IEndpointDataSource, IEndpointScanCounter
    {
        private readonly Dictionary<string, object?> _definitions;
        private readonly Dictionary<string, object?> _records;

        /// <summary>Row-reading scans issued. A scan refused by its count must leave this at zero.</summary>
        public int ScanCalls { get; private set; }

        public FakeDataSource(Dictionary<string, object?> definitions, Dictionary<string, object?> records)
        {
            _definitions = definitions;
            _records = records;
        }

        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
        {
            var key = $"{projectId}:ep:{endpointSlug}";
            return Task.FromResult(_definitions.TryGetValue(key, out var def) ? def as Dictionary<string, object?> : null);
        }

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(
                [new Dictionary<string, object?> { ["collection_id"] = "players", ["name"] = "players" }]);

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
        {
            var ck = $"{projectId}:{collectionId}:{key}";
            return Task.FromResult(_records.TryGetValue(ck, out var record) ? record : null);
        }

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        {
            ScanCalls++;
            var prefix = $"{projectId}:{collectionId}:";
            var results = _records.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
            return Task.FromResult<IReadOnlyList<object?>>(results);
        }

        public Task<long> CountCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        {
            var prefix = $"{projectId}:{collectionId}:";
            return Task.FromResult((long)_records.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal)));
        }

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult(_definitions.GetValueOrDefault($"{projectId}:wf:{workflowId}") as Dictionary<string, object?>);

        public Task<IReadOnlyList<string>> ListWorkflowIdsAsync(string projectId, CancellationToken ct)
        {
            var prefix = $"{projectId}:wf:";
            return Task.FromResult<IReadOnlyList<string>>(
                _definitions.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Select(key => key[prefix.Length..]).ToList());
        }

        public Task<IReadOnlyList<string>> ListEndpointIdsAsync(string projectId, CancellationToken ct)
        {
            var prefix = $"{projectId}:ep:";
            return Task.FromResult<IReadOnlyList<string>>(
                _definitions.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Select(key => key[prefix.Length..]).ToList());
        }

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => Task.FromResult<IEndpointWriteTransaction>(new FakeWriteTransaction(_records));

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> IsWebhookOutboxEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Stages a transaction's writes and applies them to the fake store only on commit.</summary>
    private sealed class FakeWriteTransaction(Dictionary<string, object?> records) : IEndpointWriteTransaction
    {
        private readonly List<(string Key, Dictionary<string, object?>? Payload)> _staged = [];

        public Task WriteRecordAsync(string projectId, string collectionId, string key,
            IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        {
            _staged.Add(($"{projectId}:{collectionId}:{key}", new Dictionary<string, object?>(payload)));
            return Task.CompletedTask;
        }

        public Task DeleteRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
        {
            _staged.Add(($"{projectId}:{collectionId}:{key}", null));
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            foreach (var (key, payload) in _staged)
            {
                if (payload is null) records.Remove(key);
                else records[key] = payload;
            }
            _staged.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Save-time validation of scripted endpoints: runtime, script source, and exclusivity with steps.</summary>
public sealed class ScriptedEndpointValidationTests
{
    private static readonly DefinitionValidationContext Context = new()
    {
        ScriptRuntimes = EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true),
    };

    [Fact]
    public void Validate_SyntaxError_ReportsLineAndColumn()
    {
        var diagnostics = Validate(new Dictionary<string, object?>
        {
            ["runtime"] = "javascript",
            ["script"] = "export default function handle(req, ns) {\n  const x = ;\n  return {};\n}",
        });

        var error = Assert.Single(diagnostics, d => d.Code == "SCRIPT_SYNTAX");
        Assert.True(error.IsError);
        Assert.StartsWith("Line 2, column", error.Message);
    }

    [Fact]
    public void Validate_TypeScriptSyntaxError_ReportsTypeScriptLineAndColumn()
    {
        var diagnostics = Validate(new Dictionary<string, object?>
        {
            ["runtime"] = "typescript",
            ["script"] = "export default function handle() {\n  const x: number = ;\n}",
        });

        var error = Assert.Single(diagnostics, d => d.Code == "SCRIPT_SYNTAX");
        Assert.True(error.IsError);
        Assert.StartsWith("Line 2, column 21", error.Message);
    }

    [Fact]
    public void Validate_ScriptAndSteps_IsRejected()
    {
        var diagnostics = Validate(new Dictionary<string, object?>
        {
            ["runtime"] = "javascript",
            ["script"] = "export default function handle() { return {}; }",
            ["steps"] = new List<object?>(),
        });

        Assert.Contains(diagnostics, d => d.Code == "SCRIPT_AND_STEPS" && d.IsError);
    }

    [Fact]
    public void Validate_MissingScript_IsRejected()
    {
        var diagnostics = Validate(new Dictionary<string, object?> { ["runtime"] = "javascript" });

        Assert.Contains(diagnostics, d => d.Code == "MISSING_SCRIPT" && d.IsError);
    }

    [Fact]
    public void Validate_ValidScript_HasNoScriptErrors()
    {
        var diagnostics = Validate(new Dictionary<string, object?>
        {
            ["runtime"] = "javascript",
            ["script"] = "export default function handle(req, ns) { return { ok: true }; }",
        });

        Assert.DoesNotContain(diagnostics, d => d.Code.StartsWith("SCRIPT", StringComparison.Ordinal) && d.IsError);
    }

    [Fact]
    public void Validate_ImportOfExportedGlobal_HasNoErrors()
    {
        var context = ContextWith("caller", ("bank", "export function coinsOf() { return 1; }"));

        var diagnostics = ValidateWith(context, Script("import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, c: coinsOf() }; }"));

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Fact]
    public void Validate_ImportOfMissingGlobal_NamesTheImporter()
    {
        var context = ContextWith("caller", ("bank", "export function coinsOf() { return 1; }"));

        var diagnostics = ValidateWith(context, Script("import { x } from \"globals/nope\";\nexport default function handle() { return { ok: true }; }"));

        var error = Assert.Single(diagnostics, d => d.Code == "GLOBAL_NOT_FOUND");
        Assert.True(error.IsError);
        Assert.Contains("\"caller\"", error.Message);
        Assert.Contains("globals/nope", error.Message);
    }

    [Fact]
    public void Validate_ImportOfMissingExport_NamesTheImporter()
    {
        var context = ContextWith("caller", ("bank", "export function coinsOf() { return 1; }"));

        var diagnostics = ValidateWith(context, Script("import { nope } from \"globals/bank\";\nexport default function handle() { return { ok: true }; }"));

        var error = Assert.Single(diagnostics, d => d.Code == "GLOBAL_EXPORT_NOT_FOUND");
        Assert.True(error.IsError);
        Assert.Contains("\"caller\"", error.Message);
        Assert.Contains("\"nope\"", error.Message);
        Assert.Contains("globals/bank", error.Message);
    }

    [Fact]
    public void Validate_GlobalImportingItselfThroughAnotherGlobal_IsACycle()
    {
        var context = ContextWith("a", ("b", "import { y } from \"globals/a\"; export function x() { return y(); }"));

        var diagnostics = ValidateWith(context, Script("import { x } from \"globals/b\"; export function y() { return x(); }", scope: "global"));

        var error = Assert.Single(diagnostics, d => d.Code == "GLOBAL_CYCLE");
        Assert.True(error.IsError);
        Assert.Contains("globals/a -> globals/b -> globals/a", error.Message);
    }

    [Fact]
    public void Validate_GlobalImportingItself_IsACycle()
    {
        var context = ContextWith("a");

        var diagnostics = ValidateWith(context, Script("import { y } from \"globals/a\"; export function z() { return y(); }", scope: "global"));

        Assert.Contains(diagnostics, d => d.Code == "GLOBAL_CYCLE" && d.IsError);
    }

    [Fact]
    public void Validate_ReExportFromGlobal_IsUnsupported()
    {
        var context = ContextWith("caller", ("bank", "export function coinsOf() { return 1; }"));

        var diagnostics = ValidateWith(context, Script("export * from \"globals/bank\";"));

        var error = Assert.Single(diagnostics, d => d.Code == "SCRIPT_MODULE_UNSUPPORTED");
        Assert.True(error.IsError);
        Assert.StartsWith("Line 1, column", error.Message);
    }

    private const string ImportsCoinsOf = "import { coinsOf } from \"globals/bank\";\nexport default function handle() { return { ok: true, c: coinsOf() }; }";

    [Fact]
    public void GlobalSave_RemovingAnImportedExport_NamesTheBrokenImporter()
    {
        var importers = new[] { Importer("caller", ImportsCoinsOf) };
        var proposed = JsonSerializer.SerializeToElement(Script("export function ownerOf() { return 1; }", scope: "global"));

        var error = Assert.Single(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));

        Assert.Equal("GLOBAL_EXPORT_NOT_FOUND", error.Code);
        Assert.True(error.IsError);
        Assert.Equal("/script", error.Path);
        Assert.Contains("Saving \"bank\"", error.Message);
        Assert.Contains("\"caller\"", error.Message);
        Assert.Contains("\"coinsOf\"", error.Message);
    }

    [Fact]
    public void GlobalSave_StillExportingTheImport_BreaksNothing()
    {
        var importers = new[] { Importer("caller", ImportsCoinsOf) };
        var proposed = JsonSerializer.SerializeToElement(Script("export function coinsOf() { return 2; }", scope: "global"));

        Assert.Empty(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));
    }

    [Fact]
    public void GlobalSave_Disabled_BreaksItsImporterAtTheEnabledField()
    {
        var importers = new[] { Importer("caller", ImportsCoinsOf) };
        var disabled = Script("export function coinsOf() { return 1; }", scope: "global");
        disabled["enabled"] = false;

        var error = Assert.Single(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", JsonSerializer.SerializeToElement(disabled), importers, ContextWith("bank")));

        Assert.Equal("GLOBAL_NOT_FOUND", error.Code);
        Assert.Equal("/enabled", error.Path);
        Assert.Contains("\"caller\"", error.Message);
    }

    [Fact]
    public void GlobalSave_ChangedToPublic_BreaksItsImporterAtTheScopeField()
    {
        var importers = new[] { Importer("caller", ImportsCoinsOf) };
        var proposed = JsonSerializer.SerializeToElement(Script("export function coinsOf() { return 1; }", scope: "public"));

        var error = Assert.Single(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));

        Assert.Equal("GLOBAL_NOT_FOUND", error.Code);
        Assert.Equal("/scope", error.Path);
    }

    [Fact]
    public void GlobalSave_ImporterOfAnotherGlobal_IsNotReported()
    {
        var importers = new[] { Importer("caller", "import { x } from \"globals/other\";\nexport default function handle() { return { ok: true, x: x() }; }") };
        var proposed = JsonSerializer.SerializeToElement(Script("export function ownerOf() { return 1; }", scope: "global"));

        Assert.Empty(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));
    }

    [Fact]
    public void GlobalSave_ThatDoesNotCheck_IsLeftToItsOwnSave()
    {
        var importers = new[] { Importer("caller", ImportsCoinsOf) };
        var proposed = JsonSerializer.SerializeToElement(Script("export function (", scope: "global"));

        Assert.Empty(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));
    }

    [Fact]
    public void GlobalSave_ImporterWithAnUnrelatedBreak_ReportsOnlyTheSavedGlobal()
    {
        var importers = new[] { Importer("caller", "import { coinsOf } from \"globals/bank\";\nimport { x } from \"globals/other\";\nexport default function handle() { return { ok: true, c: coinsOf(), x: x() }; }") };
        var proposed = JsonSerializer.SerializeToElement(Script("export function ownerOf() { return 1; }", scope: "global"));

        var error = Assert.Single(NetworkStorageDefinitionValidator.ValidateGlobalImporters("bank", proposed, importers, ContextWith("bank")));

        Assert.Equal("GLOBAL_EXPORT_NOT_FOUND", error.Code);
        Assert.Contains("\"coinsOf\"", error.Message);
    }

    [Fact]
    public async Task OwnerSave_OfGlobalThatBreaksAnImporter_IsRefusedAndNamesTheImporter()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync("p", "bank", "bank", "GET", true,
            JsonSerializer.SerializeToElement(Script("export function coinsOf() { return 1; }", scope: "global")), null, 1, CancellationToken.None);
        await store.UpsertEndpointAsync("p", "caller", "caller", "GET", true,
            JsonSerializer.SerializeToElement(Script(ImportsCoinsOf)), null, 1, CancellationToken.None);
        var resources = await OwnerProjectResources.LoadAsync(store, "p", CancellationToken.None);
        var proposed = JsonSerializer.Serialize(new { id = "bank", scope = "global", runtime = "javascript", script = "export function renamed() { return 1; }" });

        var diagnostics = OwnerResourceSource.Check("endpoint", proposed, "bank", resources, 1_000_000,
            EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true), out _, out _);

        var error = Assert.Single(diagnostics, item => item.IsError);
        Assert.Equal("GLOBAL_EXPORT_NOT_FOUND", error.Code);
        Assert.Contains("\"caller\"", error.Message);
    }

    [Fact]
    public async Task OwnerSave_OfGlobalThatKeepsTheExport_IsAccepted()
    {
        var store = new InMemoryNetworkStorageStore();
        await store.UpsertEndpointAsync("p", "bank", "bank", "GET", true,
            JsonSerializer.SerializeToElement(Script("export function coinsOf() { return 1; }", scope: "global")), null, 1, CancellationToken.None);
        await store.UpsertEndpointAsync("p", "caller", "caller", "GET", true,
            JsonSerializer.SerializeToElement(Script(ImportsCoinsOf)), null, 1, CancellationToken.None);
        var resources = await OwnerProjectResources.LoadAsync(store, "p", CancellationToken.None);
        var proposed = JsonSerializer.Serialize(new { id = "bank", scope = "global", runtime = "javascript", script = "export function coinsOf() { return 2; }" });

        var diagnostics = OwnerResourceSource.Check("endpoint", proposed, "bank", resources, 1_000_000,
            EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true), out _, out _);

        Assert.DoesNotContain(diagnostics, item => item.IsError);
    }

    private static (string Id, JsonElement Definition) Importer(string id, string source) =>
        (id, JsonSerializer.SerializeToElement(Script(source)));

    private static DefinitionValidationContext ContextWith(string resourceId, params (string Id, string Source)[] globals) => new()
    {
        ResourceId = resourceId,
        ScriptRuntimes = EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true),
        GlobalModules = globals.ToDictionary(g => g.Id, g => ScriptModuleAnalysis.Analyze(g.Source)),
    };

    private static Dictionary<string, object?> Script(string script, string? scope = null)
    {
        var definition = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script };
        if (scope is not null) definition["scope"] = scope;
        return definition;
    }

    private static IReadOnlyList<DefinitionDiagnostic> ValidateWith(DefinitionValidationContext context, Dictionary<string, object?> endpoint) =>
        NetworkStorageDefinitionValidator.Validate("endpoint", JsonSerializer.SerializeToElement(endpoint), context);

    private static IReadOnlyList<DefinitionDiagnostic> Validate(Dictionary<string, object?> endpoint) =>
        NetworkStorageDefinitionValidator.Validate(
            "endpoint",
            JsonSerializer.SerializeToElement(endpoint),
            Context);
}

/// <summary>Where the request clock starts, and that save-time checks never throw a cancellation out of the runtime.</summary>
public sealed class ScriptBudgetSetupTests
{
    [Fact]
    public void Budget_DoesNotArmTheDeadline_UntilStart()
    {
        // A zero timeout would cancel at once if the clock started in the constructor, as it did before engine setup moved out of the budget.
        var limits = EndpointScriptLimits.Defaults with { Timeout = TimeSpan.Zero };
        using var budget = new ScriptRunBudget(limits, CancellationToken.None);

        Assert.False(budget.Token.IsCancellationRequested);

        budget.Start();

        Assert.True(budget.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Classify_JintExecutionCanceled_IsALimitNotAScriptError()
    {
        // Jint's ExecutionCanceledException is not an OperationCanceledException. A real engine whose token is already cancelled raises it.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var engine = new Jint.Engine(Jint.ConstraintsOptionsExtensions.CancellationToken(new Jint.Options(), cancelled.Token));
        var error = Record.Exception(() => engine.Execute("for (;;) {}"));

        Assert.IsType<Jint.Runtime.ExecutionCanceledException>(error);
        var outcome = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults).Classify(error!);
        Assert.Equal(EndpointScriptOutcomeKind.LimitExceeded, outcome.Kind);
    }

    [Fact]
    public async Task Check_UnderLoadedThreadPool_ReturnsNoDiagnosticInsteadOfThrowing()
    {
        var runtime = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults);
        using var stop = new CancellationTokenSource();
        var burners = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                }
            }))
            .ToArray();
        try
        {
            for (var i = 0; i < 3; i++)
                Assert.Empty(runtime.Check("export default function handle(req, ns) { return { ok: true }; }"));
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(burners);
        }
    }
}
