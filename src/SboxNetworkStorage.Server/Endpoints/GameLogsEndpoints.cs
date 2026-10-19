using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.GameLogs;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Structured game logs (epic #111, L1 ingest and L2 search), served at <c>/v3/game-logs/{projectId}</c> and,
/// through <see cref="ApiRootRewriteMiddleware"/>, at <c>/api/game-logs/{projectId}</c>.
/// Per project opt-in: while logs are disabled every request answers like an unknown route.
/// </summary>
internal static class GameLogsEndpoints
{
    /// <summary>Per-actor ingest budget, in entries per minute. The per-project budget comes from the project's settings.</summary>
    internal const int ActorRateLimitPerMinute = 1_200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapGameLogs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v3/game-logs/{projectId}", IngestAsync)
            .WithDisplayName("Game logs ingest");
        endpoints.MapGet("/v3/game-logs/{projectId}", SearchAsync)
            .WithDisplayName("Game logs search");
        return endpoints;
    }

    private sealed record Caller(string ApiKey, StorageApiKeyAuthResult Auth, NetworkStorageProjectAccessResult Access, GameLogPolicy Policy)
    {
        public bool Trusted => string.Equals(Auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct BatchParse(JsonDocument? Document, int Status, string? Code, string? Detail)
    {
        public static BatchParse Fail(int status, string code, string detail) => new(null, status, code, detail);
    }

    private static async Task IngestAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var caller = await AuthorizeAsync(context, projectId);
        if (caller is null) return;
        if (caller.Trusted && !ApiKeyPermissionPolicy.HasPermission(caller.Auth, "logs", "w"))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "FORBIDDEN",
                "Writing game logs with a secret key requires logs:write.");
            return;
        }

        var request = context.Request;
        var services = context.RequestServices;
        var onBehalfOf = request.Headers["x-on-behalf-of"].FirstOrDefault();
        if (!caller.Trusted && !string.IsNullOrEmpty(onBehalfOf))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "ACTOR_NOT_PERMITTED",
                "Only secret-key callers may log on behalf of another player.");
            return;
        }

        if (caller.Trusted && !string.IsNullOrEmpty(onBehalfOf) && !AuthSessionEndpoints.IsPlausibleSteamId(onBehalfOf))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "INVALID_STEAMID",
                "x-on-behalf-of must be a plausible numeric ID starting with 7 or 9.");
            return;
        }

        var bytes = await ReadBoundedAsync(request, GameLogPolicy.MaxBatchBytes, context.RequestAborted);
        if (bytes is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "BATCH_TOO_LARGE",
                $"A batch may be at most {GameLogPolicy.MaxBatchBytes} bytes.");
            return;
        }

        var batch = ParseBatch(request, bytes);
        if (batch.Document is null)
        {
            await WriteErrorAsync(context, batch.Status, batch.Code!, batch.Detail!);
            return;
        }

        using var parsed = batch.Document;
        var entries = parsed.RootElement;
        var limiter = services.GetRequiredService<GameLogRateLimiter>();
        var queue = services.GetRequiredService<GameLogQueue>();

        // Identity comes first, so a request that fails identity consumes no budget at all.
        // Public keys identify the project, not a player: the actor is the verified player, never a body field.
        // Secret keys (dedicated servers) name the player, via x-on-behalf-of or the body's actor field.
        string? verifiedActor = caller.Trusted ? onBehalfOf : null;
        if (!caller.Trusted)
        {
            var identity = await PlayerIdentity.ResolveAsync(
                context, default, projectId, "game-logs", caller.ApiKey, caller.Auth.UserId,
                required: true, sessionsEnabled: caller.Access.Project.EnableAuthSessions == true,
                claimedSteamId: PlayerIdentity.ClaimedSteamId(request, default));
            if (!identity.Ok)
            {
                await WriteErrorAsync(context, identity.Status, identity.Code!, identity.Message!);
                return;
            }

            verifiedActor = identity.SteamId;
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var outcome = GameLogEntryNormalizer.Normalize(entries, new GameLogIngestContext(
            ProjectId: projectId,
            Now: now,
            Policy: caller.Policy,
            Trusted: caller.Trusted,
            VerifiedActorSteamId: verifiedActor,
            IsSteamId: AuthSessionEndpoints.IsPlausibleSteamId,
            NextRandom: Random.Shared.NextDouble));

        // Only entries that pass normalization are billed; rejected entries cost nothing. Sampled entries were
        // accepted by validation, so they are billed too. The actor bucket is charged first, so an over-limit actor
        // never drains the project budget.
        var billable = outcome.Rows.Count + outcome.Sampled;
        if (!caller.Trusted)
        {
            var actorLimit = limiter.TryConsume($"actor:{projectId}:{verifiedActor}", ActorRateLimitPerMinute, billable);
            if (!actorLimit.Allowed)
            {
                await WriteRateLimitedAsync(context, queue, projectId, billable, actorLimit.RetryAfterSeconds);
                return;
            }
        }

        var projectLimit = limiter.TryConsume("project:" + projectId, caller.Policy.RateLimitPerMinute, billable);
        if (!projectLimit.Allowed)
        {
            await WriteRateLimitedAsync(context, queue, projectId, billable, projectLimit.RetryAfterSeconds);
            return;
        }

        var rejected = outcome.Rejected.ToList();
        var accepted = 0;
        foreach (var item in outcome.Rows)
        {
            if (queue.TryEnqueue(item.Row)) accepted++;
            else rejected.Add(new GameLogRejection(item.Index, "QUEUE_FULL"));
        }

        rejected.Sort((a, b) => a.Index.CompareTo(b.Index));
        queue.RecordRejected(projectId, outcome.Rejected.Count);
        queue.RecordSampled(projectId, outcome.Sampled);
        queue.RecordTruncated(projectId, outcome.Truncated.Count);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        await context.Response.WriteAsJsonAsync(new
        {
            accepted,
            rejected,
            truncated = outcome.Truncated,
            sampled = outcome.Sampled,
        }, JsonOptions);
    }

    private static async Task SearchAsync(HttpContext context)
    {
        var projectId = (string?)context.GetRouteValue("projectId") ?? "";
        var caller = await AuthorizeAsync(context, projectId);
        if (caller is null) return;

        // Search reads other players' logs: secret keys only. ApiKeyPermissionPolicy does not restrict public keys.
        if (!caller.Trusted || !ApiKeyPermissionPolicy.HasPermission(caller.Auth, "logs", "r"))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "FORBIDDEN",
                "Searching game logs requires a secret key with logs:read.");
            return;
        }

        var pairs = context.Request.Query
            .Select(q => new KeyValuePair<string, string>(q.Key, q.Value.FirstOrDefault() ?? ""))
            .ToList();
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var parsed = GameLogSearchParser.Parse(projectId, pairs, caller.Policy, AuthSessionEndpoints.IsPlausibleSteamId, now);
        if (!parsed.Ok)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, parsed.ErrorCode!, parsed.ErrorMessage!);
            return;
        }

        var query = parsed.Query!;
        if (query.StreamFloorsUnixMs.Count == 0)
        {
            await WriteEntriesAsync(context, [], null);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IGameLogStore>();
        // One extra row tells whether another page exists without a second query.
        var rows = await store.SearchGameLogsAsync(query with { Limit = query.Limit + 1 }, context.RequestAborted);
        var page = rows.Take(query.Limit).ToList();
        string? nextCursor = null;
        if (rows.Count > query.Limit && page.Count > 0)
        {
            var last = page[^1];
            nextCursor = GameLogSearchParser.FormatCursor(new GameLogCursor(last.TsUnixMs, last.EntryId));
        }

        await WriteEntriesAsync(context, page, nextCursor);
    }

    /// <summary>
    /// Resolves the caller's key and project. Any failure before logs are known to be enabled (no key, unknown key,
    /// no project, logs off) answers exactly like an unknown route, so the feature is invisible until an owner enables it.
    /// </summary>
    private static async Task<Caller?> AuthorizeAsync(HttpContext context, string projectId)
    {
        var services = context.RequestServices;
        var ct = context.RequestAborted;
        var apiKey = StorageApiEndpoints.ExtractApiKey(context.Request);
        var resolver = services.GetRequiredService<IStorageApiKeyResolver>();
        var projects = services.GetRequiredService<INetworkStorageProjectService>();

        // A key in the query string ends up in access logs and proxies. Refuse it before any key lookup.
        if (context.Request.Query.ContainsKey("apiKey"))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "API_KEY_IN_QUERY",
                "Send the API key in the x-api-key header; ?apiKey= is not accepted on game-log routes.");
            return null;
        }

        var auth = string.IsNullOrEmpty(apiKey) ? null : await resolver.ResolveApiKeyAsync(apiKey, projectId, ct);
        var access = auth is null ? null : await projects.ResolveProjectAccessAsync(auth.UserId, projectId, ct);
        if (auth is null || access is null || access.Project.GameLogs?.Enabled != true)
        {
            await NetworkStorageGatewayEndpoints.ServeNotFoundAsync(context);
            return null;
        }

        if (!access.Project.Enabled)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "PROJECT_DISABLED", "This project is disabled.");
            return null;
        }

        if (!auth.Enabled)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "KEY_DISABLED", "This API key is disabled.");
            return null;
        }

        NetworkStorageUsageContext.SetAuthenticated(context, projectId);
        var settings = access.Project.GameLogs ?? new GameLogSettings();
        return new Caller(apiKey!, auth, access, new GameLogPolicy(settings));
    }

    /// <summary>
    /// Parses the batch. A JSON array, or <c>application/x-ndjson</c> with one JSON value per line. Every line must be a
    /// single complete value, so one line cannot smuggle extra entries past the count limit.
    /// </summary>
    private static BatchParse ParseBatch(HttpRequest request, byte[] bytes)
    {
        if (request.ContentType?.StartsWith("application/x-ndjson", StringComparison.OrdinalIgnoreCase) == true)
            return ParseNdjson(Encoding.UTF8.GetString(bytes));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return BatchParse.Fail(StatusCodes.Status400BadRequest, "INVALID_JSON", "The body is not valid JSON.");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            return BatchParse.Fail(StatusCodes.Status400BadRequest, "INVALID_JSON",
                "The body must be a JSON array of entries, or application/x-ndjson.");
        }

        if (document.RootElement.GetArrayLength() > GameLogPolicy.MaxBatchEntries)
        {
            document.Dispose();
            return BatchParse.Fail(StatusCodes.Status413PayloadTooLarge, "BATCH_TOO_LARGE",
                $"A batch may hold at most {GameLogPolicy.MaxBatchEntries} entries.");
        }

        return new BatchParse(document, 0, null, null);
    }

    private static BatchParse ParseNdjson(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > GameLogPolicy.MaxBatchEntries)
            return BatchParse.Fail(StatusCodes.Status413PayloadTooLarge, "BATCH_TOO_LARGE",
                $"A batch may hold at most {GameLogPolicy.MaxBatchEntries} entries.");

        var builder = new StringBuilder("[");
        for (var i = 0; i < lines.Length; i++)
        {
            try
            {
                using var _ = JsonDocument.Parse(lines[i]);
            }
            catch (JsonException)
            {
                return BatchParse.Fail(StatusCodes.Status400BadRequest, "INVALID_JSON",
                    $"Entry {(i + 1).ToString(CultureInfo.InvariantCulture)} is not valid JSON.");
            }

            if (i > 0) builder.Append(',');
            builder.Append(lines[i]);
        }

        builder.Append(']');
        return new BatchParse(JsonDocument.Parse(builder.ToString()), 0, null, null);
    }

    /// <summary>Reads the body up to <paramref name="max"/> bytes. Returns null when it is longer.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), ct);
            if (read == 0) break;
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task WriteRateLimitedAsync(HttpContext context, GameLogQueue queue, string projectId, int entryCount, int retryAfterSeconds)
    {
        queue.RecordRateLimited(projectId, entryCount);
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = "LOG_RATE_LIMITED",
            detail = "Game-log ingest rate limit exceeded.",
            retryAfterSeconds,
        }, JsonOptions);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { ok = false, error = code, detail }, JsonOptions);
    }

    private static Task WriteEntriesAsync(HttpContext context, IReadOnlyList<GameLogRow> rows, string? nextCursor)
    {
        var entries = rows.Select(row => new
        {
            entryId = row.EntryId,
            tsUnixMs = row.TsUnixMs,
            stream = row.Stream,
            level = row.Level,
            @event = row.Event,
            actor = row.ActorSteamId,
            target = row.TargetSteamId,
            revisionId = row.RevisionId,
            clientType = row.ClientType,
            libraryVersion = row.LibraryVersion,
            message = row.Message,
            attrs = JsonSerializer.Deserialize<JsonElement>(row.AttrsJson),
            traceId = row.TraceId,
            source = row.Source,
        });
        return context.Response.WriteAsJsonAsync(new { entries, nextCursor }, JsonOptions);
    }
}
