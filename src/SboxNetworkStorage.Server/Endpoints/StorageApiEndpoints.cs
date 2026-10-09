using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.Observability;
using SboxNetworkStorage.Server.Middleware;
using System.Text.RegularExpressions;
using System.Linq;
using SboxNetworkStorage.Server.Routing;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Server.Endpoints;

public static class StorageApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IEndpointRouteBuilder MapStorageApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/storage/{projectId}/{collectionId}/{key}", GetRecordAsync)
            .WithDisplayName("Storage API GET record")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core Network Storage record read");

        endpoints.MapPost("/api/storage/{projectId}/{collectionId}/{key}", PostRecordAsync)
            .WithDisplayName("Storage API POST record")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core Network Storage record write");

        endpoints.MapDelete("/api/storage/{projectId}/{collectionId}/{key}", DeleteRecordAsync)
            .WithDisplayName("Storage API DELETE record")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core Network Storage record delete");

        // Client save-failure report. Registered under /api/network-storage/* (NOT
        // /api/storage/*) so the live nginx routes it to the .NET website today —
        // /api/storage/* is still proxied to the legacy Bun data plane until the
        // ScyllaDB cutover. /api/* always reaches .NET post-cutover too, so this URL
        // is stable across the migration.
        endpoints.MapPost("/api/network-storage/{projectId}/save-failure", PostSaveFailureAsync)
            .WithDisplayName("Network Storage save-failure report")
            .WithRouteOwner(RouteOwner.DotNetNative, "ASP.NET Core Network Storage client save-failure report");

        return endpoints;
    }

    internal static async Task GetRecordAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";
        var recordKey = (string?)context.GetRouteValue("key") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        // Direct collection-data API requires secret keys to hold collections execute.
        if (!ApiKeyPermissionPolicy.CanAccessCollectionData(auth))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "FORBIDDEN", detail = "This secret key does not have execute access to collections." }, JsonOptions);
            return;
        }

        // Malformed client identifiers (e.g. an encoded "..%2F..%2Fetc" collection
        // or "a%2Fb" key) can never address a stored record. Reject them here as
        // 404 — matching the Bun observable outcome — instead of letting the
        // store throw ArgumentException, which would misclassify client input as
        // a backend STORAGE_ERROR 500.
        var invalidIds = ValidateRecordIds(collectionId, recordKey);
        if (invalidIds is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(invalidIds, JsonOptions);
            return;
        }

        // Record analytics event
        _ = analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: projectId,
            CollectionId: collectionId,
            RecordKey: recordKey,
            EventType: context.Request.Method.ToLower() switch { "post" => "record.write", "delete" => "record.delete", _ => "record.read" },
            Payload: new { apiKey = MaskApiKey(apiKey) }
        ), CancellationToken.None);

        var dataPlane = context.RequestServices.GetRequiredService<INetworkStorageDataPlane>();
        RecordReadResult read;
        try
        {
            read = await dataPlane.ReadRecordAsync(auth.UserId, projectId, collectionId, recordKey, context.RequestAborted);
        }
        catch (Exception ex)
        {
            await ReportDataPlaneErrorAsync(context, projectId, collectionId, recordKey, "record.read", ex);
            return;
        }

        if (!read.Found)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "NOT_FOUND", detail = "Key not found." }, JsonOptions);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(read.Value, JsonOptions);
    }

    internal static async Task PostRecordAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";
        var recordKey = (string?)context.GetRouteValue("key") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        // Direct collection-data API requires secret keys to hold collections execute.
        if (!ApiKeyPermissionPolicy.CanAccessCollectionData(auth))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "FORBIDDEN", detail = "This secret key does not have execute access to collections." }, JsonOptions);
            return;
        }

        // Malformed client identifiers (e.g. an encoded "..%2F..%2Fetc" collection
        // or "a%2Fb" key) can never address a stored record. Reject them here as
        // 404 — matching the Bun observable outcome — instead of letting the
        // store throw ArgumentException, which would misclassify client input as
        // a backend STORAGE_ERROR 500.
        var invalidIds = ValidateRecordIds(collectionId, recordKey);
        if (invalidIds is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(invalidIds, JsonOptions);
            return;
        }

        // Record analytics event
        _ = analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: projectId,
            CollectionId: collectionId,
            RecordKey: recordKey,
            EventType: context.Request.Method.ToLower() switch { "post" => "record.write", "delete" => "record.delete", _ => "record.read" },
            Payload: new { apiKey = MaskApiKey(apiKey) }
        ), CancellationToken.None);

        var maxPayloadBytes = context.RequestServices.GetRequiredService<INetworkStorageStore>().MaxPayloadBytes;
        if (context.Request.ContentLength > maxPayloadBytes)
        {
            await WritePayloadTooLargeAsync(context, maxPayloadBytes);
            return;
        }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY" }, JsonOptions);
            return;
        }

        if (body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY" }, JsonOptions);
            return;
        }
        if (Encoding.UTF8.GetByteCount(body.GetRawText()) > maxPayloadBytes)
        {
            await WritePayloadTooLargeAsync(context, maxPayloadBytes);
            return;
        }

        // Usage metering: authenticated record write. Annotated (rather than
        // relying on the middleware's method fallback) so the stored-bytes
        // delta below lands on the request.
        NetworkStorageUsageContext.Set(context, projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.Usage.UsageKind.Write);

        var dataPlane = context.RequestServices.GetRequiredService<INetworkStorageDataPlane>();
        string? invalidOperation = null;
        var appliedTooLarge = false;
        try
        {
            var previous = await dataPlane.ReadRecordAsync(
                auth.UserId, projectId, collectionId, recordKey, context.RequestAborted);
            var previousBytes = previous.Found ? Encoding.UTF8.GetByteCount(previous.Value.GetRawText()) : 0;
            var document = body;
            if (IsOperationsRequest(body, out var ops))
            {
                var existing = previous.Found ? EndpointExpression.FromJson(previous.Value) as Dictionary<string, object?> : null;
                var applied = RecordOperations.Apply(existing, (List<object?>)EndpointExpression.FromJson(ops)!);
                if (applied.Ok) document = JsonSerializer.SerializeToElement(applied.Data);
                else invalidOperation = applied.Error;
            }
            var currentBytes = Encoding.UTF8.GetByteCount(document.GetRawText());
            appliedTooLarge = currentBytes > maxPayloadBytes;
            if (invalidOperation is null && !appliedTooLarge)
            {
                await dataPlane.WriteRecordAsync(auth.UserId, projectId, collectionId, recordKey, document, context.RequestAborted);
                NetworkStorageUsageContext.AddStorageDelta(context, currentBytes - previousBytes);
            }
        }
        catch (Exception ex)
        {
            await ReportDataPlaneErrorAsync(context, projectId, collectionId, recordKey, "record.write", ex);
            return;
        }

        if (invalidOperation is not null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_OPERATION", detail = invalidOperation }, JsonOptions);
            return;
        }
        if (appliedTooLarge)
        {
            await WritePayloadTooLargeAsync(context, maxPayloadBytes);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { ok = true }, JsonOptions);
    }

    /// <summary>
    /// The game library's UpdateDocument/PatchDocument posts <c>{"ops":[...]}</c>
    /// to the save route; the managed service applies them to the stored
    /// document. Storing the body verbatim would replace the player's document.
    /// </summary>
    private static bool IsOperationsRequest(JsonElement body, out JsonElement ops)
    {
        ops = default;
        return body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("ops", out ops) && ops.ValueKind == JsonValueKind.Array;
    }

    private static async Task WritePayloadTooLargeAsync(HttpContext context, int maxPayloadBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(
            new { error = "PAYLOAD_TOO_LARGE", detail = $"Payload exceeds the {maxPayloadBytes}-byte limit." },
            JsonOptions, context.RequestAborted);
    }

    internal static async Task DeleteRecordAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";
        var recordKey = (string?)context.GetRouteValue("key") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        // Direct collection-data API requires secret keys to hold collections execute.
        if (!ApiKeyPermissionPolicy.CanAccessCollectionData(auth))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "FORBIDDEN", detail = "This secret key does not have execute access to collections." }, JsonOptions);
            return;
        }

        // Malformed client identifiers (e.g. an encoded "..%2F..%2Fetc" collection
        // or "a%2Fb" key) can never address a stored record. Reject them here as
        // 404 — matching the Bun observable outcome — instead of letting the
        // store throw ArgumentException, which would misclassify client input as
        // a backend STORAGE_ERROR 500.
        var invalidIds = ValidateRecordIds(collectionId, recordKey);
        if (invalidIds is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(invalidIds, JsonOptions);
            return;
        }

        // Record analytics event
        _ = analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: projectId,
            CollectionId: collectionId,
            RecordKey: recordKey,
            EventType: context.Request.Method.ToLower() switch { "post" => "record.write", "delete" => "record.delete", _ => "record.read" },
            Payload: new { apiKey = MaskApiKey(apiKey) }
        ), CancellationToken.None);

        var dataPlane = context.RequestServices.GetRequiredService<INetworkStorageDataPlane>();
        NetworkStorageUsageContext.Set(context, projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.Usage.UsageKind.Write);
        try
        {
            var previous = await dataPlane.ReadRecordAsync(
                auth.UserId, projectId, collectionId, recordKey, context.RequestAborted);
            await dataPlane.DeleteRecordAsync(auth.UserId, projectId, collectionId, recordKey, context.RequestAborted);
            if (previous.Found)
                NetworkStorageUsageContext.AddStorageDelta(
                    context, -Encoding.UTF8.GetByteCount(previous.Value.GetRawText()));
        }
        catch (Exception ex)
        {
            await ReportDataPlaneErrorAsync(context, projectId, collectionId, recordKey, "record.delete", ex);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { ok = true }, JsonOptions);
    }

    // POST /v3/storage/{projectId}/{collectionId}/append
    // Append a record to a global collection. Native ScyllaDB-backed replacement
    // for the Bun `routeV3GlobalAppend` handler in `controllers/storage-v3-controller.js`.
    internal static async Task AppendRecordAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var dataPlaneStore = context.RequestServices.GetRequiredService<INetworkStorageStore>();
        var rateLimiter = context.RequestServices.GetRequiredService<IAppendRateLimiter>();
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();

        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var collectionId = (string?)context.GetRouteValue("collectionId") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        // Direct global append requires a secret key with collections execute permission.
        if (!string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase)
            || !ApiKeyPermissionPolicy.HasPermission(auth, "collections", "x"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "FORBIDDEN", detail = "This secret key does not have execute access to collections." }, JsonOptions);
            return;
        }

        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!access.Project.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "PROJECT_DISABLED" }, JsonOptions);
            return;
        }

        if (access.RequireSboxAuth)
        {
            // The legacy Bun runtime verifies s&box auth tokens for non-secret
            // requests. A .NET sbox-auth shim is not yet wired, but secret keys
            // bypass the check and are the only keys allowed for append anyway.
            // Keep a response header so the gap is observable in client telemetry.
            context.Response.Headers.Append("X-Sboxcool-SboxAuth", "bypassed-secret-key");
        }

        var resources = await projectService.GetProjectResourcesForOwnerAsync(access.StorageOwnerUserId, projectId, context.RequestAborted);
        if (resources is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "NOT_FOUND", detail = "Collection not found." }, JsonOptions);
            return;
        }

        var collection = FindCollection(resources.Collections, collectionId);
        if (collection is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "NOT_FOUND", detail = "Collection not found." }, JsonOptions);
            return;
        }

        if (!string.Equals(collection.CollectionType, "global", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "COLLECTION_TYPE_MISMATCH", detail = "This is a per-steamid collection. Use an endpoint instead." }, JsonOptions);
            return;
        }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY" }, JsonOptions);
            return;
        }

        if (body.ValueKind is not JsonValueKind.Object || body.EnumerateObject().Count() == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY", detail = "Body must be a non-empty JSON object." }, JsonOptions);
            return;
        }

        if (body.GetRawText().Length > 1_048_576)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "PAYLOAD_TOO_LARGE" }, JsonOptions);
            return;
        }

        // [KNOWN GAP] JSON-schema validation against collection.Schema is not yet
        // implemented in the .NET runtime. The record is still type-validated
        // above and all other access/integrity rules are enforced.

        // Writer identity. Use the on-behalf-of header when present, then the
        // steam-id header/query, defaulting to "anonymous".
        var writerId = context.Request.Headers["x-on-behalf-of"].FirstOrDefault()
            ?? context.Request.Headers["x-steam-id"].FirstOrDefault()
            ?? context.Request.Query["steamId"].FirstOrDefault()
            ?? "anonymous";

        var rateResult = await rateLimiter.CheckAsync(
            projectId, collectionId, "player", 8640, writerId, context.RequestAborted);
        if (!rateResult.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { error = rateResult.Code, detail = rateResult.Message }, JsonOptions);
            return;
        }

        // Build new record and write via ScyllaDB (global_records table).
        var recordId = GenerateNetworkStorageId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Clone body without reserved internal fields.
        var recordDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["_id"] = JsonSerializer.SerializeToElement(recordId),
            ["_timestamp"] = JsonSerializer.SerializeToElement(now),
            ["_writerId"] = JsonSerializer.SerializeToElement(writerId)
        };
        foreach (var property in body.EnumerateObject())
        {
            if (property.NameEquals("_id") || property.NameEquals("_timestamp") || property.NameEquals("_writerId") || property.NameEquals("_txId"))
                continue;
            recordDict[property.Name] = property.Value;
        }
        var record = JsonSerializer.SerializeToElement(recordDict);

        // Usage metering: authenticated global append — annotated so the
        // stored-bytes delta lands on the request (design D4).
        NetworkStorageUsageContext.Set(context, projectId, SboxNetworkStorage.Infrastructure.NetworkStorage.Usage.UsageKind.Write);

        await dataPlaneStore.UpsertGlobalRecordAsync(
            projectId, collectionId, recordId, record, version: 1, context.RequestAborted);

        NetworkStorageUsageContext.AddStorageDelta(context, Encoding.UTF8.GetByteCount(record.GetRawText()));

        _ = analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: projectId,
            CollectionId: collectionId,
            RecordKey: recordId,
            EventType: "record.write",
            Payload: new { apiKey = MaskApiKey(apiKey) }), CancellationToken.None);

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { ok = true, record }, JsonOptions);
    }

    /// <summary>
    /// POST /v3/storage/{projectId}/analytics/events and the /api/storage alias.
    /// Records a player analytics event from the game client. Mirrors the Bun
    /// <c>routeStorageApiAnalyticsEvent</c> handler in
    /// <c>controllers/storage-modules/insights-routes.js</c>.
    /// </summary>
    internal static async Task PostAnalyticsEventAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var projectService = context.RequestServices.GetRequiredService<INetworkStorageProjectService>();
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
        if (access is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!access.Project.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "PROJECT_DISABLED" }, JsonOptions);
            return;
        }

        if (access.RequireSboxAuth)
        {
            // [KNOWN GAP] s&box auth token verification is not yet ported to .NET.
            // The native candidates follow the same transitional behavior: the
            // request is allowed through so live clients are not blocked.
            context.Response.Headers.Append("X-Sboxcool-SboxAuth", "not-verified");
        }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_JSON" }, JsonOptions);
            return;
        }

        var raw = body.GetRawText();
        if (raw.Length > 32_768)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "PAYLOAD_TOO_LARGE", detail = "Analytics event payload is too large." }, JsonOptions);
            return;
        }

        if (body.ValueKind is not JsonValueKind.Object)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_JSON", detail = "Analytics event body must be a JSON object." }, JsonOptions);
            return;
        }

        var steamId = GetStringProperty(body, "steamId")
            ?? context.Request.Query["steamId"].FirstOrDefault()
            ?? "";
        if (string.IsNullOrWhiteSpace(steamId) || !Regex.IsMatch(steamId, "^[0-9]{4,32}$"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_KEY", detail = "steamId is required and must be numeric." }, JsonOptions);
            return;
        }

        var severity = GetStringProperty(body, "severity")?.ToLowerInvariant() ?? "";
        if (!string.IsNullOrEmpty(severity) && severity is not ("warning" or "error" or "info" or "custom" or "session"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_ANALYTICS_EVENT", detail = "severity must be warning, error, info, custom, or session." }, JsonOptions);
            return;
        }

        var rawType = GetStringProperty(body, "type", "event", "code") ?? "";
        var normalizedCustomType = string.IsNullOrEmpty(rawType) ? "" : NormalizeAnalyticsEventType(rawType);
        var type = rawType.Contains('.') ? rawType.ToLowerInvariant()[..Math.Min(rawType.Length, 80)] : $"custom.{normalizedCustomType}";
        type = severity == "warning" ? "warning.reported"
            : severity == "error" ? "error.reported"
            : severity == "session" ? CategorizeSessionType(normalizedCustomType)
            : type;
        if (string.IsNullOrEmpty(type) || type == "custom.")
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_ANALYTICS_EVENT", detail = "A valid analytics event type or code is required." }, JsonOptions);
            return;
        }

        var label = GetStringProperty(body, "label", "code", "message")
            ?? normalizedCustomType
            ?? rawType;
        var source = GetStringProperty(body, "source") == "network-storage-library" ? "network-storage-library" : "manual";

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["steamId"] = steamId,
            ["playerName"] = GetStringProperty(body, "playerName", "player_name"),
            ["sessionId"] = GetStringProperty(body, "sessionId", "session_id"),
            ["type"] = type,
            ["label"] = label,
            ["source"] = source,
            ["payload"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["severity"] = string.IsNullOrEmpty(severity) ? null : severity,
                ["code"] = GetStringProperty(body, "code") ?? null,
                ["message"] = GetStringProperty(body, "message") ?? null,
                ["context"] = GetStringProperty(body, "context", "data"),
                ["requestPayload"] = GetStringProperty(body, "requestPayload", "request_payload", "request"),
                ["responsePayload"] = GetStringProperty(body, "responsePayload", "response_payload", "response"),
                ["stack"] = GetStringProperty(body, "stack"),
                ["sessionSeconds"] = GetDoubleOrNull(body, "sessionSeconds", "session_seconds"),
                ["durationSeconds"] = GetDoubleOrNull(body, "durationSeconds", "duration_seconds"),
                ["libraryVersion"] = GetStringProperty(body, "libraryVersion", "library_version"),
                ["packageIdent"] = GetStringProperty(body, "packageIdent", "package_ident"),
                ["fps"] = body.TryGetProperty("fps", out var fps) ? (object?)fps : (body.TryGetProperty("performance", out var perf) ? (object?)perf : null)
            }
        };

        context.Response.StatusCode = StatusCodes.Status200OK;

        try
        {
            await analytics.RecordEventAsync(new PlayerEventRequest(
                ProjectId: projectId,
                CollectionId: "analytics",
                RecordKey: steamId,
                EventType: type,
                Payload: payload), CancellationToken.None);

            await context.Response.WriteAsJsonAsync(new { ok = true, stored = true, reason = (string?)null }, JsonOptions);
        }
        catch (Exception)
        {
            // Best-effort storage, matching the Bun path which returns ok even on failure.
            await context.Response.WriteAsJsonAsync(new { ok = true, stored = false, reason = "storage_unavailable" }, JsonOptions);
        }
    }

    private static string GenerateNetworkStorageId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static CollectionResource? FindCollection(IReadOnlyList<CollectionResource> collections, string collectionId)
    {
        foreach (var c in collections)
        {
            if (string.Equals(c.Id, collectionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }

    private static string? GetStringProperty(JsonElement body, params string[] names)
    {
        foreach (var name in names)
        {
            if (body.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }


    private static double? GetDoubleOrNull(JsonElement body, params string[] names)
    {
        foreach (var name in names)
        {
            if (body.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var v))
                return v;
        }
        return null;
    }

    private static string NormalizeAnalyticsEventType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim().ToLowerInvariant();
        var normalized = System.Text.RegularExpressions.Regex.Replace(trimmed, @"[^a-z0-9_\-\.]", "-");
        return normalized[..Math.Min(normalized.Length, 80)];
    }

    private static string CategorizeSessionType(string normalized)
    {
        var name = Regex.Replace(normalized, @"^session[.:_-]", "");
        if (name is "join" or "start" or "session_start") return "session.join";
        if (name is "leave" or "disconnect" or "end" or "session_end") return "session.leave";
        return "session.heartbeat";
    }


    /// <summary>
    /// Client-reported save verification failure. A game posts here when a save
    /// returned HTTP 200 but its read-back could not confirm the write persisted
    /// (a silent drop — the class of bug that loses player progress). The server
    /// never sees these on the legacy proxied write path, so the client is the
    /// only place that can detect them. Fires a Discord alert (deduped/rate-limited
    /// in the sink) and records a diagnostic analytics event so the cause can be
    /// correlated per player and save.
    /// </summary>
    internal static async Task PostSaveFailureAsync(HttpContext context)
    {
        var resolver = context.RequestServices.GetRequiredService<IStorageApiKeyResolver>();
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";

        var apiKey = ExtractApiKey(context.Request);
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED", detail = "Missing apiKey" }, JsonOptions);
            return;
        }

        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted);
        if (auth is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "UNAUTHORIZED" }, JsonOptions);
            return;
        }

        if (!auth.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "KEY_DISABLED" }, JsonOptions);
            return;
        }

        JsonElement body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<JsonElement>(JsonOptions, context.RequestAborted);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY" }, JsonOptions);
            return;
        }

        if (body.ValueKind is not JsonValueKind.Object)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "INVALID_BODY" }, JsonOptions);
            return;
        }

        var report = SaveFailureReport.From(body);

        // Immediate human alert. The sink dedupes + rate-limits, so a flaky client
        // cannot flood the channel.
        var alertSink = context.RequestServices.GetService<INetworkStorageErrorAlertSink>();
        var message =
            $"Save not confirmed by client — project={projectId} collection={report.CollectionId} key={report.RecordKey} " +
            $"reason={report.Reason} expectedSeq={report.ExpectedSeq} observedSeq={report.ObservedSeq} attempts={report.Attempts}";
        if (alertSink is not null)
        {
            var log = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("StorageApiEndpoints") ?? NullLogger.Instance;
            AlertFireAndForget.Run(
                alertSink.NotifyAsync(new NetworkStorageError(
                    projectId, report.CollectionId, report.RecordKey,
                    "save.unconfirmed", "SAVE_NOT_CONFIRMED", message), CancellationToken.None),
                log,
                "save-unconfirmed alert");
        }

        // Diagnostic telemetry for cause correlation. Network Storage analytics
        // live in ScyllaDB (air-gapped from the website Postgres), same as every
        // other record event.
        var analytics = context.RequestServices.GetRequiredService<IPlayerAnalyticsService>();
        _ = analytics.RecordEventAsync(new PlayerEventRequest(
            ProjectId: projectId,
            CollectionId: report.CollectionId,
            RecordKey: report.RecordKey,
            EventType: "record.save_unconfirmed",
            Payload: new
            {
                reason = report.Reason,
                expectedSeq = report.ExpectedSeq,
                observedSeq = report.ObservedSeq,
                attempts = report.Attempts,
                apiKey = MaskApiKey(apiKey)
            }), context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new { ok = true, recorded = true }, JsonOptions);
    }

    /// <summary>Parsed client save-failure report. Missing fields degrade to ""/-1 (the alert still fires).</summary>
    private readonly record struct SaveFailureReport(
        string CollectionId, string RecordKey, string Reason, long ExpectedSeq, long ObservedSeq, int Attempts)
    {
        public static SaveFailureReport From(JsonElement body)
        {
            string Str(string name) =>
                body.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            long Num(string name) =>
                body.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : -1;
            return new SaveFailureReport(Str("collectionId"), Str("recordKey"), Str("reason"), Num("expectedSeq"), Num("observedSeq"), (int)Num("attempts"));
        }
    }

    /// <summary>
    /// Validates direct-record route identifiers against the shared provider-neutral
    /// patterns (<see cref="StorageIdValidation"/> — the same shapes the stores
    /// enforce). Returns the 404 body to serve when either identifier is malformed,
    /// or null when both are valid. Collection and key misses reuse the wording of
    /// the data-plane miss paths so callers cannot distinguish "malformed" from
    /// "absent" — both are 404, never a backend failure.
    /// </summary>
    private static object? ValidateRecordIds(string collectionId, string recordKey)
    {
        if (!StorageIdValidation.IsValidCollectionId(collectionId))
            return new { error = "NOT_FOUND", detail = "Collection not found." };
        if (!StorageIdValidation.IsValidRecordKey(recordKey))
            return new { error = "NOT_FOUND", detail = "Key not found." };
        return null;
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 12) return "****";
        return $"{apiKey[..8]}...{apiKey[^4..]}";
    }

    internal static string? ExtractApiKey(HttpRequest request)

    {
        var header = request.Headers["x-api-key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(header)) return header;

        if (request.Query.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey.ToString();
        }
        return string.Empty;
    }

    private static async Task ReportDataPlaneErrorAsync(
        HttpContext context, string projectId, string collectionId, string recordKey,
        string operation, Exception ex)
    {
        var alertSink = context.RequestServices.GetService<INetworkStorageErrorAlertSink>();
        if (alertSink is not null)
        {
            var log = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("StorageApiEndpoints") ?? NullLogger.Instance;
            AlertFireAndForget.Run(
                alertSink.NotifyAsync(new NetworkStorageError(
                    projectId, collectionId, recordKey, operation,
                    ex.GetType().Name, ex.Message), CancellationToken.None),
                log,
                "data-plane error alert");
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "STORAGE_ERROR",
            detail = "A storage operation failed.",
            correlationId = CorrelationContext.Get(context)
        }, JsonOptions);
    }
}
