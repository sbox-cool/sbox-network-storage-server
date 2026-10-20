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

    [Fact]
    public async Task StructuredLogs_RedactBeforeJsonEscaping()
    {
        const string credential = "quote\"slash\\newline\nsecret";
        var executor = Executor("""
            export default function handle(req, ns) {
              const key = ns.secrets.get("API_KEY");
              ns.log({ key });
              throw new Error("failed");
            }
            """, secret: ("API_KEY", credential));
        var trace = new EndpointDryRunTrace();
        var result = await Run(executor, trace);
        Assert.False(result.Ok);
        var logged = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single(trace.ScriptLogs))!;
        Assert.Equal("[secret:API_KEY]", logged["key"]);
        Assert.DoesNotContain(JsonSerializer.Serialize(credential), result.OwnerDetail!);
    }

    [Fact]
    public async Task FailureCode_CannotCarryACredential()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              ns.fail(ns.secrets.get("API_KEY"), "request failed", 409);
            }
            """, secret: ("API_KEY", Value)));
        Assert.Equal(409, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("SECRET_ERROR_REDACTED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(Value, body.GetRawText());
    }

    [Fact]
    public async Task SecretPropertyNames_AreRedactedWithoutLosingTheirValues()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              return { [ns.secrets.get("API_KEY")]: "present" };
            }
            """, secret: ("API_KEY", Value)));
        Assert.True(result.Ok, result.OwnerDetail);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal("present", body.GetProperty("[secret:API_KEY]").GetString());
        Assert.False(body.TryGetProperty(Value, out _));
    }

    [Fact]
    public async Task CollidingRedactedProperties_FailClosed()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              return { [ns.secrets.get("API_KEY")]: "secret", "[secret:API_KEY]": "literal" };
            }
            """, secret: ("API_KEY", Value)));
        Assert.False(result.Ok);
        Assert.Contains("SECRET_OUTPUT_REJECTED", JsonSerializer.Serialize(result.Body));
    }

    [Theory]
    [InlineData("ns.records.put('players', 'steam1', {token:key})")]
    [InlineData("ns.records.put('players', key, {ok:true})")]
    public async Task SecretBearingRecordWrites_AreRefusedBeforePersistenceOrPreview(string write)
    {
        var result = await Run(Executor(
            "export default function handle(req,ns) { const key=ns.secrets.get('API_KEY'); " + write + "; return {}; }",
            secret: ("API_KEY", Value)), trace: new EndpointDryRunTrace());
        Assert.False(result.Ok);
        Assert.Equal(400, result.Status);
        Assert.Contains("SECRET_WRITE_FORBIDDEN", JsonSerializer.Serialize(result.Body));
        Assert.DoesNotContain(Value, JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task BlockedFetchHost_IsRedactedBeforeItReachesOwnerDiagnostics()
    {
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              const key = ns.secrets.get("API_KEY");
              if (key === null) return null;
              try { ns.fetch("https://" + key + ".example.com/"); } catch (_) {}
              return {};
            }
            """, secret: ("API_KEY", "credential")));
        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal("[secret:API_KEY].example.com", Assert.Single(result.FetchBlocked!).Host);
    }

    [Fact]
    public async Task Fetch_WaitsForSecretResolutionBeforeSendingTheSideEffect()
    {
        var transport = new RecordingTransport();
        var result = await Run(Executor("""
            export default function handle(req, ns) {
              const key = ns.secrets.get("API_KEY");
              const response = ns.fetch("https://api.example.com/action", {
                method:"POST", headers:{Authorization:"Bearer "+key}, body:{action:"create"}
              });
              return {status:response.status};
            }
            """, secret: ("API_KEY", Value), transport: transport));
        Assert.True(result.Ok, result.OwnerDetail);
        var sent = Assert.Single(transport.Requests);
        Assert.Equal("Bearer " + Value, Assert.Single(sent.Headers, pair => pair.Key == "Authorization").Value);
        Assert.Contains("\"status\":200", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public void Redaction_DoesNotRewriteItsOwnReplacementTokens()
    {
        var redact = new SecretRedactor(new Dictionary<string, string?> { ["BEE"]="cat", ["DOG"]="BEE" });
        Assert.Equal("[secret:BEE] [secret:DOG]", redact.Text("cat BEE"));
    }

    private sealed class RecordingTransport : IScriptFetchTransport
    {
        public bool AllowPrivateNetworks => false;
        public List<ScriptFetchRequest> Requests { get; } = [];
        public Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(ScriptFetchResult.Ok(new ScriptFetchResponse(200, new Dictionary<string,string>(), "{}")));
        }
    }

    private static EndpointExecutor Executor(
        string script, (string Name, string Value)? secret = null, bool secretsUnavailable = false, IScriptFetchTransport? transport = null)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        var store = new ScriptedEndpointExecutorTests.FakeDataSource(definitions, new Dictionary<string, object?>())
        {
            SecretsUnavailable = secretsUnavailable,
            FetchPolicy = transport is null ? EndpointFetchPolicy.Closed : new(["api.example.com"], false),
        };
        if (secret is { } stored)
            store.Secrets[$"proj:{stored.Name}"] = stored.Value;
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults);
        return new EndpointExecutor(
            store,
            scriptRuntimes: [javaScript, new TypeScriptEndpointRuntime(javaScript)], fetchTransport: transport);
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
