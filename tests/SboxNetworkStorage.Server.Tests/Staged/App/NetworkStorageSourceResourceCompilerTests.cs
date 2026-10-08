using System.Collections.Generic;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Pins the compiled shape of source-authored endpoint conditions. The user's
/// SERVER_ONLY gate is authored in YAML as
/// <c>check: { field: "{{_hasSecretKey}}", op: "==", value: true }</c> — the
/// sync flow compiles the sourceText server-side via
/// <see cref="NetworkStorageSourceResourceCompiler"/>, and the stored definition
/// is what the executor evaluates. These tests assert the compile preserves the
/// boolean VALUE (JSON <c>true</c>, not the string "true") and the templated
/// FIELD, and that the compiled check evaluates correctly against
/// <c>_hasSecretKey</c>.
/// </summary>
public sealed class NetworkStorageSourceResourceCompilerTests
{
    private const string ServerOnlyEndpointYaml = """
        kind: endpoint
        slug: init-player
        name: Init Player
        method: POST
        steps:
          - id: server_only
            type: condition
            check:
              field: "{{_hasSecretKey}}"
              op: "=="
              value: true
            onFail:
              action: reject
              status: 403
              errorCode: SERVER_ONLY
              errorMessage: dedicated server only
          - id: z
            type: transform
            expression: "{{input.zone}}"
        response:
          status: 200
          body:
            ok: true
            zone: "{{z}}"
        """;

    [Fact]
    public void Compile_ServerOnlyCondition_PreservesTemplateFieldAndBooleanValue()
    {
        var resource = JsonSerializer.SerializeToElement(new
        {
            id = "init-player",
            slug = "init-player",
            sourceFormat = "yaml",
            sourceText = ServerOnlyEndpointYaml,
        });

        var ok = NetworkStorageSourceResourceCompiler.TryCompile(resource, "endpoint", out var compiled, out var error);

        Assert.True(ok, error);
        Assert.True(compiled.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array);
        var check = steps[0].GetProperty("check");

        // The compiled check must keep the documented shape: templated field,
        // "==" operator, and a REAL boolean value — not the string "true".
        Assert.Equal("{{_hasSecretKey}}", check.GetProperty("field").GetString());
        Assert.Equal("==", check.GetProperty("op").GetString());
        Assert.Equal(JsonValueKind.True, check.GetProperty("value").ValueKind);

        // Numbers must stay numbers (YamlDotNet resolves int/float for object
        // targets) and response booleans must not be stringified either.
        Assert.Equal(JsonValueKind.Number, steps[0].GetProperty("onFail").GetProperty("status").ValueKind);
        Assert.Equal(403, steps[0].GetProperty("onFail").GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.True, compiled.GetProperty("response").GetProperty("body").GetProperty("ok").ValueKind);
    }

    [Fact]
    public void CompiledCheck_WithStringifiedValue_StillEvaluatesAgainstHasSecretKey()
    {
        // Defensive: definitions compiled before the boolean fix carry
        // "value": "true" (a STRING). The evaluator must tolerate that stored
        // form so existing endpoints work without a re-push.
        var resource = JsonSerializer.SerializeToElement(new
        {
            id = "init-player",
            slug = "init-player",
            sourceFormat = "yaml",
            sourceText = ServerOnlyEndpointYaml.Replace("value: true", "value: \"true\""),
        });

        Assert.True(NetworkStorageSourceResourceCompiler.TryCompile(resource, "endpoint", out var compiled, out var error), error);
        var check = EndpointExpression.FromJson(compiled.GetProperty("steps")[0].GetProperty("check"));

        var withSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = true };
        var withoutSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = false };

        Assert.True(EndpointExpression.EvaluateCondition(check, withSecret));
        Assert.False(EndpointExpression.EvaluateCondition(check, withoutSecret));
    }

    [Fact]
    public void CompiledCheck_EvaluatesAgainstHasSecretKey()
    {
        var resource = JsonSerializer.SerializeToElement(new
        {
            id = "init-player",
            slug = "init-player",
            sourceFormat = "yaml",
            sourceText = ServerOnlyEndpointYaml,
        });

        Assert.True(NetworkStorageSourceResourceCompiler.TryCompile(resource, "endpoint", out var compiled, out var error), error);
        var check = EndpointExpression.FromJson(compiled.GetProperty("steps")[0].GetProperty("check"));

        var withSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = true };
        var withoutSecret = new Dictionary<string, object?> { ["_hasSecretKey"] = false };

        Assert.True(EndpointExpression.EvaluateCondition(check, withSecret));
        Assert.False(EndpointExpression.EvaluateCondition(check, withoutSecret));
    }
}
