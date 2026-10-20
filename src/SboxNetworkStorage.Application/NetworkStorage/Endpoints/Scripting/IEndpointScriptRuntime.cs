using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// A language runtime for scripted endpoints (<c>runtime: &lt;Language&gt;</c> in the endpoint definition).
/// Implementations run untrusted source in a sandbox: no host objects, CLR types, I/O, network or timers are
/// reachable from script. Everything a script can do goes through <see cref="EndpointScriptHost"/>.
/// </summary>
public interface IEndpointScriptRuntime
{
    /// <summary>The value of <c>runtime:</c> that selects this runtime, for example <c>javascript</c>.</summary>
    string Language { get; }

    /// <summary>
    /// Whether scripted endpoints may run. On by default; the server kill switch is <c>endpoints.scripts</c> in
    /// server.toml. Parsing and validation still run when off, so owners see syntax errors before turning it on.
    /// </summary>
    bool Enabled { get; }

    /// <summary>Per-invocation resource limits. Shared by every script run through this runtime.</summary>
    EndpointScriptLimits Limits { get; }

    /// <summary>Parses <paramref name="source"/> without running it. An empty list means the source is valid.</summary>
    IReadOnlyList<EndpointScriptDiagnostic> Check(string source);

    /// <summary>
    /// Returns the JavaScript the engine runs for <paramref name="source"/>: the source itself for JavaScript, the
    /// type-stripped code for TypeScript. Callers run <see cref="Check"/> first. Throws <see cref="InvalidOperationException"/>
    /// when the source does not compile.
    /// </summary>
    string ToJavaScript(string source);

    /// <summary>
    /// Runs the endpoint handler once, synchronously, against <paramref name="host"/>. Every engine entry of the run
    /// draws from <paramref name="budget"/>, which is shared by all rounds of the same request.
    /// </summary>
    EndpointScriptOutcome Execute(string source, EndpointScriptRequest request, EndpointScriptHost host, ScriptRunBudget budget);
}

/// <summary>A syntax error with its 1-based position in the source.</summary>
public sealed record EndpointScriptDiagnostic(int Line, int Column, string Message);

/// <summary>The request a script handler receives as its first argument (<c>req</c>).</summary>
/// <param name="Input">The endpoint input (query string for GET, JSON body for POST), as the YAML steps see it.</param>
/// <param name="PlayerSteamId">The server-verified player identity, exposed as <c>req.player.steamId</c>; null (<c>req.player</c> is null) when anonymous or called with a secret key.</param>
/// <param name="Method">HTTP method, <c>GET</c> or <c>POST</c>.</param>
/// <param name="Endpoint">Endpoint slug.</param>
/// <param name="Revision">The game client's revision id, or null when the client did not send one.</param>
public sealed record EndpointScriptRequest(
    IReadOnlyDictionary<string, object?> Input,
    string? PlayerSteamId,
    string Method,
    string Endpoint,
    string? Revision);

public enum EndpointScriptOutcomeKind
{
    /// <summary>The handler returned; <see cref="EndpointScriptOutcome.Body"/> holds its JSON value (null when it returned nothing).</summary>
    Returned,
    /// <summary>The handler threw, or its source could not be run.</summary>
    ScriptError,
    /// <summary>A sandbox limit stopped the handler (time, memory, statements, recursion or response size).</summary>
    LimitExceeded,
}

/// <summary>What a script run produced. Host failures (<c>ns.fail</c>, storage errors) are read from the host, not here.</summary>
/// <param name="OwnerDetail">Full diagnostic for the owner's Errors page. Never shown to game clients.</param>
/// <param name="ErrorCode">The script-visible <c>FETCH_*</c> code when the uncaught error was an <c>ns.fetch</c> failure; null otherwise.</param>
public sealed record EndpointScriptOutcome(EndpointScriptOutcomeKind Kind, JsonElement? Body, string? OwnerDetail = null, string? ErrorCode = null);
