using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;

namespace SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

/// <summary>
/// Native .NET implementation of POST /v3/manage/{projectId}/package-sync.
/// Mirrors legacy server's routeManagePackageSync without relying on the decommissioned
/// storage-api legacy server backend. Persists the game package and promotes staged
/// revision overrides when the revision changes.
/// </summary>
public sealed class PackageSyncHandler(
    IWorkspaceStore workspaceClient,
    IStorageApiKeyResolver apiKeyResolver,
    ILogger<PackageSyncHandler> logger,
    ProjectMetadataCache metadataCache)
{
    // Write scopes aggregated by a package sync. Must stay aligned with
    // ManagementMutationHandler.AllManagementScopes.
    private static readonly string[] PackageSyncRequiredScopes =
        ["endpoints", "queries", "collections", "workflows", "game_values", "rate_limits", "settings"];

    public async Task<IResult> HandleAsync(HttpContext context, string projectId, CancellationToken cancellationToken)
    {
        var apiKey = context.Request.Headers.TryGetValue("x-api-key", out var keyHeader) ? keyHeader.ToString() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
            return PackageSyncError(context, 401, "UNAUTHORIZED", "Missing x-api-key header.");

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "API key resolution failed for project {ProjectId}", projectId);
            return PackageSyncError(context, 401, "UNAUTHORIZED", "Invalid or missing management API key.");
        }

        if (auth is null || !auth.Enabled || !string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase))
            return PackageSyncError(context, 401, "UNAUTHORIZED", "Invalid or missing management API key.");

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);

        // Package sync publishes the game package and promotes staged revisions,
        // aggregating every managed resource category. A secret key alone is not
        // sufficient: require read/write on all management scopes, matching the
        // ManagementMutationHandler mapping for "package-sync".
        foreach (var scope in PackageSyncRequiredScopes)
        {
            if (!ApiKeyPermissionPolicy.HasPermission(auth, scope, "rw"))
                return PackageSyncError(context, 403, "FORBIDDEN", "This key does not have permission for this operation.");
        }

        var bodyText = await new StreamReader(context.Request.Body).ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(bodyText))
            return PackageSyncError(context, 400, "INVALID_JSON", "Request body must be valid JSON.");

        JsonElement body;
        try
        {
            body = JsonSerializer.Deserialize<JsonElement>(bodyText);
        }
        catch (JsonException)
        {
            return PackageSyncError(context, 400, "INVALID_JSON", "Request body must be valid JSON.");
        }

        if (body.ValueKind != JsonValueKind.Object)
            return PackageSyncError(context, 400, "INVALID_BODY", "Request body must be a JSON object.");

        var ownerUserId = auth.UserId;
        var previous = await ReadGamePackageAsync(ownerUserId, projectId, cancellationToken);
        var previousRevisionId = previous?.TryGetValue("currentRevisionId", out var prevRev) == true
            && prevRev.ValueKind == JsonValueKind.Number ? prevRev.GetInt64() : (long?)null;

        var gamePackage = MergeGamePackage(previous, body);
        await WriteGamePackageAsync(ownerUserId, projectId, gamePackage, cancellationToken);

        var currentRevisionId = gamePackage.TryGetValue("currentRevisionId", out var curRev) && curRev.ValueKind == JsonValueKind.Number
            ? curRev.GetInt64() : (long?)null;
        var revisionChanged = previousRevisionId.HasValue && currentRevisionId.HasValue
            && previousRevisionId.Value != currentRevisionId.Value;

        var promoted = new PackageSyncPromotionResult(0, 0, false);

        if (revisionChanged)
        {
            promoted = await PromoteRevisionOverridesAsync(ownerUserId, projectId, cancellationToken);
        }
        metadataCache.Invalidate(projectId);
        var responseGamePackage = WithUnixTimestamps(gamePackage);
        context.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Json(new
        {
            ok = true,
            gamePackage = responseGamePackage,
            previousRevisionId,
            revisionChanged,
            promotedRevisionOverrides = promoted,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task<Dictionary<string, JsonElement>?> ReadGamePackageAsync(long userId, string projectId, CancellationToken ct)
    {
        try
        {
            var result = await workspaceClient.GetProjectResourceAsync<Dictionary<string, JsonElement>>(userId, projectId, "game-package.json", ct);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read game-package.json for project {ProjectId}; treating as new", projectId);
            return null;
        }
    }

    private async Task WriteGamePackageAsync(long userId, string projectId, Dictionary<string, JsonElement> package, CancellationToken ct)
    {
        // Strip read-only synthetic keys before writing so the stored JSON shape matches legacy server.
        var toWrite = new Dictionary<string, JsonElement>(package, StringComparer.Ordinal);
        toWrite.Remove("revisionFirstSyncedAtUnix");
        toWrite.Remove("lastSyncedAtUnix");
        await workspaceClient.PutProjectResourceAsync(userId, projectId, "game-package.json", toWrite, ct);
    }

    private static Dictionary<string, JsonElement> MergeGamePackage(Dictionary<string, JsonElement>? existing, JsonElement body)
    {
        var merged = existing is not null
            ? new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var prop in body.EnumerateObject())
        {
            merged[prop.Name] = prop.Value.Clone();
        }

        merged["lastSyncedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        if (!merged.ContainsKey("revisionPublishedAt"))
        {
            merged["revisionPublishedAt"] = merged["lastSyncedAt"];
        }

        merged["publishStatus"] = JsonSerializer.SerializeToElement("published");

        return merged;
    }

    private async Task<PackageSyncPromotionResult> PromoteRevisionOverridesAsync(long userId, string projectId, CancellationToken ct)
    {
        try
        {
            var overrides = await workspaceClient.GetProjectResourceAsync<Dictionary<string, JsonElement>>(
                userId, projectId, RevisionOverrides.ResourcePath, ct);
            var endpointOverrides = overrides?.TryGetValue("endpoints", out var ep) == true && ep.ValueKind == JsonValueKind.Object ? (JsonElement?)ep : null;
            var collectionOverrides = overrides?.TryGetValue("collections", out var col) == true && col.ValueKind == JsonValueKind.Object ? (JsonElement?)col : null;

            if ((endpointOverrides is null || endpointOverrides.Value.EnumerateObject().Count() == 0) &&
                (collectionOverrides is null || collectionOverrides.Value.EnumerateObject().Count() == 0))
            {
                return new PackageSyncPromotionResult(0, 0, false);
            }

            var endpointCount = 0;
            var collectionCount = 0;

            if (endpointOverrides is not null)
            {
                var liveEndpoints = await workspaceClient.GetProjectResourceAsync<List<Dictionary<string, JsonElement>>>(
                    userId, projectId, "endpoints.json", ct) ?? new List<Dictionary<string, JsonElement>>();
                foreach (var staged in endpointOverrides.Value.EnumerateObject())
                {
                    var stagedObject = staged.Value.ValueKind == JsonValueKind.Object ? staged.Value : default;
                    if (stagedObject.ValueKind != JsonValueKind.Object || !stagedObject.TryGetProperty("slug", out var slugProp) || slugProp.ValueKind != JsonValueKind.String)
                        continue;
                    var slug = slugProp.GetString()!;
                    var existing = liveEndpoints.FirstOrDefault(e => e.TryGetValue("slug", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == slug);
                    if (existing is not null)
                    {
                        foreach (var prop in stagedObject.EnumerateObject())
                        {
                            if (prop.NameEquals("id")) continue;
                            existing[prop.Name] = prop.Value.Clone();
                        }
                        existing["updatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        var clone = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stagedObject.GetRawText())!;
                        if (!clone.ContainsKey("id")) clone["id"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N")[..16]);
                        if (!clone.ContainsKey("createdAt")) clone["createdAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        clone["updatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        liveEndpoints.Add(clone);
                    }
                    endpointCount++;
                }
                await workspaceClient.PutProjectResourceAsync(userId, projectId, "endpoints.json", liveEndpoints, ct);
            }

            if (collectionOverrides is not null)
            {
                var liveCollections = await workspaceClient.GetProjectResourceAsync<List<Dictionary<string, JsonElement>>>(
                    userId, projectId, "collections.json", ct) ?? new List<Dictionary<string, JsonElement>>();
                foreach (var staged in collectionOverrides.Value.EnumerateObject())
                {
                    var stagedObject = staged.Value.ValueKind == JsonValueKind.Object ? staged.Value : default;
                    if (stagedObject.ValueKind != JsonValueKind.Object || !stagedObject.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                        continue;
                    var name = nameProp.GetString()!;
                    var existing = liveCollections.FirstOrDefault(c => c.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name);
                    if (existing is not null)
                    {
                        foreach (var prop in stagedObject.EnumerateObject())
                        {
                            if (prop.NameEquals("id")) continue;
                            existing[prop.Name] = prop.Value.Clone();
                        }
                        existing["updatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        var clone = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stagedObject.GetRawText())!;
                        if (!clone.ContainsKey("id")) clone["id"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N")[..16]);
                        if (!clone.ContainsKey("createdAt")) clone["createdAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        clone["updatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        liveCollections.Add(clone);
                    }
                    collectionCount++;
                }
                await workspaceClient.PutProjectResourceAsync(userId, projectId, "collections.json", liveCollections, ct);
            }

            // Clear overrides after promotion.
            await workspaceClient.PutProjectResourceAsync(userId, projectId, RevisionOverrides.ResourcePath,
                new Dictionary<string, object> { ["endpoints"] = new Dictionary<string, object>(), ["collections"] = new Dictionary<string, object>() }, ct);

            return new PackageSyncPromotionResult(endpointCount, collectionCount, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to promote revision overrides for project {ProjectId}", projectId);
            return new PackageSyncPromotionResult(0, 0, false);
        }
    }

    private sealed record PackageSyncPromotionResult(int EndpointCount, int CollectionCount, bool Promoted);

    private static Dictionary<string, object?> WithUnixTimestamps(Dictionary<string, JsonElement> gamePackage)
    {
        var clone = new Dictionary<string, object?>(gamePackage.Count, StringComparer.Ordinal);
        foreach (var kv in gamePackage)
        {
            clone[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        }

        clone["revisionFirstSyncedAtUnix"] = ToUnixTimestamp(gamePackage.TryGetValue("revisionPublishedAt", out var r) ? r : default);
        clone["lastSyncedAtUnix"] = ToUnixTimestamp(gamePackage.TryGetValue("lastSyncedAt", out var l) ? l : default);
        return clone;
    }

    private static long? ToUnixTimestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        if (!DateTimeOffset.TryParse(element.GetString(), out var dto)) return null;
        return dto.ToUnixTimeSeconds();
    }

    private static IResult PackageSyncError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return Results.Json(new { ok = false, error = code, message }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
