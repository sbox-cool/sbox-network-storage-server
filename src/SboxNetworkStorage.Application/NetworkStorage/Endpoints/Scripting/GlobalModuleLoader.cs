using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;
using Jint.Runtime.Modules;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Serves <c>import { fn } from "globals/&lt;endpointId&gt;"</c> for scripted endpoints. It is the only module source a
/// script has: bare names, URLs, relative paths and files are refused. The host supplies the JavaScript of a global
/// endpoint, and returns null for any id that is not a stored global endpoint of the project.
/// </summary>
internal sealed class GlobalModuleLoader(Func<string, string?> source) : ModuleLoader
{
    public const string Prefix = "globals/";

    /// <summary>The module name the entry script is registered under. It resolves to the registered module, never to a file or a global.</summary>
    public const string EntryModule = "endpoint";

    private static readonly Regex ValidId = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}\\z", RegexOptions.CultureInvariant);

    internal static bool IsValidModuleId(string value) => ValidId.IsMatch(value);

    public override ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
    {
        var specifier = moduleRequest.Specifier;
        if (specifier == EntryModule)
            return new ResolvedSpecifier(moduleRequest, specifier, null, SpecifierType.Bare);
        if (!specifier.StartsWith(Prefix, StringComparison.Ordinal) || !IsValidModuleId(specifier[Prefix.Length..]))
            throw new JavaScriptException(
                $"Module \"{specifier}\" is not available. Scripts can only import globals/<endpointId>.");
        return new ResolvedSpecifier(moduleRequest, specifier, null, SpecifierType.Bare);
    }

    protected override string LoadModuleContents(Engine engine, ResolvedSpecifier resolved)
    {
        var endpointId = resolved.Key[Prefix.Length..];
        return source(endpointId)
            ?? throw new InvalidOperationException($"globals/{endpointId} is not a global endpoint of this project.");
    }
}
