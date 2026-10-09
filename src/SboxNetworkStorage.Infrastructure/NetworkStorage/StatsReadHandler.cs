using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// Read-only native handler for <c>GET /api/storage/:projectId/stats/:steamId</c>.
/// Mirrors legacy server's <c>routeStorageApiStatsGet</c>: resolves the API key and returns the raw
/// player-stats object. Read-only; never mutates.
///
/// Store-authoritative: <c>NativeStatsHeartbeatHandler</c> persists through
/// <see cref="INetworkStorageDataPlane"/> (collection <c>player-stats</c>, key = bare
/// steamId), so reads go through the same data plane — guaranteeing the read routes to
/// the same table the write did. A miss is a real miss (404); absent stats are never
/// synthesized or zero-filled. The legacy workspace file path is retained only for callers
/// that construct the handler without a data plane (existing unit tests); production
/// always injects the data plane.
/// </summary>
public sealed class StatsReadHandler : INetworkStorageHandler
{
    /// <summary>Collection ID the heartbeat producer writes player-stats records under.</summary>
    private const string StatsCollectionId = "player-stats";

    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IWorkspaceStore _workspaceClient;
    private readonly INetworkStorageDataPlane? _dataPlane;

    public StatsReadHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IWorkspaceStore workspaceClient,
        INetworkStorageDataPlane? dataPlane = null)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
        _dataPlane = dataPlane;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Stats;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Stats
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var steamId = request.RouteParameter("steamId");

        if (string.IsNullOrEmpty(steamId))
        {
            return NetworkStorageResult.Error(
                400,
                "INVALID_KEY",
                new { error = new { code = "INVALID_KEY", message = "Steam ID must be numeric." } },
                authDecision: "denied");
        }

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult();
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult();
        }

        if (auth is null || !auth.Enabled)
        {
            return UnauthorizedResult();
        }

        var ownerUserId = auth.UserId;
        var keyType = auth.KeyType;

        // Store-authoritative read (the heartbeat persists via the data plane; the
        // obsolete workspace player-stats file never sees those rows).
        if (_dataPlane is not null)
        {
            return await ReadFromDataPlaneAsync(
                _dataPlane, ownerUserId, projectId, steamId, keyType, request.CancellationToken);
        }

        // ── Read player-stats file ──
        var statsRelPath = $"player-stats/{steamId}.json";
        var storagePathsRead = new[] { $"network-storage/users/{ownerUserId}/{projectId}/{statsRelPath}" };

        JsonElement stats;
        try
        {
            stats = await _workspaceClient.GetProjectResourceAsync<JsonElement>(
                ownerUserId, projectId, statsRelPath, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                500,
                "ENDPOINT_CONFIG_ERROR",
                new { error = new { code = "ENDPOINT_CONFIG_ERROR", message = "Endpoint configuration error. Check step collection references and field names." } },
                storagePathsRead,
                authDecision: keyType);
        }

        // Not found → NOT_FOUND (matching legacy server)
        if (stats.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "No stats found for this player." } },
                storagePathsRead,
                authDecision: keyType);
        }

        // Return the raw stats object (matching legacy server's formatResponse(request, stats))
        return NetworkStorageResult.Ok(
            stats,
            storagePathsRead,
            authDecision: keyType);
    }

    /// <summary>
    /// Read the player-stats record through the same data plane the heartbeat writes,
    /// returning the raw stats object it persisted (matching legacy server's
    /// <c>formatResponse(request, stats)</c>). Absent stats stay a 404 — never
    /// synthesized or zero-filled.
    /// </summary>
    private static async Task<NetworkStorageResult> ReadFromDataPlaneAsync(
        INetworkStorageDataPlane dataPlane, long ownerUserId, string projectId, string steamId,
        string keyType, CancellationToken cancellationToken)
    {
        var storagePathsRead = new[] { $"store://{projectId}/{StatsCollectionId}/{steamId}" };

        RecordReadResult read;
        try
        {
            read = await dataPlane.ReadRecordAsync(
                ownerUserId, projectId, StatsCollectionId, steamId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                500,
                "STORAGE_ERROR",
                new { error = new { code = "STORAGE_ERROR", message = "A storage operation failed." } },
                storagePathsRead,
                authDecision: keyType);
        }

        // Not found → NOT_FOUND (matching legacy server). The data plane already treats
        // soft-deleted tombstones as missing.
        if (!read.Found)
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "No stats found for this player." } },
                storagePathsRead,
                authDecision: keyType);
        }

        return NetworkStorageResult.Ok(
            read.Value,
            storagePathsRead,
            authDecision: keyType);
    }

    private static NetworkStorageResult UnauthorizedResult()
    {
        return NetworkStorageResult.Error(
            401,
            "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            authDecision: "denied");
    }
}
