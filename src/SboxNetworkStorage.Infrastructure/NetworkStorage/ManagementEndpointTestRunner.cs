using System.Diagnostics;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dry-run endpoint tests shared by the management API (<c>test-endpoint</c>, <c>run-tests</c>)
/// and the owner dashboard. Runs the real native executor with live serving disabled, so record
/// writes stay in memory, webhooks are skipped and nothing durable changes. Result shapes follow
/// what the editor Sync Tool test window parses: <c>result{ok,status,body,timing}</c>,
/// <c>steps[]</c>, <c>pendingWrites[]</c>, <c>warnings[]</c>, <c>expectation{passed,reason}</c>.
/// </summary>
public sealed class ManagementEndpointTestRunner(
    INetworkStorageStore store,
    NativeEndpointShadowExecutor executor,
    IQueryValuesContextProvider? valuesProvider)
{
    public const string DefaultSteamId = "76561198000000000";

    /// <summary>A test as stored in <c>tests.json</c> or sent to <c>test-endpoint</c>.</summary>
    public sealed record TestSpec(string? Id, string? Name, string Slug, JsonElement Input, string SteamId, bool AsServer,
        string ExpectOutcome, int? ExpectStatus);

    public sealed record TestOutcome(bool Found, bool Passed, Dictionary<string, object?> Body);

    public static TestSpec? ReadSpec(JsonElement test)
    {
        if (test.ValueKind != JsonValueKind.Object) return null;
        var slug = Text(test, "endpoint") ?? Text(test, "slug");
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var input = test.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object
            ? inputElement.Clone()
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        string outcome = "pass";
        int? status = null;
        if (test.TryGetProperty("expect", out var expect) && expect.ValueKind == JsonValueKind.Object)
        {
            outcome = Text(expect, "outcome") is "fail" or "any" ? Text(expect, "outcome")! : "pass";
            if (expect.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var expected)) status = expected;
        }
        var steamId = Text(test, "steamId");
        return new TestSpec(Text(test, "id"), Text(test, "name"), slug, input,
            string.IsNullOrWhiteSpace(steamId) ? DefaultSteamId : steamId.Trim(),
            test.TryGetProperty("asServer", out var server) && server.ValueKind == JsonValueKind.True, outcome, status);
    }

    /// <summary>Executes one test. <see cref="TestOutcome.Found"/> is false when the endpoint does not exist.</summary>
    public async Task<TestOutcome> RunAsync(string projectId, long ownerUserId, string? playerKeyMode, TestSpec spec, CancellationToken ct)
    {
        var row = await FindEndpointAsync(projectId, spec.Slug, ct);
        if (row is null) return new TestOutcome(false, false, new Dictionary<string, object?>());
        var definition = Definition(row.Value);
        var method = Text(row.Value, "method") ?? (definition is { } def ? Text(def, "method") : null) ?? "POST";
        var warnings = Warnings(row.Value, definition);
        var values = valuesProvider is null
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()
            : await valuesProvider.GetValuesAsync(projectId, ct);

        var trace = new EndpointDryRunTrace();
        var clock = Stopwatch.StartNew();
        EndpointExecutionResult? execution;
        string? failure = null;
        try
        {
            execution = await executor.TryExecuteAsync(projectId, spec.Slug, ToInput(spec.Input), spec.SteamId,
                ownerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), values,
                hasSecretKey: spec.AsServer, isDedicatedServer: spec.AsServer, ct,
                liveServe: false, playerKeyMode: playerKeyMode, enforcePublicAccessGates: false, trace: trace);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            execution = null;
            failure = error.Message;
        }
        clock.Stop();
        var timing = new Dictionary<string, object?> { ["total"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1) };

        var status = execution?.Status ?? (failure is null ? 501 : 500);
        var ok = execution?.Ok == true;
        var body = execution?.Body ?? new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = failure is null ? "ENDPOINT_NOT_SUPPORTED" : "ENDPOINT_EXECUTION_FAILED",
                ["message"] = failure ?? "The native executor does not support this endpoint definition.",
            },
        };
        var (passed, reason) = Evaluate(spec, ok, status, body);
        var response = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["dryRun"] = true,
            ["id"] = spec.Id,
            ["name"] = spec.Name ?? $"{spec.Slug} test",
            ["endpoint"] = spec.Slug,
            ["method"] = method,
            ["steamId"] = spec.SteamId,
            ["passed"] = passed,
            ["reason"] = passed ? null : reason,
            ["timing"] = timing,
            ["result"] = new Dictionary<string, object?> { ["ok"] = ok, ["status"] = status, ["body"] = body, ["timing"] = timing },
            ["steps"] = trace.Steps.Select(step => new Dictionary<string, object?>
            {
                ["id"] = step.Id, ["type"] = step.Type, ["result"] = step.Result, ["passed"] = step.Passed,
            }).ToList(),
            ["pendingWrites"] = trace.PendingWrites.Select(write => new Dictionary<string, object?>
            {
                ["collection"] = write.Collection, ["key"] = write.Key, ["op"] = write.IsDelete ? "delete" : "write", ["data"] = write.Data,
            }).ToList(),
            ["warnings"] = warnings,
            ["expectation"] = new Dictionary<string, object?> { ["outcome"] = spec.ExpectOutcome, ["status"] = spec.ExpectStatus, ["passed"] = passed, ["reason"] = passed ? "" : reason },
        };
        return new TestOutcome(true, passed, response);
    }

    /// <summary>Runs every saved test in order. Missing endpoints count as failures.</summary>
    public async Task<Dictionary<string, object?>> RunSavedAsync(string projectId, long ownerUserId, string? playerKeyMode, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();
        foreach (var test in await ManagementProjectObjects.ReadTestsAsync(store, ownerUserId, projectId, ct))
        {
            var spec = ReadSpec(test);
            if (spec is null)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["id"] = Text(test, "id"), ["name"] = Text(test, "name") ?? "Unnamed test", ["endpoint"] = null,
                    ["passed"] = false, ["reason"] = "The test does not name an endpoint.", ["warnings"] = Array.Empty<string>(),
                });
                continue;
            }
            var outcome = await RunAsync(projectId, ownerUserId, playerKeyMode, spec, ct);
            if (!outcome.Found)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["id"] = spec.Id, ["name"] = spec.Name ?? spec.Slug, ["endpoint"] = spec.Slug,
                    ["passed"] = false, ["reason"] = $"Endpoint '{spec.Slug}' was not found.", ["warnings"] = Array.Empty<string>(),
                });
                continue;
            }
            results.Add(outcome.Body);
        }
        var passed = results.Count(result => result.GetValueOrDefault("passed") is true);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["dryRun"] = true,
            ["total"] = results.Count,
            ["passed"] = passed,
            ["failed"] = results.Count - passed,
            ["results"] = results,
            ["summary"] = new Dictionary<string, object?> { ["total"] = results.Count, ["passed"] = passed, ["failed"] = results.Count - passed },
        };
    }

    private static (bool Passed, string Reason) Evaluate(TestSpec spec, bool ok, int status, object? body)
    {
        var message = ErrorMessage(body);
        var detail = message is null ? $"HTTP {status}" : $"HTTP {status}: {message}";
        if (spec.ExpectStatus is { } expected && expected != status)
            return (false, $"Expected HTTP {expected}, got {detail}.");
        return spec.ExpectOutcome switch
        {
            "any" => (true, ""),
            "fail" => ok ? (false, $"Expected a rejection, got {detail}.") : (true, ""),
            _ => ok ? (true, "") : (false, $"Expected success, got {detail}."),
        };
    }

    private static string? ErrorMessage(object? body)
    {
        if (body is not IReadOnlyDictionary<string, object?> map) return null;
        if (map.GetValueOrDefault("message") is string message) return message;
        return map.GetValueOrDefault("error") switch
        {
            IReadOnlyDictionary<string, object?> error => error.GetValueOrDefault("message") as string ?? error.GetValueOrDefault("code") as string,
            string code => code,
            _ => null,
        };
    }

    private async Task<JsonElement?> FindEndpointAsync(string projectId, string slug, CancellationToken ct)
    {
        foreach (var row in await store.ListEndpointsAsync(projectId, ct))
        {
            if (string.Equals(Text(row, "slug"), slug, StringComparison.Ordinal)
                || string.Equals(Text(row, "endpoint_id"), slug, StringComparison.Ordinal))
                return row;
        }
        return null;
    }

    private static List<string> Warnings(JsonElement row, JsonElement? definition)
    {
        var warnings = new List<string>();
        var enabled = row.TryGetProperty("enabled", out var enabledColumn)
            ? !(enabledColumn.ValueKind == JsonValueKind.False || (enabledColumn.ValueKind == JsonValueKind.Number && enabledColumn.GetDouble() == 0))
            : definition is not { } d || !d.TryGetProperty("enabled", out var enabledField) || enabledField.ValueKind != JsonValueKind.False;
        if (!enabled) warnings.Add("Endpoint is disabled. Game clients receive an error; the test runs it anyway.");
        if (definition is { } def)
        {
            if (def.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True)
                warnings.Add("Endpoint is deprecated.");
            if (Text(def, "exposure") == "internal" || (def.TryGetProperty("internalOnly", out var internalOnly) && internalOnly.ValueKind == JsonValueKind.True))
                warnings.Add("Endpoint is internal and cannot be called by game clients.");
            if (def.TryGetProperty("requiresSecretKey", out var secret) && secret.ValueKind == JsonValueKind.True)
                warnings.Add("Endpoint requires a secret key; game clients with a public key are rejected.");
        }
        return warnings;
    }

    private static JsonElement? Definition(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition)) return null;
        if (definition.ValueKind == JsonValueKind.Object) return definition;
        if (definition.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var document = JsonDocument.Parse(definition.GetString() ?? "null");
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Same conversion the live POST route applies to request bodies.</summary>
    private static IReadOnlyDictionary<string, object?> ToInput(JsonElement input)
        => input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().Where(p => !p.NameEquals("endpoint") && !p.NameEquals("slug"))
                .ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal)
            : new Dictionary<string, object?>();

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        _ => element.GetRawText(),
    };

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
