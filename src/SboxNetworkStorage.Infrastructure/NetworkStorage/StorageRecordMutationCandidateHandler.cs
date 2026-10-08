using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dry-run native candidate for StorageRecord mutation routes (POST, DELETE, PATCH).
/// Mirrors the Bun handlers (<c>routeStorageApiPost</c>, <c>routeStorageApiDeleteData</c>,
/// <c>routeStorageApiDeleteRecord</c>, <c>routeStorageApiRenameRecord</c>).
///
/// Computes the production write paths for the old storage layout and returns a dry-run
/// suppression marker. Never writes production state.
/// </summary>
/// <remarks>
/// Covers:
/// <list type="bullet">
///   <item><description><c>POST /:collectionId/:key</c> — direct key data save</description></item>
///   <item><description><c>DELETE /:collectionId/:key</c> — direct key data delete</description></item>
///   <item><description><c>DELETE /:collectionId/:steamId/records/:recordId</c> — player record delete</description></item>
///   <item><description><c>PATCH /:collectionId/:steamId/records/:recordId</c> — player record rename</description></item>
/// </list>
/// Collection visibility defaults to public (<c>isPrivate = false</c>) — private-visibility resolution
/// is a follow-up.
/// </remarks>
public sealed class StorageRecordMutationCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;

    public StorageRecordMutationCandidateHandler(IStorageApiKeyResolver apiKeyResolver)
    {
        _apiKeyResolver = apiKeyResolver;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageRecord;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageRecord
        && (string.Equals(route.Method, "POST", StringComparison.OrdinalIgnoreCase)
         || string.Equals(route.Method, "DELETE", StringComparison.OrdinalIgnoreCase)
         || string.Equals(route.Method, "PATCH", StringComparison.OrdinalIgnoreCase));

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult(projectId);
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult(projectId);
        }

        if (auth is null)
        {
            return UnauthorizedResult(projectId);
        }

        if (!auth.Enabled)
        {
            return ProjectDisabledResult(projectId);
        }

        var collectionId = request.RouteParameter("collectionId") ?? string.Empty;
        var key = request.RouteParameter("key");
        var steamId = request.RouteParameter("steamId");
        var recordId = request.RouteParameter("recordId");

        // ── Branch on route shape ──
        var methodIsPost = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var methodIsDelete = string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase);

        if (methodIsPost && key is not null)
        {
            return HandleKeyPost(auth, projectId, collectionId, key, request);
        }

        if (methodIsDelete && key is not null)
        {
            return HandleKeyDelete(auth, projectId, collectionId, key);
        }

        if (methodIsDelete && recordId is not null && steamId is not null)
        {
            return HandleRecordDelete(auth, projectId, collectionId, steamId, recordId);
        }

        // PATCH rename (only non-POST, non-DELETE mutation verb remaining)
        if (recordId is not null && steamId is not null)
        {
            return HandleRecordRename(auth, projectId, collectionId, steamId, recordId);
        }

        // Should never reach here for a classified StorageRecord mutation route
        return NetworkStorageCandidateResult.Error(
            500,
            "INTERNAL_ERROR",
            new
            {
                ok = false,
                error = new { code = "INTERNAL_ERROR", message = "Unrecognized StorageRecord mutation route shape." },
                projectId,
                source = "candidate"
            },
            authDecision: auth.KeyType);
    }

    // ── Route shape handlers ──

    private static NetworkStorageCandidateResult HandleKeyPost(
        StorageApiKeyAuthResult auth,
        string projectId,
        string collectionId,
        string key,
        NetworkStorageCandidateRequest request)
    {
        var userId = auth.UserId;
        var isPrivate = false;
        var colBase = CollectionBasePath(userId, projectId, collectionId, isPrivate);

        // Primary data write
        var savedFilePath = $"{colBase}/data/{key}/saved.json";
        var ledgerFilePath = $"{colBase}/data/{key}/logs/ledger.json";

        // Player stats path (uses base steamId from key if key contains _)
        var steamId = ResolveBaseSteamId(request, key);
        var intendedWrites = new List<string> { savedFilePath, ledgerFilePath };
        if (steamId is not null)
        {
            intendedWrites.Add($"network-storage/users/{userId}/{projectId}/player-stats/{steamId}.json");
        }

        // Unique index placeholder (field name unknown without reading schema)
        intendedWrites.Add($"{colBase}/unique-index/{{fieldName}}.json");

        // Auto-create record index entry if key has {steamId}_{recordId} pattern
        if (key.Contains("_"))
        {
            var steamIdBase = key.Split('_')[0];
            intendedWrites.Add($"{colBase}/data/{steamIdBase}/record-index.json");
        }

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "save",
            method = "POST",
            projectId,
            collectionId,
            key,
            source = "candidate",
            _note = "Production write would save to saved.json and update ledger, unique index, record index, and player stats.",
            intendedWrites
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private static NetworkStorageCandidateResult HandleKeyDelete(
        StorageApiKeyAuthResult auth,
        string projectId,
        string collectionId,
        string key)
    {
        var userId = auth.UserId;
        var colBase = CollectionBasePath(userId, projectId, collectionId, isPrivate: false);

        var savedFilePath = $"{colBase}/data/{key}/saved.json";
        var ledgerFilePath = $"{colBase}/data/{key}/logs/ledger.json";

        var intendedWrites = new List<string>
        {
            savedFilePath,
            ledgerFilePath,
            $"{colBase}/unique-index/{{fieldName}}.json"
        };

        // Update record index if key has steamId_recordId pattern
        if (key.Contains("_"))
        {
            var steamIdBase = key.Split('_')[0];
            intendedWrites.Add($"{colBase}/data/{steamIdBase}/record-index.json");
        }

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "delete",
            method = "DELETE",
            projectId,
            collectionId,
            key,
            source = "candidate",
            _note = "Production delete would remove saved.json, ledger, clean up unique index and record index entries.",
            intendedWrites
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private static NetworkStorageCandidateResult HandleRecordDelete(
        StorageApiKeyAuthResult auth,
        string projectId,
        string collectionId,
        string steamId,
        string recordId)
    {
        var userId = auth.UserId;
        var colBase = CollectionBasePath(userId, projectId, collectionId, isPrivate: false);

        // Data key is steamId_recordId (assuming non-legacy)
        var dataKey = $"{steamId}_{recordId}";

        var savedFilePath = $"{colBase}/data/{dataKey}/saved.json";
        var ledgerFilePath = $"{colBase}/data/{dataKey}/logs/ledger.json";
        var recordIndexPath = $"{colBase}/data/{steamId}/record-index.json";
        var uniqueIndexPath = $"{colBase}/unique-index/{{fieldName}}.json";

        var intendedWrites = new List<string>
        {
            savedFilePath,
            ledgerFilePath,
            recordIndexPath,
            uniqueIndexPath
        };

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "delete-record",
            method = "DELETE",
            projectId,
            collectionId,
            steamId,
            recordId,
            dataKey,
            source = "candidate",
            _note = "Production delete would remove record saved data, ledger, update record index, and clean up unique index.",
            intendedWrites
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    private static NetworkStorageCandidateResult HandleRecordRename(
        StorageApiKeyAuthResult auth,
        string projectId,
        string collectionId,
        string steamId,
        string recordId)
    {
        var userId = auth.UserId;
        var colBase = CollectionBasePath(userId, projectId, collectionId, isPrivate: false);

        var recordIndexPath = $"{colBase}/data/{steamId}/record-index.json";
        var intendedWrites = new List<string> { recordIndexPath };

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "rename-record",
            method = "PATCH",
            projectId,
            collectionId,
            steamId,
            recordId,
            source = "candidate",
            _note = "Production rename would update record-index.json with the new record name.",
            intendedWrites
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: dryRunMarker,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWrites,
            AuthDecision: "allowed");
    }

    // ── Shared error results ──

    private static NetworkStorageCandidateResult UnauthorizedResult(string projectId)
    {
        return NetworkStorageCandidateResult.Error(
            401,
            "UNAUTHORIZED",
            new
            {
                ok = false,
                status = 401,
                error = new { code = "UNAUTHORIZED", message = "Invalid or inactive API key for this project." },
                projectId,
                source = "candidate"
            },
            authDecision: "denied");
    }

    private static NetworkStorageCandidateResult ProjectDisabledResult(string projectId)
    {
        return NetworkStorageCandidateResult.Error(
            403,
            "PROJECT_DISABLED",
            new
            {
                ok = false,
                status = 403,
                error = new { code = "PROJECT_DISABLED", message = "This project is disabled. Enable it in your workspace settings." },
                projectId,
                source = "candidate"
            },
            authDecision: "denied");
    }

    // ── Path helpers ──

    /// <summary>
    /// Old-layout collection base path. Mirrors Bun's <c>collectionBasePath</c> from shared.js.
    /// <c>network-storage/users/{userId}/{projectId}/{collectionId}</c> for public,
    /// <c>network-storage/users/{userId}/{projectId}/private/{collectionId}</c> for private.
    /// </summary>
    private static string CollectionBasePath(long userId, string projectId, string collectionId, bool isPrivate)
    {
        var escapedProject = Uri.EscapeDataString(projectId);
        var escapedCollection = Uri.EscapeDataString(collectionId);
        return isPrivate
            ? $"network-storage/users/{userId}/{escapedProject}/private/{escapedCollection}"
            : $"network-storage/users/{userId}/{escapedProject}/{escapedCollection}";
    }

    /// <summary>Resolve base steamId from the request credentials or key pattern.</summary>
    private static string? ResolveBaseSteamId(NetworkStorageCandidateRequest request, string key)
    {
        if (!string.IsNullOrEmpty(request.Credentials.SteamId))
            return request.Credentials.SteamId;

        if (key.Contains("_"))
            return key.Split('_')[0];

        return null;
    }
}
