using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Media routes: upload grants (JSON), raw-body upload (no API key; the grant is the credential),
/// and object reads. Served at <c>/v3/media/...</c> and, through the API root aliases, at
/// <c>/api/media/...</c>. Default off per project: while media is disabled, or the project is
/// unknown, every media route answers the standard not-found response.
/// </summary>
public static class MediaEndpoints
{
    private const string Wiki = "https://sboxcool.com/wiki/network-storage-v3";
    private const string GrantEndpointSlug = "__media_grant";

    /// <summary>
    /// Registers the media routes. <paramref name="maxObjectBytes"/> is the server-wide hard cap, applied
    /// to the upload route as its request body limit; per-purpose limits are checked inside the handler.
    /// </summary>
    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder endpoints, long maxObjectBytes)
    {
        endpoints.MapPost("/v3/media/{projectId}/grants", CreateGrantAsync)
            .WithDisplayName("Network Storage media grant");
        endpoints.MapPut("/v3/media/{projectId}/upload/{grantToken}", UploadAsync)
            .WithDisplayName("Network Storage media upload")
            .WithMetadata(new RequestSizeLimitAttribute(maxObjectBytes));
        endpoints.MapMethods("/v3/media/{projectId}/objects/{mediaId}", [HttpMethods.Get, HttpMethods.Head], ReadObjectAsync)
            .WithDisplayName("Network Storage media object");
        return endpoints;
    }

    private static async Task CreateGrantAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var services = context.RequestServices;
        var ct = context.RequestAborted;

        var projects = services.GetRequiredService<INetworkStorageProjectService>();
        var project = await projects.FindProjectAsync(projectId, ct);
        var policy = PolicyFor(project, services);
        if (project is null || !policy.Enabled)
        {
            await ServeNotFoundAsync(context);
            return;
        }
        if (!project.Enabled)
        {
            await FailAsync(context, requestId, StatusCodes.Status403Forbidden, "PROJECT_DISABLED", "Project is disabled.");
            return;
        }

        var body = await ReadJsonObjectAsync(context);
        if (body is null)
        {
            await FailAsync(context, requestId, StatusCodes.Status400BadRequest, "MEDIA_INVALID_REQUEST", "Request body must be a JSON object.");
            return;
        }
        var request = new MediaGrantRequest(
            Purpose: ReadString(body.Value, "purpose"),
            ContentType: ReadString(body.Value, "contentType"),
            SizeBytes: ReadInt64(body.Value, "sizeBytes"),
            Sha256: ReadString(body.Value, "sha256"),
            ForSteamId: ReadString(body.Value, "forSteamId"));

        var apiKey = ReadApiKey(context);
        if (string.IsNullOrEmpty(apiKey))
        {
            await FailAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "No API key provided.");
            return;
        }
        var resolver = services.GetRequiredService<IStorageApiKeyResolver>();
        var auth = await resolver.ResolveApiKeyAsync(apiKey, projectId, ct);
        if (auth is null || !auth.Enabled)
        {
            await FailAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid or missing API key.");
            return;
        }
        var access = await projects.ResolveProjectAccessAsync(auth.UserId, projectId, ct);
        if (access is null)
        {
            await FailAsync(context, requestId, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid or missing API key.");
            return;
        }

        var media = services.GetRequiredService<NetworkStorageMediaService>();
        var callerIsSecret = string.Equals(auth.KeyType, "secret", StringComparison.Ordinal);
        // Policy gates run before any identity work: a disabled purpose or a closed self-grant
        // is refused without touching the Steam or s&box verifiers.
        var authorized = media.AuthorizeGrant(policy, request, callerIsSecret);
        if (!authorized.Ok)
        {
            await FailAsync(context, requestId, authorized.Failure!);
            return;
        }

        string ownerSteamId;
        if (callerIsSecret)
        {
            if (string.IsNullOrEmpty(request.ForSteamId))
            {
                await FailAsync(context, requestId, StatusCodes.Status400BadRequest, "MEDIA_INVALID_REQUEST",
                    "forSteamId is required for grants issued with the secret key.");
                return;
            }
            ownerSteamId = request.ForSteamId;
        }
        else
        {
            var claimed = PlayerIdentity.ClaimedSteamId(context.Request, body.Value);
            var identity = await PlayerIdentity.ResolveAsync(context, body.Value, projectId, GrantEndpointSlug, apiKey,
                auth.UserId, required: true, sessionsEnabled: access.Project.EnableAuthSessions == true, claimedSteamId: claimed);
            if (!identity.Ok)
            {
                await FailAsync(context, requestId, identity.Status, identity.Code!, identity.Message ?? "Player identity could not be verified.");
                return;
            }
            ownerSteamId = identity.SteamId!;
        }

        var issued = await media.IssueGrantAsync(projectId, authorized.Value!, request, ownerSteamId, NowUnixMs(), ct);
        if (!issued.Ok)
        {
            await FailAsync(context, requestId, issued.Failure!);
            return;
        }
        var grant = issued.Value!;
        await WriteJsonAsync(context, requestId, StatusCodes.Status201Created, new
        {
            ok = true,
            grantToken = grant.GrantToken,
            uploadUrl = $"/api/media/{projectId}/upload/{grant.GrantToken}",
            method = "PUT",
            mediaId = grant.MediaId,
            contentType = grant.ContentType,
            maxBytes = grant.MaxBytes,
            expiresAtUnixMs = grant.ExpiresAtUnixMs,
            _requestId = requestId,
        });
    }

    private static async Task UploadAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var grantToken = (string?)context.GetRouteValue("grantToken") ?? string.Empty;
        var services = context.RequestServices;
        var ct = context.RequestAborted;

        var project = await services.GetRequiredService<INetworkStorageProjectService>().FindProjectAsync(projectId, ct);
        var policy = PolicyFor(project, services);
        if (project is null || !policy.Enabled)
        {
            await ServeNotFoundAsync(context);
            return;
        }
        if (!project.Enabled)
        {
            await FailAsync(context, requestId, StatusCodes.Status403Forbidden, "PROJECT_DISABLED", "Project is disabled.");
            return;
        }

        var media = services.GetRequiredService<NetworkStorageMediaService>();
        var now = NowUnixMs();
        var verified = media.VerifyGrant(projectId, grantToken, now);
        if (!verified.Ok)
        {
            await FailAsync(context, requestId, verified.Failure!);
            return;
        }
        var claims = verified.Value!;
        if (!policy.Purposes.TryGetValue(claims.Purpose, out var purpose) || !purpose.Enabled)
        {
            await FailAsync(context, requestId, StatusCodes.Status403Forbidden, "MEDIA_PURPOSE_DISABLED",
                "This media purpose is disabled for the project.");
            return;
        }

        // Neither the declared nor the chunked oversize check spends the grant, so the client can retry with a
        // smaller file on the same grant.
        if (context.Request.ContentLength is { } declared && declared > claims.MaxBytes)
        {
            await FailAsync(context, requestId, StatusCodes.Status413PayloadTooLarge, "MEDIA_TOO_LARGE",
                $"Upload exceeds the granted size of {claims.MaxBytes} bytes.");
            return;
        }

        var bytes = await ReadCappedAsync(context.Request.Body, claims.MaxBytes, ct);
        if (bytes is null)
        {
            await FailAsync(context, requestId, StatusCodes.Status413PayloadTooLarge, "MEDIA_TOO_LARGE",
                $"Upload exceeds the granted size of {claims.MaxBytes} bytes.");
            return;
        }

        // Validation runs first; the grant is consumed only once the bytes are known to be acceptable.
        var stored = await media.CompleteUploadAsync(claims, purpose, bytes, context.Request.ContentType, now, ct);
        if (!stored.Ok)
        {
            await FailAsync(context, requestId, stored.Failure!);
            return;
        }
        var record = stored.Value!;
        await WriteJsonAsync(context, requestId, StatusCodes.Status201Created, new
        {
            ok = true,
            mediaId = record.MediaId,
            sha256 = record.Sha256,
            contentType = record.ContentType,
            sizeBytes = record.SizeBytes,
            width = record.Width,
            height = record.Height,
            visibility = record.Visibility,
            expiresAtUnixMs = record.ExpiresAtUnixMs,
            _requestId = requestId,
        });
    }

    private static async Task ReadObjectAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        var projectId = (string?)context.GetRouteValue("projectId") ?? string.Empty;
        var mediaId = (string?)context.GetRouteValue("mediaId") ?? string.Empty;
        var services = context.RequestServices;
        var ct = context.RequestAborted;

        var project = await services.GetRequiredService<INetworkStorageProjectService>().FindProjectAsync(projectId, ct);
        if (project is null || !PolicyFor(project, services).Enabled)
        {
            await ServeNotFoundAsync(context);
            return;
        }
        if (!project.Enabled)
        {
            await FailAsync(context, requestId, StatusCodes.Status403Forbidden, "PROJECT_DISABLED", "Project is disabled.");
            return;
        }

        var callerIsSecret = await IsSecretKeyAsync(services, ReadApiKey(context), projectId, ct);
        var media = services.GetRequiredService<NetworkStorageMediaService>();
        var result = await media.ReadObjectAsync(projectId, mediaId, callerIsSecret, HttpMethods.IsGet(context.Request.Method), NowUnixMs(), ct);
        if (!result.Ok)
        {
            await FailAsync(context, requestId, result.Failure!);
            return;
        }

        var (record, blob) = result.Value;
        using var body = blob;
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = record.ContentType;
        response.ContentLength = record.SizeBytes;
        response.Headers["X-Request-Id"] = requestId;
        // Uploads are untrusted bytes: never let a browser sniff them into another type, and
        // render them inside a sandboxed origin if they are opened directly.
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "sandbox";
        response.Headers.ETag = $"\"{record.Sha256}\"";
        response.Headers.CacheControl = record.Visibility == MediaPolicyParser.VisibilityPublic
            ? "public, max-age=3600"
            : "private, no-store";
        // HEAD carries headers only; GET streams the blob to the response without buffering it.
        if (blob is not null)
            await blob.Content.CopyToAsync(response.Body, ct);
    }

    /// <summary>
    /// Reads at most <paramref name="max"/> bytes. Returns null once the body is larger, so an
    /// over-limit body is never buffered in full.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static MediaProjectPolicy PolicyFor(WorkspaceProject? project, IServiceProvider services)
    {
        if (project?.Media is null) return MediaProjectPolicy.Disabled;
        var hardMax = services.GetRequiredService<MediaServerOptions>().MaxObjectBytes;
        return MediaPolicyParser.Parse(JsonSerializer.SerializeToElement(project.Media), hardMax);
    }

    private static async Task<bool> IsSecretKeyAsync(IServiceProvider services, string? apiKey, string projectId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) return false;
        var auth = await services.GetRequiredService<IStorageApiKeyResolver>().ResolveApiKeyAsync(apiKey, projectId, ct);
        return auth is { Enabled: true } && string.Equals(auth.KeyType, "secret", StringComparison.Ordinal);
    }

    /// <summary>
    /// The API key comes from the x-api-key header only. A query-string key is never read: proxies and browser
    /// history record query strings, so a secret key there would leak.
    /// </summary>
    private static string? ReadApiKey(HttpContext context) =>
        context.Request.Headers["x-api-key"].FirstOrDefault() is { Length: > 0 } key ? key : null;

    private static async Task<JsonElement?> ReadJsonObjectAsync(HttpContext context)
    {
        try
        {
            var element = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long ReadInt64(JsonElement body, string name)
        => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Task ServeNotFoundAsync(HttpContext context) => NetworkStorageGatewayEndpoints.ServeNotFoundAsync(context);

    private static Task FailAsync(HttpContext context, string requestId, MediaFailure failure)
        => FailAsync(context, requestId, failure.Status, failure.Code, failure.Message);

    private static Task FailAsync(HttpContext context, string requestId, int status, string code, string message)
        => WriteJsonAsync(context, requestId, status, new
        {
            ok = false,
            status,
            error = new { code, message, requestId, docsUrl = $"{Wiki}/media" },
            message,
            _requestId = requestId,
        });

    private static async Task WriteJsonAsync(HttpContext context, string requestId, int status, object body)
    {
        context.Response.Headers["X-Request-Id"] = requestId;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
