using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Runs scripted endpoints written in TypeScript (<c>runtime: typescript</c>). TypeScript is type-stripped on the
/// server with the vendored sucrase transform (MIT, see <c>ThirdParty/sucrase/LICENSE.txt</c>), which runs inside an
/// isolated Jint engine. Types are erased and never checked. The stripped JavaScript then runs through
/// <see cref="JavaScriptEndpointRuntime"/> with the same sandbox. Stripping preserves line numbers, so runtime
/// errors keep TypeScript line numbers.
/// </summary>
/// <remarks>
/// Not supported because they are not erasable and sucrase would drop or change their meaning: <c>namespace</c>
/// and <c>module</c> declarations with bodies. Enums and constructor parameter properties are compiled by sucrase.
/// Imports are rejected by the module loader, which only provides the handler module.
/// </remarks>
public sealed partial class TypeScriptEndpointRuntime : IEndpointScriptRuntime
{
    public const string LanguageName = "typescript";

    private const string StripResourceName = "SboxNetworkStorage.Application.TypeScript.strip.js";
    private const int StrippedCacheLimit = 256;

    private static readonly EndpointScriptLimits StripLimits = new(
        Timeout: TimeSpan.FromSeconds(5),
        MaxStatements: 20_000_000,
        MemoryBytes: 128L * 1024 * 1024,
        MaxRecursionDepth: 1000,
        MaxSourceBytes: EndpointScriptLimits.Defaults.MaxSourceBytes,
        MaxResponseBytes: EndpointScriptLimits.Defaults.MaxResponseBytes,
        MaxLogBytes: EndpointScriptLimits.Defaults.MaxLogBytes);

    private static readonly Lazy<Prepared<Acornima.Ast.Script>> StripperScript = new(LoadStripperScript);

    private readonly JavaScriptEndpointRuntime _javaScript;
    private readonly ConcurrentDictionary<string, string> _stripped = new(StringComparer.Ordinal);

    public TypeScriptEndpointRuntime(JavaScriptEndpointRuntime javaScript)
    {
        _javaScript = javaScript ?? throw new ArgumentNullException(nameof(javaScript));
    }

    public string Language => LanguageName;

    public bool Enabled => _javaScript.Enabled;

    public EndpointScriptLimits Limits => _javaScript.Limits;

    public IReadOnlyList<EndpointScriptDiagnostic> Check(string source)
    {
        var stripped = Strip(source);
        if (stripped.Diagnostic is not null)
            return [stripped.Diagnostic];
        return _javaScript.Check(stripped.Code!);
    }

    public string ToJavaScript(string source)
    {
        var stripped = Strip(source);
        return stripped.Code ?? throw new InvalidOperationException(stripped.Diagnostic?.Message ?? "TypeScript could not be compiled.");
    }

    public EndpointScriptOutcome Execute(string source, EndpointScriptRequest request, EndpointScriptHost host, ScriptRunBudget budget)
    {
        var stripped = Strip(source);
        if (stripped.Diagnostic is not null)
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.ScriptError, null,
                $"line {stripped.Diagnostic.Line}, column {stripped.Diagnostic.Column}: {stripped.Diagnostic.Message}");
        return _javaScript.Execute(stripped.Code!, request, host, budget);
    }

    /// <summary>Returns the JavaScript for a TypeScript source, cached by source hash.</summary>
    private StripResult Strip(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Encoding.UTF8.GetByteCount(source);
        if (size > Limits.MaxSourceBytes)
            return StripResult.Fail(1, 1, $"Script is {size} bytes; the limit is {Limits.MaxSourceBytes}.");

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (_stripped.TryGetValue(key, out var cached))
            return StripResult.Ok(cached);

        var unsupported = UnsupportedNamespace().Match(source);
        if (unsupported.Success)
        {
            var line = source.AsSpan(0, unsupported.Index).Count('\n') + 1;
            return StripResult.Fail(line, 1,
                "TypeScript namespace and module declarations are not supported; use plain objects or ES module exports.");
        }

        var code = StripWithSucrase(source);
        if (code.Diagnostic is not null)
            return code;

        if (_stripped.Count >= StrippedCacheLimit)
            _stripped.Clear();
        _stripped[key] = code.Code!;
        return code;
    }

    private static StripResult StripWithSucrase(string source)
    {
        var options = new Options { Strict = true }
            .TimeoutInterval(StripLimits.Timeout)
            .MaxStatements(StripLimits.MaxStatements)
            .LimitMemory(StripLimits.MemoryBytes)
            .LimitRecursion(StripLimits.MaxRecursionDepth)
            .DisableStringCompilation(true);
        ScriptEngineGuards.Configure(options, StripLimits.Timeout);
        using var engine = new Engine(options);
        ScriptEngineGuards.Apply(engine, keepBinaryBuffers: true);
        try
        {
            engine.Execute(StripperScript.Value);
            // Pass the source as a global. A .NET-constructed JsString argument makes the bundle's charCodeAt calls fail.
            engine.SetValue("__nsSource", source);
            var envelope = engine.Evaluate("__nsTypeScript.strip(__nsSource)").AsString();

            using var document = JsonDocument.Parse(envelope);
            var root = document.RootElement;
            if (root.GetProperty("ok").GetBoolean())
                return StripResult.Ok(root.GetProperty("code").GetString() ?? string.Empty);
            return StripResult.Fail(
                root.GetProperty("line").GetInt32(),
                root.GetProperty("column").GetInt32(),
                root.GetProperty("message").GetString() ?? "TypeScript syntax error.");
        }
        catch (Exception error) when (error is JintException or TimeoutException)
        {
            return StripResult.Fail(1, 1, "TypeScript could not be compiled within the server's limits.");
        }
    }

    private static Prepared<Acornima.Ast.Script> LoadStripperScript()
    {
        using var stream = typeof(TypeScriptEndpointRuntime).Assembly.GetManifestResourceStream(StripResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{StripResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Engine.PrepareScript(reader.ReadToEnd(), "sucrase-strip.js", strict: false, ScriptEngineGuards.ScriptPreparation(StripLimits.Timeout));
    }

    // `namespace X {` or `module X {` that is not an ambient declaration. Such blocks have runtime semantics that
    // sucrase drops silently, so they are rejected rather than compiled.
    [GeneratedRegex(@"(?<!declare\s+)(?<![\w$.])(?:namespace|module)\s+[A-Za-z_$][\w$.]*\s*\{")]
    private static partial Regex UnsupportedNamespace();

    private readonly record struct StripResult(string? Code, EndpointScriptDiagnostic? Diagnostic)
    {
        public static StripResult Ok(string code) => new(code, null);

        public static StripResult Fail(int line, int column, string message) =>
            new(null, new EndpointScriptDiagnostic(line, column, message));
    }
}
