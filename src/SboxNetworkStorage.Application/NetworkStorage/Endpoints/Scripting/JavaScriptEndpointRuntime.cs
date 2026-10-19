using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Jint.Runtime.Modules;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Runs scripted endpoints (<c>runtime: javascript</c>) with Jint, a managed JavaScript interpreter. Each invocation
/// gets a fresh engine with no CLR interop, no file or network modules, no string compilation and hard limits for time,
/// statements, memory and recursion. The only way out of the engine is the <c>host</c> function, which carries JSON text
/// to <see cref="EndpointScriptHost"/>.
/// </summary>
/// <remarks>
/// Script contract: the source is an ES module whose default export is the handler,
/// <c>export default function handle(req, ns) { ... }</c> (the function may have any name). It must be synchronous.
/// Ambient globals for the whole invocation, including imported global modules: <c>ns</c>, <c>req</c>, <c>workflows</c>
/// and <c>globals</c>. The only importable modules are <c>globals/&lt;endpointId&gt;</c> (see <see cref="GlobalModuleLoader"/>).
/// </remarks>
public sealed class JavaScriptEndpointRuntime : IEndpointScriptRuntime
{
    public const string LanguageName = "javascript";

    private const string ModuleSpecifier = GlobalModuleLoader.EntryModule;
    private const int PreparedCacheLimit = 256;
    /// <summary>Time budget for save-time checks and analysis. Separate from the request budget so a slow host does not fail a valid save.</summary>
    internal static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(5);

    // Member names come from ScriptApiAttribute values, which are fixed identifiers, and are quoted anyway.
    private static readonly string Prelude = BuildPrelude();

