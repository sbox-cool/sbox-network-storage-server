using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

// A `scope: global` definition is a module other scripts import. The public gate answers it exactly as an
// unknown slug and flags the denial, so every recording path can skip the call.
public sealed class EndpointGlobalScopeTelemetryTests
{
    private static Dictionary<string, object?> Def(params (string Key, object? Value)[] fields)
        => new(fields.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);

    private static JsonElement ErrorElement(EndpointExecutionResult denial)
        => JsonSerializer.SerializeToElement(denial.Body).GetProperty("error");

    [Fact]
    public void Global_scope_is_read_from_string_and_json_values_only()
    {
        Assert.True(EndpointExecutor.IsGlobalScope(Def(("scope", "global"))));
        Assert.True(EndpointExecutor.IsGlobalScope(Def(("scope", JsonSerializer.SerializeToElement("global")))));
        Assert.False(EndpointExecutor.IsGlobalScope(Def(("scope", "public"))));
        Assert.False(EndpointExecutor.IsGlobalScope(Def()));
    }

    [Fact]
    public void Global_definition_is_denied_with_the_unknown_slug_answer_and_flagged()
    {
        var denial = EndpointExecutor.EvaluatePublicGates(Def(("scope", "global"), ("enabled", true)), "bank", hasSecretKey: false);

        Assert.NotNull(denial);
        Assert.Equal(404, denial.Status);
        Assert.True(denial.GlobalScope);
        Assert.Equal("ENDPOINT_NOT_FOUND", ErrorElement(denial).GetProperty("code").GetString());
        Assert.Equal("Endpoint \"bank\" was not found.", ErrorElement(denial).GetProperty("message").GetString());
    }

    [Fact]
    public void Global_denial_wins_over_every_other_gate()
    {
        var disabledDeprecatedSecret = Def(
            ("scope", "global"), ("enabled", false), ("deprecated", true), ("requiresSecretKey", true));

        var denial = EndpointExecutor.EvaluatePublicGates(disabledDeprecatedSecret, "bank", hasSecretKey: false);

        Assert.NotNull(denial);
        Assert.Equal(404, denial.Status);
        Assert.Equal("ENDPOINT_NOT_FOUND", ErrorElement(denial).GetProperty("code").GetString());
        Assert.True(denial.GlobalScope);
    }

    [Fact]
    public void Only_the_global_denial_is_flagged_as_global_scope()
    {
        Assert.False(EndpointExecutor.EvaluatePublicGates(Def(("enabled", false)), "bank", hasSecretKey: false)!.GlobalScope);
        Assert.False(EndpointExecutor.EvaluatePublicGates(Def(("deprecated", true)), "bank", hasSecretKey: false)!.GlobalScope);
        Assert.False(EndpointExecutor.EvaluatePublicGates(Def(("exposure", "internal")), "bank", hasSecretKey: false)!.GlobalScope);
        Assert.False(EndpointExecutor.EvaluatePublicGates(Def(("requiresSecretKey", true)), "bank", hasSecretKey: false)!.GlobalScope);
        Assert.Null(EndpointExecutor.EvaluatePublicGates(Def(("scope", "public")), "bank", hasSecretKey: false));
    }
}
