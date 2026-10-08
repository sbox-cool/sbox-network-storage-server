using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dry-run native candidate for StorageGlobal mutation routes (POST append).
/// Mirrors the Bun handler <c>routeV3GlobalAppend</c>.
///
/// Computes the production write path for the old storage layout and returns a dry-run
/// suppression marker. Never writes production state.
/// </summary>
/// <remarks>
/// Covers:
/// <list type="bullet">
///   <item><description><c>POST /:collectionId/append</c> on all version aliases (v1/v3)</description></item>
/// </list>
/// The global append creates a new record file under <c>{collectionBase}/global/{recordId}.json</c>.
/// The actual record ID is generated server-side (<c>generateId()</c> / <c>crypto.randomUUID</c>);
/// the dry-run reports a placeholder record ID pattern.
/// </remarks>
public sealed class StorageGlobalMutationCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;

    public StorageGlobalMutationCandidateHandler(IStorageApiKeyResolver apiKeyResolver)
    {
        _apiKeyResolver = apiKeyResolver;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageGlobal;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageGlobal
        && string.Equals(route.Method, "POST", StringComparison.OrdinalIgnoreCase);

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
        return HandleAppend(auth, projectId, collectionId);
    }

    private static NetworkStorageCandidateResult HandleAppend(
        StorageApiKeyAuthResult auth,
        string projectId,
        string collectionId)
    {
        var userId = auth.UserId;
        var colBase = CollectionBasePath(userId, projectId, collectionId, isPrivate: false);

        // The actual recordId is generated at runtime via crypto.randomUUID (6-char hex).
        // We use a placeholder pattern to indicate the write will go to a generated file.
        var globalRecordPath = $"{colBase}/global/{{generatedRecordId}}.json";

        var intendedWrites = new List<string> { globalRecordPath };

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "append",
            method = "POST",
            projectId,
            collectionId,
            source = "candidate",
            _note = "Production append would create a new global record file with a server-generated record ID.",
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

    /// <summary>Old-layout collection base path.</summary>
    private static string CollectionBasePath(long userId, string projectId, string collectionId, bool isPrivate)
    {
        var escapedProject = Uri.EscapeDataString(projectId);
        var escapedCollection = Uri.EscapeDataString(collectionId);
        return isPrivate
            ? $"network-storage/users/{userId}/{escapedProject}/private/{escapedCollection}"
            : $"network-storage/users/{userId}/{escapedProject}/{escapedCollection}";
    }
}
