using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>What a script module imports and exports, read from the JavaScript the runtime executes.</summary>
/// <param name="GlobalImports">Every <c>globals/&lt;id&gt;</c> import, in source order, one entry per endpoint id.</param>
/// <param name="Exports">Every export. Re-exports and <c>export *</c> are rejected, so each export is declared in this module.</param>
public sealed record ScriptModuleInfo(IReadOnlyList<ScriptImport> GlobalImports, IReadOnlyList<ScriptExport> Exports);

/// <summary>A <c>globals/&lt;id&gt;</c> import: the endpoint id and the names taken from it. <c>default</c> is the default export, <c>*</c> a namespace import; empty for a bare side-effect import.</summary>
/// <param name="EndpointId">The imported global endpoint's id.</param>
/// <param name="Names">The imported names, deduplicated, in source order.</param>
public sealed record ScriptImport(string EndpointId, IReadOnlyList<string> Names);

/// <summary>An export of a script module.</summary>
/// <param name="Name">The exported name; <c>default</c> for the default export.</param>
/// <param name="Kind">Whether the export is a function, a class or a value.</param>
/// <param name="Parameters">The parameters of a function declared in this module; null for every other export.</param>
public sealed record ScriptExport(string Name, ScriptExportKind Kind, IReadOnlyList<ScriptParameter>? Parameters);

/// <summary>What an export is, as far as the typings care.</summary>
public enum ScriptExportKind
{
    /// <summary>A function declaration.</summary>
    Function,

    /// <summary>A class declaration. Typed as <c>any</c>.</summary>
    Class,

    /// <summary>A variable or re-bound name. Typed as <c>any</c>.</summary>
    Value,
}

/// <summary>A parameter of an exported function.</summary>
/// <param name="Name">The parameter name, or <c>arg{n}</c> for a destructured parameter.</param>
/// <param name="JsDocType">The primitive type named by a JSDoc <c>@param</c> tag, or null when there is none or it is not a primitive.</param>
/// <param name="Optional">True for a parameter with a default value.</param>
/// <param name="Rest">True for a rest parameter.</param>
public sealed record ScriptParameter(string Name, string? JsDocType, bool Optional, bool Rest);

/// <summary>A save-time problem in a script module, with the location of the construct that caused it.</summary>
public sealed class ScriptModuleException(int line, int column, string message) : Exception(message)
{
    public int Line { get; } = line;

    public int Column { get; } = column;
}

/// <summary>
/// Reads the imports and exports of a global module (<c>scope: global</c>) at save time, so the server can reject
/// unresolvable imports, cycles and unsupported forms before anything runs, and so the project typings can declare
/// each export. Only the forms a global module may use are accepted; see <c>docs/endpoints-javascript.md</c>.
/// </summary>
public static class ScriptModuleAnalysis
{
    /// <summary>The module specifier prefix for another endpoint's global module.</summary>
    public const string GlobalPrefix = GlobalModuleLoader.Prefix;

