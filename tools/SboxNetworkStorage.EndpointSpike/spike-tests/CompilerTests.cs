using System.Text;
using SboxNetworkStorage.EndpointSpike.Compiler;
using SboxNetworkStorage.EndpointSpike.Plan;

namespace SboxNetworkStorage.EndpointSpike.Tests;

public sealed class CompilerTests
{
    private const string Probe =
        "endpoint({ id: \"probe\", method: \"POST\", input: { type: \"object\", properties: {}, required: [] } }, " +
        "({ now }) => {\n  return { status: 200, body: { at: now } };\n});\n";

    [Fact]
    public void Finish_combat_fixture_compiles_to_a_plan_that_declares_players()
    {
        var outcome = NsEndpointCompiler.Compile(SpikeFixtures.ReadFixture("finish-combat.ns.js"));

        Assert.True(outcome.Ok, outcome.Error?.Message);
        var endpoint = outcome.Endpoint!;
        Assert.Equal("finish-combat", endpoint.EndpointId);
        var plan = PlanReader.ParseObject(endpoint.PlanJson);
        Assert.Equal("finish-combat", plan["id"]);
        Assert.Contains("players", (IEnumerable<object?>)plan["collections"]!);
    }

    [Fact]
    public void Compiling_the_same_source_twice_yields_identical_hashes()
    {
        var source = SpikeFixtures.ReadFixture("finish-combat.ns.js");

        var first = NsEndpointCompiler.Compile(source).Endpoint!;
        var second = NsEndpointCompiler.Compile(source).Endpoint!;

        Assert.Equal(first.PlanSha256, second.PlanSha256);
        Assert.Equal(first.PlanJson, second.PlanJson);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
    }

    [Fact]
    public void Source_exactly_at_the_byte_limit_compiles()
    {
        var source = SpikeFixtures.PadToBytes(Probe, NsLimits.MaxSourceBytes);
        Assert.Equal(NsLimits.MaxSourceBytes, Encoding.UTF8.GetByteCount(source));

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.True(outcome.Ok, outcome.Error?.Message);
    }

    [Fact]
    public void Source_one_byte_over_the_limit_is_rejected_before_parsing()
    {
        var source = SpikeFixtures.PadToBytes(Probe, NsLimits.MaxSourceBytes + 1);

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.False(outcome.Ok);
        Assert.Equal("SOURCE_TOO_LARGE", outcome.Error!.Code);
    }

    [Fact]
    public void Bracket_nesting_beyond_the_limit_is_rejected()
    {
        var depth = NsLimits.MaxNestingDepth + 1;
        var nested = new string('[', depth) + "1" + new string(']', depth);
        var source = Probe.Replace("body: { at: now }", "body: { at: now, deep: " + nested + " }");

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.False(outcome.Ok);
        Assert.Equal("DEPTH_EXCEEDED", outcome.Error!.Code);
    }

    [Fact]
    public void Template_literals_are_rejected()
    {
        var source = Probe.Replace("body: { at: now }", "body: { at: `${now}` }");

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.False(outcome.Ok);
        Assert.Equal("TEMPLATE_UNSUPPORTED", outcome.Error!.Code);
    }

    [Theory]
    [InlineData("while (true) { }")]
    [InlineData("for (let i = 0; i < 3; i++) { }")]
    [InlineData("eval(\"1\");")]
    [InlineData("const f = function () { return 1; };")]
    public void Constructs_outside_the_grammar_are_rejected_without_execution(string statement)
    {
        var source = Probe.Replace("  return {", "  " + statement + "\n  return {");

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error!.Code));
    }

    [Fact]
    public void Unknown_capability_is_rejected_with_its_name()
    {
        var source = Probe.Replace("({ now })", "({ now, fetch })");

        var outcome = NsEndpointCompiler.Compile(source);

        Assert.False(outcome.Ok);
        Assert.Equal("UNDECLARED_IDENTIFIER", outcome.Error!.Code);
        Assert.Contains("fetch", outcome.Error.Message);
    }

    [Fact]
    public void Syntax_error_carries_a_stable_code()
    {
        var outcome = NsEndpointCompiler.Compile("endpoint({ id: ");

        Assert.False(outcome.Ok);
        Assert.Equal("SYNTAX_ERROR", outcome.Error!.Code);
    }
}
