namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Data-plane credentials extracted from a Network Storage request (API key, Steam id, auth-session
/// token, session token, encrypted request id). Carried on the request so auth-requiring
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
/// Shared, transport-agnostic context resolved from a classified Network Storage route and
/// forwarded directly to its handler, without carrying an <c>HttpContext</c>.
/// </summary>
public sealed record NetworkStorageRequest(
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

}

/// <summary>
/// Client-compatible response and authentication outcome from a Network Storage handler.
/// </summary>
public sealed record NetworkStorageResult(
    int StatusCode,
    string? PublicErrorCode,
    object? Body,
    IReadOnlyList<string> StoragePathsRead,
    IReadOnlyList<string> IntendedWritePaths,
    string? AuthDecision)
{
    private static readonly IReadOnlyList<string> NoPaths = Array.Empty<string>();

    public static NetworkStorageResult Ok(object body, IReadOnlyList<string>? storagePathsRead = null, string? authDecision = null) =>
        new(200, null, body, storagePathsRead ?? NoPaths, NoPaths, authDecision);

    public static NetworkStorageResult Error(int statusCode, string publicErrorCode, object body, IReadOnlyList<string>? storagePathsRead = null, string? authDecision = null) =>
        new(statusCode, publicErrorCode, body, storagePathsRead ?? NoPaths, NoPaths, authDecision);
}

/// <summary>
/// Executes requests for one Network Storage route family.
/// </summary>
public interface INetworkStorageHandler
{
    NetworkStorageRouteFamily Family { get; }

    /// <summary>True when this handler owns the classified route (family + method).</summary>
    bool CanHandle(NetworkStorageRouteClassification route);

    Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request);
}

