using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native candidate for GET StorageRecord routes. Handles two shapes:
/// <list type="bullet">
///   <item><description><c>GET .../:collectionId/:key</c> — direct key read, mirrors Bun's <c>routeStorageApiGet</c>.
///         Reads <c>{collectionId}/data/{key}/saved.json</c>.</description></item>
///   <item><description><c>GET .../:collectionId/:steamId/records</c> — player records list, mirrors Bun's <c>routeStorageApiListRecords</c>.
///         Reads <c>{collectionId}/data/{steamId}/record-index.json</c>.</description></item>
/// </list>
/// Resolves API-key auth, reads the resource via <see cref="IBunnyWorkspaceClient.GetProjectResourceAsync{T}"/>,
/// and returns Bun-compatible JSON shapes and public error codes. Read-only; never writes.
/// </summary>
/// <remarks>
/// Collection visibility (public vs private) is assumed public (isPrivate=false) in this pass.
/// Private-visibility resolution is a follow-up.
/// </remarks>
public sealed class StorageRecordReadCandidateHandler : INetworkStorageCandidateHandler
{
    private const string WikiBase = "https://sboxcool.com/wiki/network-storage-v3";

    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly IBunnyWorkspaceClient _workspaceClient;

    public StorageRecordReadCandidateHandler(
        IStorageApiKeyResolver apiKeyResolver,
        IBunnyWorkspaceClient workspaceClient)
    {
        _apiKeyResolver = apiKeyResolver;
        _workspaceClient = workspaceClient;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageRecord;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageRecord
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;

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

        if (auth is null)
        {
            return UnauthorizedResult();
        }

        var ownerUserId = auth.UserId;
        var keyType = auth.KeyType;

        var collectionId = request.RouteParameter("collectionId") ?? string.Empty;

        // ── Branch on route shape ──
        // (a) key parameter present => direct key read (template ends /:collectionId/:key)
        // (b) steamId parameter present => player records list (template ends /:collectionId/:steamId/records)
        var key = request.RouteParameter("key");
        var steamId = request.RouteParameter("steamId");

        if (key is not null)
        {
            return await HandleKeyReadAsync(ownerUserId, projectId, collectionId, key, keyType, request.CancellationToken);
        }

        if (steamId is not null)
        {
            return await HandleRecordsListAsync(ownerUserId, projectId, collectionId, steamId, keyType, request.CancellationToken);
        }

        // Should never reach here for a classified StorageRecord GET route
        return NetworkStorageCandidateResult.Error(
            500,
            "INTERNAL_ERROR",
            new { error = new { code = "INTERNAL_ERROR", message = "Unrecognized StorageRecord GET route shape." } },
            authDecision: keyType);
    }

    private async Task<NetworkStorageCandidateResult> HandleKeyReadAsync(
        long userId, string projectId, string collectionId, string key, string authDecision, CancellationToken ct)
    {
        var relativePath = $"{collectionId}/data/{key}/saved.json";
        var fullStoragePath = $"network-storage/users/{userId}/{projectId}/{relativePath}";
        var storagePathsRead = new[] { fullStoragePath };

        JsonElement data;
        try
        {
            data = await _workspaceClient.GetProjectResourceAsync<JsonElement>(userId, projectId, relativePath, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return NetworkStorageCandidateResult.Error(
                500,
                "INTERNAL_ERROR",
                new { error = new { code = "INTERNAL_ERROR", message = "Storage read failed." } },
                storagePathsRead,
                authDecision);
        }

        // GetProjectResourceAsync returns default(JsonElement) (ValueKind=Undefined) for missing keys.
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return NetworkStorageCandidateResult.Error(
                404,
                "NOT_FOUND",
                new
                {
                    error = new
                    {
                        code = "NOT_FOUND",
                        message = "Key not found.",
                        docsUrl = $"{WikiBase}/error-codes#collection-errors"
                    }
                },
                storagePathsRead,
                authDecision);
        }

        // Success: return the stored JSON value directly, matching Bun's routeStorageApiGet.
        return NetworkStorageCandidateResult.Ok(
            data,
            storagePathsRead,
            authDecision);
    }

    private async Task<NetworkStorageCandidateResult> HandleRecordsListAsync(
        long userId, string projectId, string collectionId, string steamId, string authDecision, CancellationToken ct)
    {
        var relativePath = $"{collectionId}/data/{steamId}/record-index.json";
        var fullStoragePath = $"network-storage/users/{userId}/{projectId}/{relativePath}";
        var storagePathsRead = new[] { fullStoragePath };

        JsonElement index;
        try
        {
            index = await _workspaceClient.GetProjectResourceAsync<JsonElement>(userId, projectId, relativePath, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return NetworkStorageCandidateResult.Error(
                500,
                "INTERNAL_ERROR",
                new { error = new { code = "INTERNAL_ERROR", message = "Storage read failed." } },
                storagePathsRead,
                authDecision);
        }

        IReadOnlyList<JsonElement> records;
        int maxRecords;

        if (index.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            // No index yet — Bun would create an empty one on write. For read-only, return empty list.
            records = Array.Empty<JsonElement>();
            maxRecords = 1;
        }
        else
        {
            // Parse records from the index. Bun reads index.records as the list.
            // maxRecords comes from the collection metadata (not read in this pass); default to 1.
            if (index.TryGetProperty("records", out var recordsProp) && recordsProp.ValueKind == JsonValueKind.Array)
            {
                records = recordsProp.EnumerateArray().ToList();
            }
            else
            {
                records = Array.Empty<JsonElement>();
            }
            maxRecords = 1;
        }

        // Match Bun's routeStorageApiListRecords response shape.
        // NOTE: Bun reads maxRecords from collection metadata. Defaulting to 1 per the Bun fallback.
        return NetworkStorageCandidateResult.Ok(
            new
            {
                records,
                maxRecords
            },
            storagePathsRead,
            authDecision);
    }

    private static NetworkStorageCandidateResult UnauthorizedResult()
    {
        return NetworkStorageCandidateResult.Error(
            401,
            "UNAUTHORIZED",
            new
            {
                error = new
                {
                    code = "UNAUTHORIZED",
                    message = "Invalid or missing API key.",
                    docsUrl = $"{WikiBase}/quick-start#3-configure-the-library"
                }
            },
            authDecision: "denied");
    }
}
