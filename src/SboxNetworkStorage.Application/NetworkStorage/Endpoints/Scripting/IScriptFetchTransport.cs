namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Performs the network side of <c>ns.fetch</c>: DNS, connect, redirects, timeouts and response limits. The
/// implementation owns every address check, so a name is never trusted to resolve to a safe address.
/// </summary>
public interface IScriptFetchTransport
{
    /// <summary>
    /// True when the operator allowed private, loopback and link-local destinations
    /// (<c>endpoints.fetch_private_networks</c>). Metadata and reserved addresses stay blocked either way.
    /// </summary>
    bool AllowPrivateNetworks { get; }

    /// <summary>
    /// Sends one request. Every hop is checked against <paramref name="policy"/> and the address rules. Returns a
    /// failure value for policy, network and limit problems. Throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct);
}

/// <summary>One outbound request, already validated and normalized by <see cref="ScriptFetchSession"/>.</summary>
public sealed record ScriptFetchRequest(
    string Url,
    string Method,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    int TimeoutMs);

/// <summary>A completed response. Header names are lower case; repeated values are joined with ", ".</summary>
public sealed record ScriptFetchResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A failed call. <see cref="Code"/> is the script-visible <c>FETCH_*</c> code. <see cref="Reason"/> is the
/// owner-facing cause (a block reason such as <c>host_not_allowed</c>, or <c>timeout</c>). <see cref="Host"/> is the
/// host of the hop that failed, which can be a redirect target.
/// </summary>
public sealed record ScriptFetchFailure(string Code, string Reason, string Host);

/// <summary>Either a response or a failure.</summary>
public sealed record ScriptFetchResult(ScriptFetchResponse? Response, ScriptFetchFailure? Failure)
{
    public static ScriptFetchResult Ok(ScriptFetchResponse response) => new(response, null);

    public static ScriptFetchResult Fail(ScriptFetchFailure failure) => new(null, failure);
}
