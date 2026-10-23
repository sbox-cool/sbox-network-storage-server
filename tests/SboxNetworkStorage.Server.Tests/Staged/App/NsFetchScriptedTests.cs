using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// <c>ns.fetch</c> through the executor: the allow-list and address rules, the per-execution call limit, the script
/// time budget, the catchable <c>FETCH_*</c> errors, the 502 rule for uncaught fetch errors (only errors the prelude marked), and the endpoint trace.
/// </summary>
public sealed class NsFetchScriptedTests
{
    [Fact]
    public async Task Fetch_AllowedExactHost_ReturnsResponseAndTracesOneFetchStep()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              const r = ns.fetch("https://api.example.com/prices");
              return { ok: true, status: r.status, coins: r.json.coins };
            }
            """, transport, Hosts("api.example.com"));
        var trace = new EndpointDryRunTrace();

        var result = await Run(executor, trace: trace);

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal(200, result.Status);
        Assert.Contains("\"status\":200", JsonSerializer.Serialize(result.Body));
        Assert.Contains("\"coins\":9", JsonSerializer.Serialize(result.Body));
        var request = Assert.Single(transport.Requests);
        Assert.Equal("https://api.example.com/prices", request.Url);
        Assert.Equal("GET", request.Method);
        var step = Assert.Single(trace.Steps, s => s.Type == "fetch");
        Assert.Equal("fetch#1", step.Id);
        Assert.True(step.Passed);
        Assert.Empty(result.FetchBlocked!);
    }

    [Fact]
    public async Task Fetch_WildcardEntry_MatchesSubdomainsButNotTheApex()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              const out = {};
              try { out.sub = ns.fetch("https://eu.example.com/x").status; } catch (e) { out.sub = e.code; }
              try { ns.fetch("https://example.com/x"); out.apex = "sent"; } catch (e) { out.apex = e.code; }
              return out;
            }
            """, transport, Hosts("*.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"sub\":200", json);
        Assert.Contains("\"apex\":\"FETCH_BLOCKED\"", json);
        Assert.Single(transport.Requests);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal("example.com", blocked.Host);
        Assert.Equal("host_not_allowed", blocked.Reason);
    }

    [Fact]
    public async Task Fetch_HostNotOnAllowList_IsCaughtAsFetchBlockedAndNeverSent()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode("ns.fetch('https://evil.example.net/steal')"), transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains("\"code\":\"FETCH_BLOCKED\"", JsonSerializer.Serialize(result.Body));
        Assert.Empty(transport.Requests);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal("evil.example.net", blocked.Host);
        Assert.Equal("host_not_allowed", blocked.Reason);
    }

    [Fact]
    public async Task Fetch_ProvisionalBlockedBranches_DoNotAppearInTheFinalTrace()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              const player = ns.records.get("players", req.player.steamId);
              if (!player) {
                try { ns.fetch("https://discarded.example.net/first"); } catch {}
                try { ns.fetch("https://discarded.example.net/second"); } catch {}
              }
              return { coins: player ? player.coins : 0 };
            }
            """, transport, Hosts("api.example.com"), records: PlayerRecord(250d));
        var trace = new EndpointDryRunTrace();

        var result = await Run(executor, trace: trace);

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal(250, JsonSerializer.SerializeToElement(result.Body).GetProperty("coins").GetInt32());
        Assert.Empty(trace.Steps.Where(step => step.Type == "fetch"));
        Assert.Empty(result.FetchBlocked!);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Fetch_RedirectRefusal_TraceNamesTheBlockedHop()
    {
        var transport = new FakeFetchTransport(_ => ScriptFetchResult.Fail(
            new ScriptFetchFailure("FETCH_BLOCKED", "host_not_allowed", "redirect.example.net")));
        var executor = Executor(CatchCode("ns.fetch('https://api.example.com/start')"), transport, Hosts("api.example.com"));
        var trace = new EndpointDryRunTrace();

        var result = await Run(executor, trace: trace);

        Assert.True(result.Ok, result.OwnerDetail);
        var step = Assert.Single(trace.Steps, step => step.Type == "fetch");
        Assert.False(step.Passed);
        var details = JsonSerializer.SerializeToElement(step.Result);
        Assert.Equal("redirect.example.net", details.GetProperty("host").GetString());
        Assert.Equal("FETCH_BLOCKED", details.GetProperty("error").GetString());
        Assert.Equal("host_not_allowed", details.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("http://10.0.0.1/admin", "10.0.0.1", "private_address")]
    [InlineData("http://169.254.169.254/latest/meta-data", "169.254.169.254", "metadata_address")]
    [InlineData("http://240.0.0.1/", "240.0.0.1", "reserved_address")]
    public async Task Fetch_BlockedAddressLiteral_IsRefusedEvenWhenAllowAllPublic(string url, string host, string reason)
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode($"ns.fetch('{url}')"), transport, AllPublic());

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains("\"code\":\"FETCH_BLOCKED\"", JsonSerializer.Serialize(result.Body));
        Assert.Empty(transport.Requests);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal(host, blocked.Host);
        Assert.Equal(reason, blocked.Reason);
    }

    [Fact]
    public async Task Fetch_PrivateLiteralOffAllowList_ReportsAddressReasonBeforeAllowListReason()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode("ns.fetch('http://10.0.0.1/')"), transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Empty(transport.Requests);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal("private_address", blocked.Reason);
    }

    [Fact]
    public async Task Fetch_IpLiteralOnAllowList_StillGoesThroughAddressRules()
    {
        // 203.0.113.0/24 is reserved. Listing the literal exactly does not make it reachable.
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode("ns.fetch('http://203.0.113.7/')"), transport, Hosts("203.0.113.7"));

        var result = await Run(executor);

        Assert.Empty(transport.Requests);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal("reserved_address", blocked.Reason);
    }

    [Fact]
    public async Task Fetch_PublicIpLiteralOnAllowList_ReachesTransport()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode("ns.fetch('http://93.184.216.34/')"), transport, Hosts("93.184.216.34"));

        var result = await Run(executor);

        Assert.Contains("\"code\":\"none\"", JsonSerializer.Serialize(result.Body));
        Assert.Equal("http://93.184.216.34/", Assert.Single(transport.Requests).Url);
        Assert.Empty(result.FetchBlocked!);
    }

    [Fact]
    public async Task Fetch_TransportAllowingPrivateNetworks_LetsPrivateLiteralThrough()
    {
        var transport = new FakeFetchTransport { AllowPrivateNetworks = true };
        var executor = Executor(CatchCode("ns.fetch('http://10.0.0.1/')"), transport, AllPublic());

        var result = await Run(executor);

        Assert.Contains("\"code\":\"none\"", JsonSerializer.Serialize(result.Body));
        Assert.Equal("http://10.0.0.1/", Assert.Single(transport.Requests).Url);
    }

    [Fact]
    public async Task Fetch_AllowAllPublic_SendsToAnyPublicHost()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode("ns.fetch('https://anything.example.org/data')"), transport, AllPublic());

        var result = await Run(executor);

        Assert.Contains("\"code\":\"none\"", JsonSerializer.Serialize(result.Body));
        Assert.Equal("https://anything.example.org/data", Assert.Single(transport.Requests).Url);
    }

    [Fact]
    public async Task Fetch_SixthCallInOneExecution_IsCaughtAsLimitExceededAndOnlyFiveAreSent()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              const codes = [];
              for (let i = 1; i <= 6; i++) {
                try { ns.fetch("https://api.example.com/" + i); } catch (e) { codes.push(e.code); }
              }
              return { codes };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains("FETCH_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
        Assert.Equal(5, transport.Calls);
    }

    [Fact]
    public async Task Fetch_CallBuiltFromAnEarlierCallsResult_IsSentAgainWithinOnePass()
    {
        var transport = new FakeFetchTransport(request => ScriptFetchResult.Ok(new ScriptFetchResponse(200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            request.Url.EndsWith("/token", StringComparison.Ordinal)
                ? """{"token":"abc"}"""
                : JsonSerializer.Serialize(new { url = request.Url }))));
        var executor = Executor("""
            export default function handle(req, ns) {
              const a = ns.fetch("https://api.example.com/token");
              const b = ns.fetch("https://api.example.com/data?t=" + (a ? a.json.token : ""));
              if (a === null || b === null) return null;
              return { data: b.json.url };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal(200, result.Status);
        Assert.Contains("data?t=abc", JsonSerializer.Serialize(result.Body));
        Assert.Equal("https://api.example.com/data?t=abc", transport.Requests[^1].Url);
    }

    [Fact]
    public async Task Fetch_CallsResentWithChangedArgumentsWithinOnePass_StillCountTowardTheLimit()
    {
        var transport = new FakeFetchTransport(request => ScriptFetchResult.Ok(new ScriptFetchResponse(200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            JsonSerializer.Serialize(new { n = request.Url.Length }))));
        var executor = Executor("""
            export default function handle(req, ns) {
              let previous = "";
              for (let i = 1; i <= 4; i++) {
                const r = ns.fetch("https://api.example.com/" + i + "?p=" + previous);
                previous = r === null ? "" : String(r.json.n);
              }
              return { ok: true };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(502, result.Status);
        Assert.Contains("FETCH_LIMIT_EXCEEDED", JsonSerializer.Serialize(result.Body));
        Assert.Equal(ScriptFetchSession.MaxRequestsPerExecution, transport.Calls);
    }

    [Fact]
    public async Task Fetch_SlowTransport_WaitDoesNotCountAgainstScriptTimeLimit()
    {
        // The transport takes 1.5 s, longer than the 1 s script limit. The wait runs outside the script's budget.
        var transport = new FakeFetchTransport(delay: TimeSpan.FromMilliseconds(1500));
        var executor = Executor("""
            export default function handle(req, ns) {
              const r = ns.fetch("https://api.example.com/slow");
              return { ok: true, status: r.status };
            }
            """, transport, Hosts("api.example.com"),
            limits: EndpointScriptLimits.Defaults with { Timeout = TimeSpan.FromSeconds(1) });
        var clock = Stopwatch.StartNew();

        var result = await Run(executor);
        clock.Stop();

        Assert.True(result.Ok, result.OwnerDetail);
        Assert.Equal(200, result.Status);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1500));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("ns.fetch('ftp://api.example.com/')")]
    [InlineData("ns.fetch('https://user:pw@api.example.com/')")]
    [InlineData("ns.fetch('https://api.example.com/', { method: 'TRACE' })")]
    [InlineData("ns.fetch('https://api.example.com/', { method: 'GET', body: 'x' })")]
    [InlineData("ns.fetch('https://api.example.com/', { retries: 3 })")]
    [InlineData("ns.fetch('https://api.example.com/', { headers: { 'proxy-authorization': 'x' } })")]
    public async Task Fetch_MalformedRequest_IsCaughtAsInvalidRequestAndNeverSent(string call)
    {
        var transport = new FakeFetchTransport();
        var executor = Executor(CatchCode(call), transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains("\"code\":\"FETCH_INVALID_REQUEST\"", JsonSerializer.Serialize(result.Body));
        Assert.Empty(transport.Requests);
        Assert.Empty(result.FetchBlocked!);
    }

    [Theory]
    [InlineData("FETCH_TIMEOUT")]
    [InlineData("FETCH_RESPONSE_TOO_LARGE")]
    [InlineData("FETCH_FAILED")]
    public async Task Fetch_TransportFailure_IsCatchableWithItsCode(string code)
    {
        var transport = new FakeFetchTransport(_ => ScriptFetchResult.Fail(new ScriptFetchFailure(code, "failed", "api.example.com")));
        var executor = Executor(CatchCode("ns.fetch('https://api.example.com/')"), transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        Assert.Contains($"\"code\":\"{code}\"", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Fetch_TransportRefusesResolvedPrivateAddress_RecordedAsBlockedCall()
    {
        // The name passed the allow-list, then resolved to a private address. The transport refuses it.
        var transport = new FakeFetchTransport(_ => ScriptFetchResult.Fail(
            new ScriptFetchFailure("FETCH_BLOCKED", "private_address", "rebind.example.net")));
        var executor = Executor(CatchCode("ns.fetch('https://rebind.example.net/')"), transport, Hosts("rebind.example.net"));

        var result = await Run(executor);

        Assert.Contains("\"code\":\"FETCH_BLOCKED\"", JsonSerializer.Serialize(result.Body));
        Assert.Equal(1, transport.Calls);
        var blocked = Assert.Single(result.FetchBlocked!);
        Assert.Equal("rebind.example.net", blocked.Host);
        Assert.Equal("private_address", blocked.Reason);
    }

    [Fact]
    public async Task Fetch_UncaughtRefusal_Returns502WithTheFetchCode()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              return { ok: true, status: ns.fetch("https://evil.example.net/").status };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Status);
        Assert.Contains("\"code\":\"FETCH_BLOCKED\"", JsonSerializer.Serialize(result.Body));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("FETCH_TIMEOUT")]
    [InlineData("FETCH_RESPONSE_TOO_LARGE")]
    [InlineData("FETCH_FAILED")]
    public async Task Fetch_UncaughtTransportFailure_Returns502WithItsCode(string code)
    {
        var transport = new FakeFetchTransport(_ => ScriptFetchResult.Fail(new ScriptFetchFailure(code, "failed", "api.example.com")));
        var executor = Executor("""
            export default function handle(req, ns) {
              return { ok: true, status: ns.fetch("https://api.example.com/").status };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Status);
        Assert.Contains($"\"code\":\"{code}\"", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Fetch_CaughtRefusalThenUnrelatedError_Returns500ScriptError()
    {
        // The fetch error was caught and not rethrown, so the escaping error is not a fetch error.
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              try { ns.fetch("https://evil.example.net/"); } catch (e) { }
              throw new Error("unrelated failure");
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("SCRIPT_ERROR", json);
        Assert.DoesNotContain("FETCH_", json);
    }

    [Fact]
    public async Task Fetch_CaughtRefusalRethrown_Returns502WithTheFetchCode()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              try { ns.fetch("https://evil.example.net/"); } catch (e) { throw e; }
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Status);
        Assert.Contains("\"code\":\"FETCH_BLOCKED\"", JsonSerializer.Serialize(result.Body));
    }

    [Theory]
    [InlineData("throw Object.assign(new Error('boom'), { code: 'FETCH_BLOCKED' });")]
    [InlineData("throw { code: 'FETCH_BLOCKED', message: 'boom' };")]
    public async Task Fetch_ScriptOwnThrowWithFetchCode_IsPlainScriptError(string statement)
    {
        var transport = new FakeFetchTransport();
        var executor = Executor($$"""
            export default function handle(req, ns) {
              {{statement}}
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("SCRIPT_ERROR", JsonSerializer.Serialize(result.Body));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Fetch_RethrownAsNewErrorWithTheSameCode_IsPlainScriptError()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              try { ns.fetch("https://evil.example.net/"); }
              catch (e) { const copy = new Error(e.message); copy.code = e.code; throw copy; }
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Contains("SCRIPT_ERROR", JsonSerializer.Serialize(result.Body));
    }

    [Fact]
    public async Task Fetch_ErrorMarkerIsNotEnumerable()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              try { ns.fetch("https://evil.example.net/"); }
              catch (e) { return { keys: Object.keys(e) }; }
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.Equal(200, result.Status);
        var json = JsonSerializer.Serialize(result.Body);
        Assert.Contains("\"keys\"", json);
        Assert.DoesNotContain("nsFetchFailure", json);
    }

    [Fact]
    public async Task Fetch_NotCalledByScript_ResultMatchesExecutorWithoutFetchWiring()
    {
        var script = """
            export default function handle(req, ns) {
              const player = ns.records.get("players", req.player.steamId);
              return { ok: true, coins: player.coins + req.input.price };
            }
            """;
        var input = new Dictionary<string, object?> { ["price"] = 5d };
        var transport = new FakeFetchTransport();

        var wired = await Run(Executor(script, transport, Hosts("api.example.com"), records: PlayerRecord(250d)), input);
        var unwired = await Run(Executor(script, null, EndpointFetchPolicy.Closed, records: PlayerRecord(250d)), input);

        Assert.True(wired.Ok, wired.OwnerDetail);
        Assert.Contains("\"coins\":255", JsonSerializer.Serialize(wired.Body));
        Assert.Equal(JsonSerializer.Serialize(unwired.Body), JsonSerializer.Serialize(wired.Body));
        Assert.Empty(transport.Requests);
        Assert.Empty(wired.FetchBlocked!);
    }

    [Fact]
    public async Task Fetch_JsonBody_IsSentWithJsonContentTypeAndUppercaseMethod()
    {
        var transport = new FakeFetchTransport();
        var executor = Executor("""
            export default function handle(req, ns) {
              const r = ns.fetch("https://api.example.com/orders", { method: "post", body: { amount: 5 } });
              return { ok: true, status: r.status };
            }
            """, transport, Hosts("api.example.com"));

        var result = await Run(executor);

        Assert.True(result.Ok, result.OwnerDetail);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Contains(request.Headers, header => header.Key == "content-type" && header.Value == "application/json");
        Assert.Contains("\"amount\":5", Encoding.UTF8.GetString(request.Body!));
    }

    /// <summary>A handler that makes one <c>ns.fetch</c> call, then returns the code of the error it caught, or "none".</summary>
    private static string CatchCode(string call) => $$"""
        export default function handle(req, ns) {
          try { {{call}}; return { code: "none" }; }
          catch (e) { return { code: e.code }; }
        }
        """;

    private static EndpointFetchPolicy Hosts(params string[] hosts) => new(hosts, AllowAllPublic: false);

    private static EndpointFetchPolicy AllPublic() => new(Array.Empty<string>(), AllowAllPublic: true);

    private static EndpointExecutor Executor(
        string script, IScriptFetchTransport? transport, EndpointFetchPolicy policy,
        Dictionary<string, object?>? records = null, EndpointScriptLimits? limits = null)
    {
        var definitions = new Dictionary<string, object?>
        {
            ["proj:ep:script"] = new Dictionary<string, object?> { ["runtime"] = "javascript", ["script"] = script },
        };
        var store = new ScriptedEndpointExecutorTests.FakeDataSource(definitions, records ?? new Dictionary<string, object?>());
        var javaScript = new JavaScriptEndpointRuntime(enabled: true, limits ?? EndpointScriptLimits.Defaults);
        return new EndpointExecutor(
            new PolicyDataSource(store, policy),
            scriptRuntimes: [javaScript, new TypeScriptEndpointRuntime(javaScript)],
            fetchTransport: transport);
    }

    private static async Task<EndpointExecutionResult> Run(
        EndpointExecutor executor, Dictionary<string, object?>? input = null, EndpointDryRunTrace? trace = null)
    {
        var result = await executor.TryExecuteAsync(
            "proj", "script",
            input: input ?? new Dictionary<string, object?>(), steamId: "steam1", userId: "u1",
            gameValues: new Dictionary<string, object?>(),
            hasSecretKey: false, isDedicatedServer: false, CancellationToken.None, trace: trace);
        return result!;
    }

    private static Dictionary<string, object?> PlayerRecord(double coins) =>
        new() { ["proj:players:steam1"] = new Dictionary<string, object?> { ["coins"] = coins } };

    /// <summary>
    /// The shared fake store is sealed and always answers the closed fetch policy. This wrapper delegates every
    /// member to it and answers the policy under test.
    /// </summary>
    private sealed class PolicyDataSource(IEndpointDataSource inner, EndpointFetchPolicy policy) : IEndpointDataSource
    {
        public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
            => inner.ReadEndpointDefinitionAsync(projectId, endpointSlug, ct);

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
            => inner.ListCollectionsAsync(projectId, ct);

        public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
            => inner.ReadRecordAsync(projectId, collectionId, key, ct);

        public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
            => inner.ScanCollectionAsync(projectId, collectionId, ct);

        public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
            => inner.ReadWorkflowDefinitionAsync(projectId, workflowId, ct);

        public Task<IReadOnlyList<string>> ListWorkflowIdsAsync(string projectId, CancellationToken ct)
            => inner.ListWorkflowIdsAsync(projectId, ct);

        public Task<IReadOnlyList<string>> ListEndpointIdsAsync(string projectId, CancellationToken ct)
            => inner.ListEndpointIdsAsync(projectId, ct);

        public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct)
            => inner.BeginWriteTransactionAsync(ct);

        public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
            => inner.ReadGlobalRecordAsync(projectId, collectionId, recordId, ct);

        public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId,
            IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
            => inner.WriteGlobalRecordAsync(projectId, collectionId, recordId, payload, ct);

        public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct)
            => inner.IsLegacyPlayerProjectionsEnabledAsync(projectId, ct);

        public Task<bool> IsWebhookOutboxEnabledAsync(string projectId, CancellationToken ct)
            => inner.IsWebhookOutboxEnabledAsync(projectId, ct);

        public Task<EndpointFetchPolicy> GetEndpointFetchPolicyAsync(string projectId, CancellationToken ct)
            => Task.FromResult(policy);
    }

    /// <summary>Stands in for the HTTP transport: records each request and answers with a canned result.</summary>
    private sealed class FakeFetchTransport(
        Func<ScriptFetchRequest, ScriptFetchResult>? answer = null, TimeSpan? delay = null) : IScriptFetchTransport
    {
        public List<ScriptFetchRequest> Requests { get; } = [];

        public int Calls => Requests.Count;

        public bool AllowPrivateNetworks { get; init; }

        public async Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct)
        {
            Requests.Add(request);
            if (delay is { } wait)
                await Task.Delay(wait, ct);
            return answer?.Invoke(request) ?? ScriptFetchResult.Ok(OkResponse());
        }

        private static ScriptFetchResponse OkResponse() => new(
            200,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
            """{"ok":true,"coins":9}""");
    }
}
