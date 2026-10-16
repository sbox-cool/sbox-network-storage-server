using System.Collections;
using System.Reflection;
using Acornima;
using Acornima.Ast;

namespace SboxNetworkStorage.EndpointSpike.Compiler;

/// <summary>
/// The single touch point for the pinned Acornima 1.8.0 parser (BSD-3-Clause). Source is parsed only;
/// it is never evaluated. Parser-version-specific spellings live here and nowhere else.
/// </summary>
internal static class JsAst
{
    private static readonly Dictionary<string, string> OperatorSpellings = new(StringComparer.Ordinal)
    {
        ["+"] = "+", ["-"] = "-", ["*"] = "*", ["/"] = "/", ["%"] = "%",
        ["<"] = "<", ["<="] = "<=", [">"] = ">", [">="] = ">=",
        ["=="] = "==", ["!="] = "!=", ["==="] = "===", ["!=="] = "!==",
        ["&&"] = "&&", ["||"] = "||", ["!"] = "!",
        ["Plus"] = "+", ["Addition"] = "+", ["Minus"] = "-", ["Subtraction"] = "-",
        ["Multiply"] = "*", ["Multiplication"] = "*", ["Times"] = "*", ["Divide"] = "/",
        ["Division"] = "/", ["Modulo"] = "%",
        ["LessThan"] = "<", ["LessThanOrEqual"] = "<=", ["GreaterThan"] = ">", ["GreaterThanOrEqual"] = ">=",
        ["Equal"] = "==", ["NotEqual"] = "!=", ["StrictEqual"] = "===", ["StrictNotEqual"] = "!==",
        ["LogicalAnd"] = "&&", ["LogicalOr"] = "||", ["LogicalNot"] = "!", ["Not"] = "!",
    };

    public static Script ParseScript(string source)
    {
        var parser = new Parser(new ParserOptions());
        return parser.ParseScript(source);
    }

    /// <summary>1-based line and column of a node's start.</summary>
    public static (int Line, int Column) Span(Node node)
    {
        var start = node.Location.Start;
        return (start.Line, start.Column + 1);
    }

    /// <summary>Operator symbol such as "+" or "==". Unknown spellings return null (caller rejects).</summary>
    public static string? OperatorText(object op)
        => OperatorSpellings.TryGetValue(op.ToString() ?? string.Empty, out var text) ? text : null;

    /// <summary>Lower-case declaration kind such as "const".</summary>
    public static string KindText(object kind) => (kind.ToString() ?? string.Empty).ToLowerInvariant();

    /// <summary>
    /// Direct child nodes via public reflection over node properties. Used only for bounded counting
    /// (node count and depth) before lowering; the walk stops as soon as a caller's limit is exceeded.
    /// </summary>
    public static IEnumerable<Node> Children(Node node)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            var value = property.GetValue(node);
            if (value is Node child)
            {
                yield return child;
            }
            else if (value is IEnumerable items && value is not string)
            {
                foreach (var item in items)
                {
                    if (item is Node childItem) yield return childItem;
                }
            }
        }
    }
}
