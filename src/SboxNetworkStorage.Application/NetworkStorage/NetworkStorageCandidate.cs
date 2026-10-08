namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Data-plane credentials extracted from a Network Storage request (API key, Steam id, auth-session
/// token, session token, encrypted request id). Carried on the candidate request so auth-requiring
/// families can resolve the project owner via <c>IStorageApiKeyResolver</c> without an HttpContext.
/// </summary>
public sealed record NetworkStorageCredentials(
    string? ApiKey,
    string? SteamId,
    string? AuthSessionToken,
    string? SessionToken,
    string? EncryptedRequestId)
{
    public static readonly NetworkStorageCredentials None = new(null, null, null, null, null);

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}

/// <summary>
/// Shared, transport-agnostic request context for a Network Storage native candidate. Resolved once
/// from the classified route and forwarded to the family handler. Carries no <c>HttpContext</c> so the
/// same context can be produced from a live request or a shadow replay.
/// </summary>
public sealed record NetworkStorageCandidateRequest(
    NetworkStorageRouteClassification Route,
    IReadOnlyDictionary<string, string> Query,
    string? ContentType,
    IReadOnlyDictionary<string, bool> AuthSignals,
    NetworkStorageCredentials Credentials,
    string? Body,
    long? ResolvedOwnerUserId,
    CancellationToken CancellationToken)
{
    public string Method => Route.Method;

    public NetworkStorageRouteFamily? Family => Route.Family;

    public string? ProjectId => Route.ProjectId;

    public string? SteamId => Route.SteamId;

    public string? RouteParameter(string name) => Route.RouteParameters.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// When <c>false</c>, mutation candidates may perform real production writes.
    /// The default (<c>true</c>) preserves dry-run behavior for shadow diagnostics.
    /// </summary>
    public bool SuppressSideEffects { get; init; } = true;
}

/// <summary>
/// Bun-compatible result of executing a native candidate. <see cref="StoragePathsRead"/> and
/// <see cref="IntendedWritePaths"/> feed the shadow side-effect diagnostics; production writes are
/// suppressed by the caller unless the runtime mode allows them.
/// </summary>
public sealed record NetworkStorageCandidateResult(
    int StatusCode,
    string? PublicErrorCode,
    object? Body,
    IReadOnlyList<string> StoragePathsRead,
    IReadOnlyList<string> IntendedWritePaths,
    string? AuthDecision)
{
    private static readonly IReadOnlyList<string> NoPaths = Array.Empty<string>();

    public static NetworkStorageCandidateResult Ok(object body, IReadOnlyList<string>? storagePathsRead = null, string? authDecision = null) =>
        new(200, null, body, storagePathsRead ?? NoPaths, NoPaths, authDecision);

    public static NetworkStorageCandidateResult Error(int statusCode, string publicErrorCode, object body, IReadOnlyList<string>? storagePathsRead = null, string? authDecision = null) =>
        new(statusCode, publicErrorCode, body, storagePathsRead ?? NoPaths, NoPaths, authDecision);
}

/// <summary>
/// A native candidate for one Network Storage route family. Handlers are read-only at this stage of the
/// migration; mutation dry-run handlers arrive with the write-safety service.
/// </summary>
public interface INetworkStorageCandidateHandler
{
    NetworkStorageRouteFamily Family { get; }

    /// <summary>True when this handler owns the classified route (family + method).</summary>
    bool CanHandle(NetworkStorageRouteClassification route);

    Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request);
}

/// <summary>Resolves and runs the native candidate handler for a classified Network Storage request.</summary>
public interface INetworkStorageReadCandidateExecutor
{
    bool HasHandler(NetworkStorageRouteClassification route);

    Task<NetworkStorageCandidateResult?> TryExecuteAsync(NetworkStorageCandidateRequest request);
}

public sealed class NetworkStorageReadCandidateExecutor : INetworkStorageReadCandidateExecutor
{
    private readonly IReadOnlyList<INetworkStorageCandidateHandler> _handlers;

    public NetworkStorageReadCandidateExecutor(IEnumerable<INetworkStorageCandidateHandler> handlers)
    {
        _handlers = handlers.ToList();
    }

    public bool HasHandler(NetworkStorageRouteClassification route) => Resolve(route) is not null;

    public async Task<NetworkStorageCandidateResult?> TryExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var handler = Resolve(request.Route);
        if (handler is null)
        {
            return null;
        }

        return await handler.ExecuteAsync(request);
    }

    private INetworkStorageCandidateHandler? Resolve(NetworkStorageRouteClassification route)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(route))
            {
                return handler;
            }
        }

        return null;
    }
}
