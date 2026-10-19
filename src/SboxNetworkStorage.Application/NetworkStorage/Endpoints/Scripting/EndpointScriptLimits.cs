namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Per-invocation sandbox limits. The defaults are deliberately tight: an endpoint is a request handler, not a
/// batch job. Operators can raise them in server.toml (<c>[endpoints]</c>); they are never unbounded.
/// </summary>
public sealed record EndpointScriptLimits(
    TimeSpan Timeout,
    int MaxStatements,
    long MemoryBytes,
    int MaxRecursionDepth,
    int MaxSourceBytes,
    int MaxResponseBytes,
    int MaxLogBytes)
{
    public static EndpointScriptLimits Defaults { get; } = new(
        Timeout: TimeSpan.FromSeconds(1),
        MaxStatements: 1_000_000,
        MemoryBytes: 64L * 1024 * 1024,
        MaxRecursionDepth: 200,
        MaxSourceBytes: 64 * 1024,
        MaxResponseBytes: 256 * 1024,
        MaxLogBytes: 8 * 1024);
}
