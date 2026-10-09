using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

/// <summary>
/// Native .NET implementation of POST /v3/manage/{projectId}/revision-init.
/// Client contract: <c>sbox-cool/sbox-network-storage</c>
/// (<c>Code/Core/NetworkStorageRevisionInit.cs</c>) sends a one-time handshake
/// at game startup with the running revision; the server compares it against
/// the synced game package and reports whether the client is outdated.
/// Public-key route: the game client authenticates with its public game key,
/// so no management scope is required. Read-only; never mutates the store.
/// </summary>
public sealed class RevisionInitHandler(
    IWorkspaceStore workspaceClient,
    IStorageApiKeyResolver apiKeyResolver,
    INetworkStorageProjectService projectService,
    ILogger<RevisionInitHandler> logger)
{
    public async Task<IResult> HandleAsync(HttpContext context, string projectId, CancellationToken cancellationToken)
    {
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var keyHeader) && !string.IsNullOrWhiteSpace(keyHeader)
            ? keyHeader.ToString()
            : context.Request.Query.TryGetValue("apiKey", out var queryKey) && !string.IsNullOrWhiteSpace(queryKey)
                ? queryKey.ToString()
                : context.Request.Headers.TryGetValue("x-public-key", out var publicHeader) ? publicHeader.ToString() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
            return RevisionInitError(context, 401, "UNAUTHORIZED", "Missing API key. Send the public game key in x-api-key, ?apiKey=, or x-public-key.");

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "API key resolution failed for project {ProjectId}", projectId);
            return RevisionInitError(context, 401, "UNAUTHORIZED", "Invalid API key.");
        }

        if (auth is null || !auth.Enabled)
            return RevisionInitError(context, 401, "UNAUTHORIZED", "Invalid API key.");

        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, cancellationToken);
        if (access is null)
            return RevisionInitError(context, 401, "UNAUTHORIZED", "Invalid API key.");
        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

        if (!access.Project.Enabled)
            return RevisionInitError(context, 403, "PROJECT_DISABLED", "This project is currently disabled.");

        JsonElement body = default;
        if (context.Request.ContentLength != 0)
        {
            try
            {
                body = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return RevisionInitError(context, 400, "INVALID_JSON", "Request body must be valid JSON.");
            }
        }

        // Client revision: body field first (client library sends revisionId),
        // then the x-ns-revision-id header. Absent revision is not an error;
        // the handshake still acknowledges with the server's latest.
        long? clientRevision = null;
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("revisionId", out var revisionProp)
            && revisionProp.ValueKind == JsonValueKind.Number
            && revisionProp.TryGetInt64(out var bodyRevision))
        {
            clientRevision = bodyRevision;
        }
        else if (context.Request.Headers.TryGetValue("x-ns-revision-id", out var revisionHeader)
            && long.TryParse(revisionHeader.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var headerRevision))
        {
            clientRevision = headerRevision;
        }

        var gamePackage = await ReadGamePackageAsync(auth.UserId, projectId, cancellationToken);
        long? currentRevision = gamePackage is not null
            && gamePackage.TryGetValue("currentRevisionId", out var currentProp)
            && currentProp.ValueKind == JsonValueKind.Number
            && currentProp.TryGetInt64(out var current)
            ? current
            : null;
        long? latestRevision = gamePackage is not null
            && gamePackage.TryGetValue("latestRevisionId", out var latestProp)
            && latestProp.ValueKind == JsonValueKind.Number
            && latestProp.TryGetInt64(out var latest)
            ? latest
            : currentRevision;

        var outdated = clientRevision.HasValue && currentRevision.HasValue && clientRevision.Value < currentRevision.Value;
        string? message = (clientRevision, currentRevision) switch
        {
            (null, _) => "Client revision unknown; reporting the server's latest revision.",
            (_, null) => "No synced game package; nothing to compare against.",
            _ when outdated => $"Revision {clientRevision} is outdated. Latest is {currentRevision}.",
            _ => "Revision is current.",
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Json(new
        {
            ok = true,
            playerRevision = clientRevision,
            currentRevisionId = currentRevision,
            latestRevisionId = latestRevision,
            revisionOutdated = outdated,
            message,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task<Dictionary<string, JsonElement>?> ReadGamePackageAsync(long userId, string projectId, CancellationToken ct)
    {
        try
        {
            return await workspaceClient.GetProjectResourceAsync<Dictionary<string, JsonElement>>(userId, projectId, "game-package.json", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No game package found for project {ProjectId}", projectId);
            return null;
        }
    }

    private static IResult RevisionInitError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return Results.Json(new { ok = false, error = code, message }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
