using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Server.Owner;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The endpoint page and endpoints list: row facts (runtime, scope, Steps, Access), the runtime filter, and the
/// script outline helpers. The helpers are pure; no host is started.
/// </summary>
public sealed class OwnerEndpointPageTests
{
    private const string EndpointRowJson = """{"endpoint_id":"ping","name":"Ping"}""";

    private static readonly Lazy<IReadOnlyList<IEndpointScriptRuntime>> ScriptRuntimes =
        new(() => EndpointExecutor.CreateScriptRuntimes(true));

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static OwnerResourceRow EndpointRow(string definition) =>
        OwnerResourcesController.BuildRow("endpoint", Json(EndpointRowJson), Json(definition), new(), new(), new());

    private static IEndpointScriptRuntime? RuntimeFor(string language) =>
        ScriptRuntimes.Value.FirstOrDefault(runtime => runtime.Language == language);

    [Fact]
    public void GlobalJavaScriptEndpointReportsRuntimeScopeAndInternalAccess()
    {
        var row = EndpointRow("""{"runtime":"javascript","scope":"global","method":"GET","script":"export default function handler(req, ns) {}"}""");

        Assert.Equal("javascript", row.Runtime);
        Assert.Equal("global", row.Scope);
        Assert.Equal("JavaScript", row.RuntimeLabel);
        Assert.Equal("Global scope", row.ScopeLabel);
        Assert.Equal("Internal", row.Access);
        Assert.Equal("GET", row.Method);
        Assert.Equal(0, row.Steps);
    }

    [Fact]
    public void PublicTypeScriptEndpointReadsAsPublic()
    {
        var row = EndpointRow("""{"runtime":"typescript","scope":"public","script":"export default function handler(req: any) {}"}""");

        Assert.Equal("typescript", row.Runtime);
        Assert.Equal("TypeScript", row.RuntimeLabel);
        Assert.Equal("Public scope", row.ScopeLabel);
        Assert.Equal("Public", row.Access);
    }

    [Fact]
    public void SecretKeyScriptEndpointReadsAsSecretKey()
    {
        var row = EndpointRow("""{"runtime":"javascript","scope":"public","requiresSecretKey":true,"script":""}""");

        Assert.Equal("Secret key", row.Access);
    }

    [Fact]
    public void YamlEndpointCountsStepsAndHasNoRuntimeOrScope()
    {
        var row = EndpointRow("""{"method":"POST","steps":[{"set":{}},{"respond":{}}]}""");

        Assert.Null(row.Runtime);
        Assert.Null(row.Scope);
        Assert.Equal("YAML steps", row.RuntimeLabel);
        Assert.Null(row.ScopeLabel);
        Assert.Equal(2, row.Steps);
    }

    [Fact]
    public void DraftScriptEndpointCarriesRuntimeScopeAndAccess()
    {
        var row = OwnerResourcesController.BuildDraftRow("endpoint", "new-endpoint", "Draft",
            Json("""{"runtime":"typescript","scope":"global","script":""}"""));

        Assert.Equal("typescript", row.Runtime);
        Assert.Equal("global", row.Scope);
        Assert.Equal("Internal", row.Access);
        Assert.Equal(0, row.Steps);
    }

    [Theory]
    [InlineData(null, "all", true)]
    [InlineData("javascript", "all", true)]
    [InlineData(null, "yaml", true)]
    [InlineData("javascript", "yaml", false)]
    [InlineData("javascript", "javascript", true)]
    [InlineData("typescript", "javascript", false)]
    [InlineData(null, "javascript", false)]
    [InlineData("typescript", "typescript", true)]
    [InlineData(null, "typescript", false)]
    public void RuntimeFilterSelectsOnlyTheNamedRuntime(string? runtime, string filter, bool expected) =>
        Assert.Equal(expected, OwnerResourcesController.MatchesRuntimeFilter(runtime, filter));

