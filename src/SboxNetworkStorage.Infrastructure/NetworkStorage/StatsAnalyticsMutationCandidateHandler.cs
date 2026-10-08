using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dry-run native candidate for Stats and Analytics mutation routes.
/// Mirrors the Bun handlers <c>routeStorageApiStatsHeartbeat</c> and
/// <c>routeStorageApiAnalyticsEvent</c>.
///
/// Computes the production write paths for the old storage layout and returns a dry-run
/// suppression marker. Never writes production state.
/// </summary>
/// <remarks>
/// Covers:
/// <list type="bullet">
///   <item><description><c>POST /:projectId/stats/heartbeat</c> — player playtime stats update</description></item>
///   <item><description><c>POST /:projectId/analytics/events</c> — custom analytics event recording</description></item>
/// </list>
///
/// The stats heartbeat writes to the player stats file (<c>player-stats/{steamId}.json</c>).
/// The analytics event writes to the player analytics profile, event-day files, session files,
/// and the recent/project-index files.
/// </remarks>
public sealed class StatsAnalyticsMutationCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;

    public StatsAnalyticsMutationCandidateHandler(IStorageApiKeyResolver apiKeyResolver)
    {
        _apiKeyResolver = apiKeyResolver;
    }


    public bool CanHandle(NetworkStorageRouteClassification route) =>
        (route.Family == NetworkStorageRouteFamily.Stats
         || route.Family == NetworkStorageRouteFamily.Analytics)
        && string.Equals(route.Method, "POST", StringComparison.OrdinalIgnoreCase);

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Stats;
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

        if (request.Family == NetworkStorageRouteFamily.Stats)
        {
            return HandleStatsHeartbeat(auth, projectId, request);
        }

        if (request.Family == NetworkStorageRouteFamily.Analytics)
        {
            return HandleAnalyticsEvent(auth, projectId, request);
        }

        // Should never reach here
        return NetworkStorageCandidateResult.Error(
            500,
            "INTERNAL_ERROR",
            new
            {
                ok = false,
                error = new { code = "INTERNAL_ERROR", message = "Unrecognized Stats/Analytics route." },
                projectId,
                source = "candidate"
            },
            authDecision: auth.KeyType);
    }

    // ── Route shape handlers ──

    private static NetworkStorageCandidateResult HandleStatsHeartbeat(
        StorageApiKeyAuthResult auth,
        string projectId,
        NetworkStorageCandidateRequest request)
    {
        var userId = auth.UserId;

        // Resolve steamId from body or query
        var steamId = ResolveSteamId(request);

        // Player stats path
        var playerStatsPath = steamId is not null
            ? $"network-storage/users/{userId}/{projectId}/player-stats/{steamId}.json"
            : $"network-storage/users/{userId}/{projectId}/player-stats/{{steamId}}.json";

        var intendedWrites = new List<string> { playerStatsPath };

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "heartbeat",
            method = "POST",
            projectId,
            steamId,
            source = "candidate",
            _note = "Production heartbeat would update the player-stats file with playtime and session data.",
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

    private static NetworkStorageCandidateResult HandleAnalyticsEvent(
        StorageApiKeyAuthResult auth,
        string projectId,
        NetworkStorageCandidateRequest request)
    {
        var userId = auth.UserId;

        // Resolve steamId from body or query
        var steamId = ResolveSteamId(request);
        var steamIdDir = steamId ?? "{steamId}";

        // Analytics base path
        var analyticsBase = $"network-storage/users/{userId}/{projectId}/analytics";

        // Player analytics profile (read + write)
        var profilePath = $"{analyticsBase}/players/{steamIdDir}.json";

        // Event-day file (date-dependent — Bun writes to events/{steamId}/{date}.json)
        var eventDayPath = $"{analyticsBase}/events/{steamIdDir}/{{date}}.json";

        // Session file (sessionId-dependent — Bun writes to sessions/{steamId}/{sessionId}.json)
        var sessionPath = $"{analyticsBase}/sessions/{steamIdDir}/{{sessionId}}.json";

        // Recent player index
        var recentPath = $"{analyticsBase}/recent.json";

        var intendedWrites = new List<string>
        {
            profilePath,
            eventDayPath,
            sessionPath,
            recentPath
        };

        var dryRunMarker = new
        {
            ok = true,
            status = 200,
            mode = "candidate",
            reason = "write_suppressed_dry_run",
            action = "analytics-event",
            method = "POST",
            projectId,
            steamId,
            source = "candidate",
            _note = "Production analytics event would update the player profile, event-day file, session file, and recent index.",
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

    // ── Helpers ──

    /// <summary>Resolve steamId from request body JSON or query parameter.</summary>
    private static string? ResolveSteamId(NetworkStorageCandidateRequest request)
    {
        // Prefer query parameter
        var querySteamId = request.QueryValue("steamId");
        if (!string.IsNullOrEmpty(querySteamId))
            return querySteamId;

        // Fall back to credentials steamId
        if (!string.IsNullOrEmpty(request.Credentials.SteamId))
            return request.Credentials.SteamId;

        // Try body JSON
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("steamId", out var steamIdProp))
                {
                    if (steamIdProp.ValueKind == JsonValueKind.String)
                        return steamIdProp.GetString();
                    if (steamIdProp.ValueKind == JsonValueKind.Number)
                        return steamIdProp.GetInt64().ToString();
                }
            }
            catch (JsonException)
            {
                // Not parseable; ignore
            }
        }

        return null;
    }
}