    private static readonly Regex JsDocParam = new(@"@param\s+\{([^}]*)\}\s+\[?([A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);

    private static readonly Regex PrimitiveType = new(
        @"^(string|number|boolean|bigint|any|unknown|null|undefined|void|never|object)(\[\])*$",
        RegexOptions.CultureInvariant);

    /// <summary>Parses <paramref name="javaScript"/> as an ES module and returns its imports and exports.</summary>
    /// <exception cref="ParseErrorException">The source does not parse. Callers check syntax first and report its position.</exception>
    /// <exception cref="ScriptModuleException">The module uses an import or export form a global module may not use.</exception>
    public static ScriptModuleInfo Analyze(string javaScript)
    {
        var module = new Parser().ParseModule(javaScript);
        var imports = new List<ScriptImport>();
        var exports = new List<ScriptExport>();
        foreach (var statement in module.Body)
        {
            switch (statement)
            {
                case ImportDeclaration import:
                    AddImport(imports, import);
                    break;

                case ExportAllDeclaration all:
                    throw Unsupported(all, "export * from is not supported in a global module; export each name explicitly.");

                case ExportNamedDeclaration named when named.Source is not null:
                    throw Unsupported(named, "Re-exporting from another module is not supported in a global module.");

                case ExportNamedDeclaration named when named.Declaration is not null:
                    exports.AddRange(DeclarationExports(named.Declaration, javaScript, named));
                    break;

                case ExportNamedDeclaration named:
                    foreach (var specifier in named.Specifiers)
                        exports.Add(new ScriptExport(ExportedName(specifier.Exported, named), ScriptExportKind.Value, null));
                    break;

                case ExportDefaultDeclaration defaultExport:
                    exports.Add(defaultExport.Declaration switch
                    {
                        FunctionDeclaration function => new ScriptExport("default", ScriptExportKind.Function,
                            Parameters(function.Params, javaScript, defaultExport)),
                        ClassDeclaration => new ScriptExport("default", ScriptExportKind.Class, null),
                        _ => new ScriptExport("default", ScriptExportKind.Value, null),
                    });
                    break;
            }
        }
        return new ScriptModuleInfo(imports, exports);
    }

    /// <summary>Adds an import, merging the names of repeated imports of the same endpoint.</summary>
    private static void AddImport(List<ScriptImport> imports, ImportDeclaration import)
    {
        var id = GlobalImportId(import);
        var names = new List<string>();
        foreach (var specifier in import.Specifiers)
        {
            names.Add(specifier switch
            {
                ImportDefaultSpecifier => "default",
                ImportNamespaceSpecifier => "*",
                ImportSpecifier named => ExportedName(named.Imported, import),
                _ => throw Unsupported(import, "This import form is not supported in a script."),
            });
        }
        var index = imports.FindIndex(existing => existing.EndpointId == id);
        if (index < 0) imports.Add(new ScriptImport(id, names.Distinct(StringComparer.Ordinal).ToList()));
        else imports[index] = imports[index] with { Names = imports[index].Names.Union(names, StringComparer.Ordinal).ToList() };
    }

    /// <summary>
    /// The endpoint id of a <c>globals/&lt;id&gt;</c> import. Any other specifier is rejected here; the loader rejects it
    /// again at run time.
    /// </summary>
    private static string GlobalImportId(ImportDeclaration import)
    {
        var specifier = (string)import.Source.Value!;
        if (!specifier.StartsWith(GlobalPrefix, StringComparison.Ordinal) || specifier.Length == GlobalPrefix.Length)
            throw Unsupported(import,
                $"Module \"{specifier}\" is not available. Scripts can only import globals/<endpointId>.");
        return specifier[GlobalPrefix.Length..];
    }

    private static IEnumerable<ScriptExport> DeclarationExports(Declaration declaration, string source, Node statement)
    {
        switch (declaration)
        {
            case FunctionDeclaration function:
                yield return new ScriptExport(function.Id!.Name, ScriptExportKind.Function,
                    Parameters(function.Params, source, statement));
                break;

            case ClassDeclaration cls:
                yield return new ScriptExport(cls.Id!.Name, ScriptExportKind.Class, null);
                break;

            case VariableDeclaration variables:
                foreach (var declarator in variables.Declarations)
                {
                    if (declarator.Id is not Identifier identifier)
                        throw Unsupported(statement, "Destructuring exports are not supported in a global module; export each name explicitly.");
                    yield return new ScriptExport(identifier.Name, ScriptExportKind.Value, null);
                }
                break;

            default:
                throw Unsupported(statement, "This export form is not supported in a global module.");
        }
    }

    private static string ExportedName(Expression exported, Node statement) => exported switch
    {
        Identifier identifier => identifier.Name,
        Literal { Value: string text } => text,
        _ => throw Unsupported(statement, "This export name is not supported in a global module."),
    };

    private static List<ScriptParameter> Parameters(NodeList<Node> parameters, string source, Node statement)
    {
        var jsDoc = JsDocParamTypes(source, statement.Range.Start);
        var result = new List<ScriptParameter>();
        for (var index = 0; index < parameters.Count; index++)
        {
            var (name, optional, rest) = parameters[index] switch
            {
                Identifier identifier => (identifier.Name, false, false),
                AssignmentPattern { Left: Identifier identifier } => (identifier.Name, true, false),
                RestElement { Argument: Identifier identifier } => (identifier.Name, false, true),
                _ => ($"arg{index}", false, false),
            };
            result.Add(new ScriptParameter(name, jsDoc.GetValueOrDefault(name), optional, rest));
        }
        return result;
    }

    /// <summary>
    /// The primitive JSDoc <c>@param</c> types of the comment directly before <paramref name="start"/>, by name. Types that
    /// are not primitives (or arrays of them) are dropped, so the typings never name a type they do not declare.
    /// </summary>
    private static Dictionary<string, string> JsDocParamTypes(string source, int start)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var before = source[..start].TrimEnd();
        if (!before.EndsWith("*/", StringComparison.Ordinal)) return types;
        var open = before.LastIndexOf("/**", StringComparison.Ordinal);
        if (open < 0) return types;
        foreach (Match match in JsDocParam.Matches(before[open..]))
        {
            var type = match.Groups[1].Value.Trim();
            if (type == "*" || type == "Object") type = "any";
            if (type == "Array") type = "any[]";
            if (PrimitiveType.IsMatch(type)) types[match.Groups[2].Value] = type;
        }
        return types;
    }

    private static ScriptModuleException Unsupported(Node node, string message) =>
        new(node.Location.Start.Line, node.Location.Start.Column + 1, message);
}