    private static readonly Regex SyntaxLocation = new(
        @"^(?<message>.*) \(.*:(?<line>\d+):(?<column>\d+)\)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly BoundedLruCache<string, Prepared<Acornima.Ast.Module>> _prepared = new(PreparedCacheLimit);

    private readonly ILogger? _logger;

    public JavaScriptEndpointRuntime(bool enabled, EndpointScriptLimits limits, ILogger<JavaScriptEndpointRuntime>? logger = null)
    {
        Enabled = enabled;
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _logger = logger;
    }

    public string Language => LanguageName;

    public bool Enabled { get; }

    public EndpointScriptLimits Limits { get; }

    public IReadOnlyList<EndpointScriptDiagnostic> Check(string source)
    {
        var size = Encoding.UTF8.GetByteCount(source);
        if (size > Limits.MaxSourceBytes)
            return [new EndpointScriptDiagnostic(1, 1, $"Script is {size} bytes; the limit is {Limits.MaxSourceBytes}.")];
        // Save-time checks get their own budget, so a slow host cannot turn a valid script into a save failure.
        using var budget = new ScriptRunBudget(Limits with { Timeout = ValidationTimeout }, CancellationToken.None);
        try
        {
            using var engine = CreateEngine(budget, loader: null);
            budget.Start();
            Prepare(source, budget.Limits);
            return [];
        }
        catch (Exception error)
        {
            if (SyntaxDiagnostic(error) is { } syntax)
                return [syntax];
            var outcome = Classify(error);
            if (outcome.Kind == EndpointScriptOutcomeKind.LimitExceeded)
                return [new EndpointScriptDiagnostic(1, 1, $"The script could not be checked within the server's limits: {outcome.OwnerDetail}")];
            throw;
        }
    }

    public string ToJavaScript(string source) => source;

    public EndpointScriptOutcome Execute(string source, EndpointScriptRequest request, EndpointScriptHost host, ScriptRunBudget budget)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        Engine? engine = null;
        try
        {
            // Engine setup is not counted against the request's time budget; Start() arms it before user code runs.
            engine = CreateEngine(budget, new GlobalModuleLoader(host.GlobalModuleSource));
            budget.Start();
            var prepared = Prepare(source, budget.Limits);
            engine.Modules.Add(ModuleSpecifier, builder => builder.AddModule(in prepared));

            var hostFunction = new ClrFunction(engine, "host", (_, args) =>
                new JsString(host.Invoke(args[0].AsString(), args[1].AsString())), 2);
            var importGlobal = new ClrFunction(engine, "importGlobal", (_, args) =>
                engine.Modules.Import(GlobalModuleLoader.Prefix + args[0].AsString()), 1);
            engine.SetValue("__hostCall", hostFunction);
            var environment = engine.Call(engine.Evaluate(Prelude), JsValue.Undefined, new JsValue[] { importGlobal }).AsObject();
            var ns = environment.Get("ns");
            var serialize = environment.Get("serialize");

            var requestJson = JsonSerializer.Serialize(new
            {
                input = request.Input,
                player = request.PlayerSteamId is null ? null : new { steamId = request.PlayerSteamId },
                method = request.Method,
                endpoint = request.Endpoint,
                revision = request.Revision,
            });
            // The prelude's saved JSON.parse builds req, so a key such as "__proto__" stays an own property.
            var reqValue = engine.Call(environment.Get("parseRequest"), JsValue.Undefined, new JsValue[] { requestJson });

            engine.SetValue("ns", ns);
            engine.SetValue("req", reqValue);
            engine.SetValue("workflows", environment.Get("workflows"));
            engine.SetValue("globals", environment.Get("globals"));

            var handler = engine.Modules.Import(ModuleSpecifier).Get("default");
            if (handler is not Function handlerFunction)
                return new EndpointScriptOutcome(EndpointScriptOutcomeKind.ScriptError, null,
                    "The script must `export default function handle(req, ns)`.");

            var result = engine.Call(handlerFunction, JsValue.Undefined, new JsValue[] { reqValue, ns });
            var json = engine.Call(serialize, JsValue.Undefined, new JsValue[] { result });
            if (json.IsUndefined())
                return new EndpointScriptOutcome(EndpointScriptOutcomeKind.Returned, null);

            var text = json.AsString();
            var responseBytes = Encoding.UTF8.GetByteCount(text);
            if (responseBytes > Limits.MaxResponseBytes)
                return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null,
                    $"Response is {responseBytes} bytes; the limit is {Limits.MaxResponseBytes}.");

            using var document = JsonDocument.Parse(text);
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.Returned, document.RootElement.Clone());
        }
        catch (Exception error) when (!IsCancellation(error) || !budget.ClientCancelled)
        {
            return Classify(error);
        }
        finally
        {
            engine?.Dispose();
        }
    }

    /// <summary>Engine options for an endpoint run. Statements, time and memory come from <paramref name="budget"/>, shared by every engine entry of the request. Internal so tests can assert the guards are on before running anything.</summary>
    internal Options CreateOptions(ScriptRunBudget budget, IModuleLoader? loader)
    {
        var options = new Options
        {
            Strict = true,
        };
        options.Constraint(budget.CreateStatementConstraint())
            .LimitMemory(budget.Limits.MemoryBytes)
            .LimitRecursion(budget.Limits.MaxRecursionDepth)
            .DisableStringCompilation(true)
            .CancellationToken(budget.Token);
        ScriptEngineGuards.Configure(options, budget.Limits.Timeout);
        if (loader is not null)
            options.EnableModules(loader);
        return options;
    }

    private Engine CreateEngine(ScriptRunBudget budget, IModuleLoader? loader)
    {
        var engine = new Engine(CreateOptions(budget, loader));
        ScriptEngineGuards.Apply(engine, keepBinaryBuffers: false);
        return engine;
    }

    private Prepared<Acornima.Ast.Module> Prepare(string source, EndpointScriptLimits limits)
    {
        // The regex timeout is baked into the prepared module, so it is part of the cache key.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))) + "@" + limits.Timeout.Ticks;
        if (_prepared.TryGet(key, out var cached))
            return cached;
        var prepared = Engine.PrepareModule(source, ModuleSpecifier, ScriptEngineGuards.ModulePreparation(limits.Timeout));
        _prepared.Set(key, prepared);
        return prepared;
    }

    /// <summary>
    /// Finds the source location of a parse failure. Jint wraps Acornima's errors, so the location is taken from the
    /// innermost parse error that carries one: an Acornima <c>ParseErrorException</c>, or a <c>SyntaxErrorException</c>
    /// read from its <c>(source:line:column)</c> suffix.
    /// </summary>
    private static EndpointScriptDiagnostic? SyntaxDiagnostic(Exception error)
    {
        if (error.InnerException is { } inner && SyntaxDiagnostic(inner) is { } nested)
            return nested;
        if (error is ParseErrorException parse)
            return new EndpointScriptDiagnostic(parse.LineNumber, parse.Column + 1, parse.Description);
        if (error is SyntaxErrorException)
        {
            var match = SyntaxLocation.Match(error.Message);
            if (match.Success)
                return new EndpointScriptDiagnostic(
                    int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
                    match.Groups["message"].Value);
        }
        return null;
    }

    /// <summary>True for the cancellations Jint raises when the budget's token fires. Jint's ExecutionCanceledException derives from JintException, not OperationCanceledException, so both must be matched.</summary>
    private static bool IsCancellation(Exception error) => error is OperationCanceledException or ExecutionCanceledException;

    internal EndpointScriptOutcome Classify(Exception error)
    {
        if (SyntaxDiagnostic(error) is { } syntax)
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.ScriptError, null,
                $"line {syntax.Line}, column {syntax.Column}: {syntax.Message}");
        if (error is JavaScriptException script)
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.ScriptError, null, LocationPrefix(error) + script.Message);
        if (error is TimeoutException || IsCancellation(error))
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, "Time limit exceeded.");
        if (error is ScriptLimitExceededException limit)
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, limit.Message);

        // Jint's statement, memory and recursion guards throw internal exception types; match them by name.
        var name = error.GetType().Name;
        if (name.Contains("Statement", StringComparison.Ordinal))
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, "Statement limit exceeded.");
        if (name.Contains("Recursion", StringComparison.Ordinal) || name.Contains("StackOverflow", StringComparison.Ordinal))
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, "Recursion limit exceeded.");
        if (name.Contains("Memory", StringComparison.Ordinal))
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, "Memory limit exceeded.");

        if (RegexTimeout(error))
            return new EndpointScriptOutcome(EndpointScriptOutcomeKind.LimitExceeded, null, "Regular expression time limit exceeded.");

        // An unclassified fault: the owner gets the type and message; the full trace goes to the server log.
        _logger?.LogError(error, "Endpoint script failed with an unclassified error");
        return new EndpointScriptOutcome(EndpointScriptOutcomeKind.ScriptError, null, EndpointScriptHost.DescribeForOwner(error));
    }

    private static bool RegexTimeout(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is RegexMatchTimeoutException)
                return true;
        }
        return false;
    }

    private static string LocationPrefix(Exception error) =>
        JintException.TryGetJavaScriptLocation(error, out var location)
            ? $"line {location.Start.Line}, column {location.Start.Column + 1}: "
            : string.Empty;

    /// <summary>
    /// Builds <c>ns</c>, <c>workflows</c> and <c>globals</c>. <c>ns</c> has one function per public host member. <c>workflows</c>
    /// has one function per workflow alias. <c>globals</c> has one lazy getter per global name, and the getter imports
    /// the module on first access, so a script pays only for the modules it reads.
    /// </summary>
    private static string BuildPrelude()
    {
        var builder = new StringBuilder();
        builder.Append("(function (importGlobal) {\n");
        builder.Append("  var hostCall = __hostCall;\n");
        builder.Append("  delete globalThis.__hostCall;\n");
        builder.Append("  var stringify = JSON.stringify;\n");
        builder.Append("  var parse = JSON.parse;\n");
        builder.Append("  var slice = Array.prototype.slice;\n");
        builder.Append("  var freeze = Object.freeze;\n");
        builder.Append("  function call(name, args) {\n");
        builder.Append("    var reply = parse(hostCall(name, stringify(args)));\n");
        builder.Append("    if (!reply.ok) throw new Error(reply.error);\n");
        builder.Append("    return reply.value;\n");
        builder.Append("  }\n");
        builder.Append("  function api(name) {\n");
        builder.Append("    return function () { return call(name, slice.call(arguments)); };\n");
        builder.Append("  }\n");
        builder.Append("  var ns = {};\n");

        var groups = EndpointScriptHost.PublicApiNames.Where(name => name.Contains('.')).GroupBy(name => name[..name.IndexOf('.')]);
        foreach (var group in groups)
        {
            builder.Append($"  ns[{JsonSerializer.Serialize(group.Key)}] = freeze({{ ");
            builder.Append(string.Join(", ", group.Select(name =>
                $"{JsonSerializer.Serialize(name[(name.IndexOf('.') + 1)..])}: api({JsonSerializer.Serialize(name)})")));
            builder.Append(" });\n");
        }
        foreach (var name in EndpointScriptHost.PublicApiNames.Where(name => !name.Contains('.')))
            builder.Append($"  ns[{JsonSerializer.Serialize(name)}] = api({JsonSerializer.Serialize(name)});\n");

        builder.Append("  var catalog = call(\"workflows.catalog\", []);\n");
        builder.Append("  var workflows = {};\n");
        builder.Append("  Object.keys(catalog).forEach(function (alias) {\n");
        builder.Append("    workflows[alias] = (function (id) {\n");
        builder.Append("      return function (input) { return call(\"workflows.run\", [id, input === undefined ? {} : input]); };\n");
        builder.Append("    })(catalog[alias]);\n");
        builder.Append("  });\n");

        builder.Append("  var names = call(\"globals.catalog\", []);\n");
        builder.Append("  var loaded = {};\n");
        builder.Append("  var globals = {};\n");
        builder.Append("  Object.keys(names).forEach(function (name) {\n");
        builder.Append("    Object.defineProperty(globals, name, { enumerable: true, get: function () {\n");
        builder.Append("      if (!Object.prototype.hasOwnProperty.call(loaded, name)) loaded[name] = importGlobal(names[name]);\n");
        builder.Append("      return loaded[name];\n");
        builder.Append("    } });\n");
        builder.Append("  });\n");

        builder.Append("  return {\n");
        builder.Append("    ns: freeze(ns),\n");
        builder.Append("    workflows: freeze(workflows),\n");
        builder.Append("    globals: freeze(globals),\n");
        builder.Append("    parseRequest: function (text) { return parse(text); },\n");
        builder.Append("    serialize: function (value) { return stringify(value); }\n");
        builder.Append("  };\n");
        builder.Append("})");
        return builder.ToString();
    }
}
