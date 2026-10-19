using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>Whole-endpoint JavaScript handlers (<c>runtime: javascript</c>) run through the same executor as YAML.</summary>
public sealed class ScriptedEndpointExecutorTests
{
    [Fact]
    public async Task Script_HappyPath_ReadsRecordAndReturnsBody()
    {
        var script = """
            export default function handle(req, ns) {
              const player = ns.records.get("players", req.playerSteamId);
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

        var scripted = Executor("export default function handle(req, ns) { return { ok: true, coins: ns.records.get(\"players\", req.playerSteamId).coins }; }", records);
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

    private static async Task<EndpointExecutionResult> Run(
        EndpointExecutor executor, string endpoint = "script", Dictionary<string, object?>? input = null)
    {
        var result = await executor.TryExecuteAsync(
            "proj", endpoint,
            input: input ?? new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None);
        return result!;
    }

    private static Dictionary<string, object?> PlayerRecord(double coins) =>
        new() { ["proj:players:steam1"] = new Dictionary<string, object?> { ["coins"] = coins } };

    /// <summary>Minimal store: endpoint definitions by "proj:ep:slug" and records by "proj:collection:key".</summary>
    private sealed class FakeDataSource : IEndpointDataSource
    {
        private readonly Dictionary<string, object?> _definitions;
        private readonly Dictionary<string, object?> _records;

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
            var prefix = $"{projectId}:{collectionId}:";
            var results = _records.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
            return Task.FromResult<IReadOnlyList<object?>>(results);
        }

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => Task.FromResult<Dictionary<string, object?>?>(null);

        public Task<IReadOnlyList<string>> ListWorkflowIdsAsync(string projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListEndpointIdsAsync(string projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => throw new InvalidOperationException("These tests never write.");

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> IsWebhookOutboxEnabledAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => Task.CompletedTask;
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

    private static IReadOnlyList<DefinitionDiagnostic> Validate(Dictionary<string, object?> endpoint) =>
        NetworkStorageDefinitionValidator.Validate(
            "endpoint",
            JsonSerializer.SerializeToElement(endpoint),
            Context);
}