    [Fact]
    public void MemberUsageListsReadsOutsideStringsCommentsAndTemplates()
    {
        const string javaScript = """
            export default function handler(req, ns) {
                // req.ignored in a comment
                const text = "req.inString";
                const id = req.input.id;
                const player = req.player?.steamId;
                ns.records.get("x");
                return `${req.input.name} ${ns.records}`;
            }
            """;

        var usage = OwnerResourcesController.ScriptMemberUsage(javaScript, ["req", "ns"]);

        Assert.Equal(2, usage.Count);
        Assert.Equal("req", usage[0].Parameter);
        Assert.Equal(new[] { "input", "player" }, usage[0].Names);
        Assert.Equal("ns", usage[1].Parameter);
        Assert.Equal(new[] { "records" }, usage[1].Names);
    }

    [Fact]
    public void HandlerParametersAreTheFirstTwoOfTheDefaultExport()
    {
        var module = ScriptModuleAnalysis.Analyze("export default function handler(req, ns, extra) { return null; }");

        Assert.Equal(new[] { "req", "ns" }, OwnerResourcesController.HandlerParameters(module));
    }

    [Fact]
    public void HandlerParametersAreEmptyWithoutADefaultFunction()
    {
        var module = ScriptModuleAnalysis.Analyze("export const limit = 3;");

        Assert.Empty(OwnerResourcesController.HandlerParameters(module));
    }

    [Fact]
    public void ValidJavaScriptViewCarriesOutlineAndMemberReads()
    {
        const string source = """
            import { price } from "globals/pricing";
            export default function handler(req, ns) {
                return { total: price(req.input.qty), seen: ns.records.get("x") };
            }
            """;

        var view = OwnerResourcesController.ScriptView("javascript", source, RuntimeFor("javascript"));

        Assert.Null(view.Error);
        Assert.NotNull(view.Module);
        Assert.Equal(source, view.Source);
        var export = Assert.Single(view.Module!.Exports);
        Assert.Equal("default", export.Name);
        Assert.Equal(ScriptExportKind.Function, export.Kind);
        Assert.Equal("pricing", Assert.Single(view.Module.GlobalImports).EndpointId);
        Assert.Equal(2, view.Members.Count);
        Assert.Equal(new[] { "input" }, view.Members[0].Names);
        Assert.Equal(new[] { "records" }, view.Members[1].Names);
    }

    [Fact]
    public void ValidTypeScriptViewIsStrippedBeforeAnalysis()
    {
        const string source = """
            export default function handler(req: { input: { id: string } }, ns: any): number {
                return req.input.id.length;
            }
            """;

        var view = OwnerResourcesController.ScriptView("typescript", source, RuntimeFor("typescript"));

        Assert.Null(view.Error);
        Assert.NotNull(view.Module);
        Assert.Equal(new[] { "req", "ns" }, OwnerResourcesController.HandlerParameters(view.Module!));
        Assert.Equal(new[] { "input" }, view.Members[0].Names);
    }

    [Fact]
    public void SourceThatDoesNotParseKeepsItsTextAndReason()
    {
        const string source = "export default function handler(req, ns) {";

        var view = OwnerResourcesController.ScriptView("javascript", source, RuntimeFor("javascript"));

        Assert.Null(view.Module);
        Assert.False(string.IsNullOrEmpty(view.Error));
        Assert.Equal(source, view.Source);
        Assert.Empty(view.Members);
    }

    [Fact]
    public void UnsupportedModuleFormReportsItsPosition()
    {
        const string source = "export * from \"globals/pricing\";";

        var view = OwnerResourcesController.ScriptView("javascript", source, RuntimeFor("javascript"));

        Assert.Null(view.Module);
        Assert.StartsWith("Line 1, column ", view.Error!);
        Assert.Empty(view.Members);
    }

    [Fact]
    public void MissingRuntimeExplainsWhyTheSourceIsNotAnalysed()
    {
        var view = OwnerResourcesController.ScriptView("typescript", "export default 1;", null);

        Assert.Null(view.Module);
        Assert.Equal("The typescript runtime is not available on this server.", view.Error);
        Assert.Equal("export default 1;", view.Source);
        Assert.Empty(view.Members);
    }
}
