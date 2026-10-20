using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// The TypeScript declarations an endpoint script sees for one project: the shipped <c>ns.d.ts</c>, then the
/// project's collections, game values, workflow aliases and global endpoint exports, merged into the
/// placeholder interfaces that file declares.
/// </summary>
internal static class EndpointTypings
{
    private const string StaticResource = "SboxNetworkStorage.Server.Owner.endpoint-api.ns.d.ts";
    private const string TemplatePrefix = "SboxNetworkStorage.Server.Owner.endpoint-api.templates.";
    private static readonly Regex Identifier = new("^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant);

    /// <summary>The shipped static declarations, valid on their own.</summary>
    public static string StaticDeclarations()
    {
        using var stream = typeof(EndpointTypings).Assembly.GetManifestResourceStream(StaticResource)
            ?? throw new InvalidOperationException("The endpoint ns.d.ts asset is missing from the server build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>File names of the shipped endpoint templates, for example <c>buy-item.typescript.endpoint.yml</c>.</summary>
    public static IReadOnlyList<string> TemplateFileNames() =>
        typeof(EndpointTypings).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(TemplatePrefix, StringComparison.Ordinal))
            .Select(name => name[TemplatePrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>One shipped endpoint template as YAML, or null when no template has that file name.</summary>
    public static Stream? Template(string fileName) =>
        TemplateFileNames().Contains(fileName, StringComparer.Ordinal)
            ? typeof(EndpointTypings).Assembly.GetManifestResourceStream(TemplatePrefix + fileName)
            : null;

    /// <summary>The full typings for one project.</summary>
    public static string ForProject(OwnerProjectResources resources, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes)
    {
        var builder = new StringBuilder(StaticDeclarations());
        builder.Append("\n// ---- Declarations for this project ----\n");
        AppendInterface(builder, "CollectionIdMap", resources.CollectionIds().Select(id => $"{Key(id)}: unknown;"));
        AppendInterface(builder, "GameValueGroups", resources.ValueItems().Select(item => GroupMember(item.Id, item.Keys)));
        AppendInterface(builder, "WorkflowAliases", ScriptWorkflowAliases.Build(resources.WorkflowIds()).Keys
            .Select(alias => $"{Key(alias)}(input?: Record<string, unknown>): unknown;"));
        var globals = resources.GlobalExports(scriptRuntimes).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        AppendInterface(builder, "GlobalModules", globals
            .Select(pair => $"{Key(pair.Key)}: {{ {string.Concat(pair.Value.Exports.Select(ExportMember))}}};"));
        // One ambient module per global endpoint, so `import ... from "globals/<id>"` completes the path and its exports.
        foreach (var pair in globals)
            AppendGlobalModule(builder, pair.Key, pair.Value.Exports);
        return builder.ToString();
    }

    private static void AppendGlobalModule(StringBuilder builder, string id, IEnumerable<ScriptExport> exports)
    {
        builder.Append("declare module ").Append(Key("globals/" + id)).Append(" {\n");
        foreach (var export in exports.Where(export => Identifier.IsMatch(export.Name) && export.Name != "default"))
            builder.Append("  ").Append(ModuleExport(export)).Append('\n');
        builder.Append("}\n");
    }

    private static string ModuleExport(ScriptExport export) => export.Kind switch
    {
        ScriptExportKind.Function => $"export function {export.Name}({Parameters(export.Parameters)}): unknown;",
        ScriptExportKind.Class => $"export const {export.Name}: new (...args: unknown[]) => unknown;",
        _ => $"export const {export.Name}: unknown;",
    };

    private static string GroupMember(string id, IReadOnlyList<string> keys) =>
        $"{Key(id)}: {{ {string.Concat(keys.Select(key => $"{Key(key)}: unknown; "))}}};";

    private static string ExportMember(ScriptExport export) => export.Kind switch
    {
        ScriptExportKind.Function => $"{Key(export.Name)}({Parameters(export.Parameters)}): unknown; ",
        ScriptExportKind.Class => $"{Key(export.Name)}: new (...args: unknown[]) => unknown; ",
        _ => $"{Key(export.Name)}: unknown; ",
    };

    private static string Parameters(IReadOnlyList<ScriptParameter> parameters) =>
        string.Join(", ", parameters.Select(ParameterDeclaration));

    private static string ParameterDeclaration(ScriptParameter parameter, int index)
    {
        // Unnamed or destructured parameters have no identifier; give them a positional name.
        var name = Identifier.IsMatch(parameter.Name) ? parameter.Name : $"arg{index}";
        var type = parameter.JsDocType is "string" or "number" or "boolean" ? parameter.JsDocType : "unknown";
        if (parameter.Rest)
            return $"...{name}: {type}[]";
        return $"{name}{(parameter.Optional ? "?" : "")}: {type}";
    }

    private static void AppendInterface(StringBuilder builder, string name, IEnumerable<string> members)
    {
        var list = members.ToList();
        if (list.Count == 0)
            return;
        builder.Append("interface ").Append(name).Append(" {\n");
        foreach (var member in list)
            builder.Append("  ").Append(member).Append('\n');
        builder.Append("}\n");
    }

    // A JSON string literal is a valid TypeScript string literal, so ids and export names are always safe as keys.
    private static string Key(string name) => JsonSerializer.Serialize(name);
}
