using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>TypeScript endpoints (<c>runtime: typescript</c>): type stripping through the vendored sucrase transform.</summary>
public sealed class TypeScriptStrippingTests
{
    private static readonly TypeScriptEndpointRuntime Runtime =
        new(new JavaScriptEndpointRuntime(enabled: true, EndpointScriptLimits.Defaults));

    [Fact]
    public void Strip_TypeAnnotationsAndInterfaces_ProducePlainJavaScript()
    {
        const string source = """
            interface Reward { coins: number; reason?: string }
            type Grant = (amount: number) => Reward;
            export default function handle(req: { input: { amount: number } }): Reward {
                const grant: Grant = (amount: number) => ({ coins: amount });
                return grant(req.input.amount as number);
            }
            """;

        Assert.Empty(Runtime.Check(source));
        var js = Runtime.ToJavaScript(source);
        Assert.DoesNotContain("interface", js);
        Assert.DoesNotContain("type Grant", js);
        Assert.DoesNotContain(": number", js);
        Assert.DoesNotContain(" as number", js);
        Assert.Contains("export default function handle(req)", js);
    }

    [Fact]
    public void Strip_Enum_IsCompiledToARuntimeObject()
    {
        const string source = """
            enum Tier { Bronze = "bronze", Gold = "gold" }
            export default function handle() { return { tier: Tier.Gold }; }
            """;

        Assert.Empty(Runtime.Check(source));
        Assert.DoesNotContain("enum Tier", Runtime.ToJavaScript(source));
    }

    [Fact]
    public void Strip_GenericsAndTypeArguments_AreErased()
    {
        const string source = """
            function first<T>(items: T[]): T | undefined { return items[0]; }
            export default function handle() { return first<number>([1, 2]); }
            """;

        Assert.Empty(Runtime.Check(source));
        var js = Runtime.ToJavaScript(source);
        Assert.DoesNotContain("<T>", js);
        Assert.DoesNotContain("<number>", js);
    }

    [Fact]
    public void Strip_AmbientDeclaration_IsAccepted()
    {
        const string source = """
            declare namespace Ambient { const x: number; }
            export default function handle() { return 1; }
            """;

        Assert.Empty(Runtime.Check(source));
    }

    [Fact]
    public void Check_NamespaceWithBody_IsRejectedAtItsLine()
    {
        const string source = """
            export default function handle() { return 1; }
            namespace Helpers {
              export const x = 1;
            }
            """;

        var diagnostic = Assert.Single(Runtime.Check(source));
        Assert.Equal(2, diagnostic.Line);
        Assert.Contains("namespace", diagnostic.Message);
    }

    [Fact]
    public void Check_SyntaxError_ReportsTheTypeScriptLineAndColumn()
    {
        // The unexpected ';' is on line 2, 1-based column 21.
        const string source = "export default function handle() {\n  const x: number = ;\n}\n";

        var diagnostic = Assert.Single(Runtime.Check(source));
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(21, diagnostic.Column);
    }

    [Fact]
    public void ToJavaScript_SameSource_ReturnsTheCachedStrippedOutput()
    {
        const string source = "export default function handle(input: number) { return input; }";

        var first = Runtime.ToJavaScript(source);
        var second = Runtime.ToJavaScript(source);

        // The cache is keyed by the source hash, so a repeat returns the stored string rather than stripping again.
        Assert.Same(first, second);
    }

    [Fact]
    public void Check_SourceOverTheLimit_IsRejectedBeforeStripping()
    {
        var source = new string('a', EndpointScriptLimits.Defaults.MaxSourceBytes + 1);

        var diagnostic = Assert.Single(Runtime.Check(source));
        Assert.Contains("limit", diagnostic.Message);
    }
}
