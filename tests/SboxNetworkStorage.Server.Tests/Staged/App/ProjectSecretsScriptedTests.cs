using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// <c>ns.secrets.get</c> through the executor: values reach the script, unset names read as null, names are
/// case-sensitive, a missing storage key fails the run with <c>SECRET_UNAVAILABLE</c>, and a secret value never
/// appears in the response, logs, trace, or owner detail.
/// </summary>
public sealed class ProjectSecretsScriptedTests
{
    private const string Value = "sk_live_9f8e7d6c5b4a";

    [Fact]
    public async Task Get_SetSecret_ReturnsValueToScriptButResponseCarriesOnlyTheToken()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              const key = ns.secrets.get("API_KEY");
              return { length: key.length, key: key };
            }
            """, secret: ("API_KEY", Value));

        var result = await Run(executor);

        Assert.True(result.Ok, result.OwnerDetail);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"length\":" + Value.Length, json);
        Assert.Contains("[secret:API_KEY]", json);
        Assert.DoesNotContain(Value, json);
    }

    [Fact]
    public async Task Get_UnsetName_ReturnsNull()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              return { missing: ns.secrets.get("NOPE") === null };
            }
            """, secret: ("API_KEY", Value));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains("\"missing\":true", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Get_NameIsCaseSensitive()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              return { lower: ns.secrets.get("api_key") === null };
            }
            """, secret: ("API_KEY", Value));

        var result = await Run(executor);

        Assert.Contains("\"lower\":true", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Get_InvalidName_FailsTheRun()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              ns.secrets.get("bad name");
              return { ok: true };
            }
            """, secret: ("API_KEY", Value));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.DoesNotContain(Value, JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Get_WithoutStorageKey_FailsWithSecretUnavailable()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              return { key: ns.secrets.get("API_KEY") };
            }
            """, secretsUnavailable: true);

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("SECRET_UNAVAILABLE", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Get_ValueNeverAppearsInLogsOrTrace()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              ns.log("key is " + ns.secrets.get("API_KEY"));
              return { ok: true };
            }
            """, secret: ("API_KEY", Value));
        var trace = new EndpointDryRunTrace();

        var result = await Run(executor, trace: trace);

        Assert.True(result.Ok, result.OwnerDetail);
        var log = Assert.Single(trace.ScriptLogs);
        Assert.Contains("[secret:API_KEY]", log);
        Assert.DoesNotContain(Value, log);
        Assert.DoesNotContain(Value, string.Join("\n", trace.Steps.Select(s => s.Result?.ToString())));
    }

    [Fact]
    public async Task Get_ValueNeverAppearsInFailureOwnerDetail()
    {
        var executor = Executor("""
            export default function handle(req, ns) {
              throw new Error("upstream rejected " + ns.secrets.get("API_KEY"));
            }
            """, secret: ("API_KEY", Value));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.DoesNotContain(Value, result.OwnerDetail ?? string.Empty);
        Assert.DoesNotContain(Value, JsonSerializer.Serialize(result.Body));
    }

    private static EndpointExecutor Executor(
        string script, (string Name, string Value)? secret = null, bool secretsUnavailable = false)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        var store = new ScriptedEndpointExecutorTests.FakeDataSource(definitions, new Dictionary<string, object?>())
        {
            SecretsUnavailable = secretsUnavailable,
        };
        if (secret is { } stored)
            store.Secrets[$"proj:{stored.Name}"] = stored.Value;
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults);
        return new EndpointExecutor(
            store,
            scriptRuntimes: [javaScript, new TypeScriptEndpointRuntime(javaScript)]);
    }

    private static async Task<EndpointExecutionResult> Run(EndpointExecutor executor, EndpointDryRunTrace? trace = null)
    {
        var result = await executor.TryExecuteAsync(
            "proj", "script",
            input: new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, trace: trace);
        return result!;
    }
}
