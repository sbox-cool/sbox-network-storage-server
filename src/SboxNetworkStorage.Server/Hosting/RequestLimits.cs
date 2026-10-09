using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>Route families that carry their own body limit and rate-limit bucket.</summary>
public enum RouteGroup
{
    /// <summary>No group: the default body limit applies and the route is not rate limited.</summary>
    None,
    /// <summary>Game data plane (<c>/v1</c>, <c>/v3</c>, <c>/api/storage</c> and aliases).</summary>
    Game,
    /// <summary>Secret-key management and sync (<c>/v3/manage</c>).</summary>
    Management,
    /// <summary>Player auth sessions.</summary>
    AuthSession,
}

/// <summary>Token-bucket settings for one route group, applied per client address.</summary>
public sealed class RateBucketOptions
{
    /// <summary>Requests allowed at once before the bucket is empty.</summary>
    public int Burst { get; set; }

    /// <summary>Tokens added every <see cref="Period"/>.</summary>
    public int Refill { get; set; }

    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Request body limits and per-client rate limits, bound from <c>server.limits.*</c>.</summary>
public sealed class RequestLimitOptions
{
    public long DefaultBodyBytes { get; set; }
    public long DataPlaneBodyBytes { get; set; }
    public long ManagementBodyBytes { get; set; }

    public RateBucketOptions Game { get; set; } = new();
    public RateBucketOptions Management { get; set; } = new();
    public RateBucketOptions AuthSession { get; set; } = new();

    /// <summary>
    /// Trust <c>CF-Connecting-IP</c> from a loopback peer. True only while tunnel mode runs
    /// the local connector, which is the sole loopback client that sets this header honestly.
    /// </summary>
    public bool TrustConnectingIpHeader { get; set; }

    public long BodyLimitFor(RouteGroup group) => group switch
    {
        RouteGroup.Game or RouteGroup.AuthSession => DataPlaneBodyBytes,
        RouteGroup.Management => ManagementBodyBytes,
        _ => DefaultBodyBytes,
    };

    public RateBucketOptions? BucketFor(RouteGroup group) => group switch
    {
        RouteGroup.Game => Game,
        RouteGroup.Management => Management,
        RouteGroup.AuthSession => AuthSession,
        _ => null,
    };

    public void Apply(EffectiveConfig config)
    {
        DefaultBodyBytes = Kibibytes(config, "server.limits.default");
        DataPlaneBodyBytes = Kibibytes(config, "server.limits.data_plane");
        ManagementBodyBytes = Kibibytes(config, "server.limits.management");
        Game = Bucket(config, "game");
        Management = Bucket(config, "management");
        AuthSession = Bucket(config, "auth_session");
        TrustConnectingIpHeader = config.GetBoolean("tunnel.enabled");
    }

    private static long Kibibytes(EffectiveConfig config, string key) => Math.Max(1, config.GetInteger(key)) * 1024;

    private static RateBucketOptions Bucket(EffectiveConfig config, string name) => new()
    {
        Burst = (int)Math.Clamp(config.GetInteger($"server.limits.{name}_burst"), 1, int.MaxValue),
        Refill = (int)Math.Clamp(config.GetInteger($"server.limits.{name}_per_second"), 1, int.MaxValue),
    };
}

/// <summary>Maps a matched endpoint to its <see cref="RouteGroup"/> from its route template.</summary>
public static class RouteGroups
{
    private static readonly string[] AuthSessionPrefixes = ["/v3/auth-sessions", "/v3/sessions", "/v1/auth-sessions", "/v1/sessions"];
    private static readonly string[] GamePrefixes = ["/v3", "/v1", "/api/v3", "/api/storage", "/api/network-storage", "/pages", "/api/pages"];

    public static RouteGroup Of(Endpoint? endpoint)
    {
        if (endpoint is not RouteEndpoint { RoutePattern.RawText: { } template }) return RouteGroup.None;

        foreach (var prefix in AuthSessionPrefixes)
        {
            if (HasSegmentPrefix(template, prefix)) return RouteGroup.AuthSession;
        }

        // The revision handshake is a public-key game call that lives under the manage prefix.
        if (HasSegmentPrefix(template, "/v3/manage") || HasSegmentPrefix(template, "/v1/manage"))
        {
            return template.EndsWith("/revision-init", StringComparison.Ordinal) ? RouteGroup.Game : RouteGroup.Management;
        }

        foreach (var prefix in GamePrefixes)
        {
            if (HasSegmentPrefix(template, prefix)) return RouteGroup.Game;
        }

        return RouteGroup.None;
    }

    private static bool HasSegmentPrefix(string template, string prefix)
        => template.StartsWith(prefix, StringComparison.Ordinal)
           && (template.Length == prefix.Length || template[prefix.Length] == '/');
}

/// <summary>
/// The connection's client address. Forwarded headers are applied by the forwarded-headers
/// middleware for loopback peers only; <c>CF-Connecting-IP</c> is honoured only from a loopback
/// peer while tunnel mode is active. Anything else a client sends is ignored.
/// </summary>
public static class ClientAddress
{
    private const string ItemKey = "sbox-ns.client-address";
    private const string Unknown = "unknown";

    /// <summary>Records the connection-level override. Must run before the forwarded-headers middleware.</summary>
    public static void Capture(HttpContext context, bool trustConnectingIpHeader)
    {
        if (!trustConnectingIpHeader) return;
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !IPAddress.IsLoopback(peer)) return;
        var values = context.Request.Headers["CF-Connecting-IP"];
        if (values.Count == 1 && IPAddress.TryParse(values[0], out var client))
        {
            context.Items[ItemKey] = client.ToString();
        }
    }

    public static string Resolve(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) && value is string overrideAddress
            ? overrideAddress
            : context.Connection.RemoteIpAddress?.ToString() ?? Unknown;
}

public sealed class ClientAddressMiddleware(RequestDelegate next, IOptions<RequestLimitOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        ClientAddress.Capture(context, options.Value.TrustConnectingIpHeader);
        return next(context);
    }
}

/// <summary>
/// Enforces the body limit of the matched route. The limit comes from endpoint metadata
/// (<see cref="IRequestSizeLimitMetadata"/>, e.g. <c>[RequestSizeLimit]</c>) or else from the route
/// group. A declared <c>Content-Length</c> over the limit is rejected without reading the body, and the
/// stream is capped so a chunked body is cut off at the limit as well. Runs after routing.
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<RequestLimitOptions> options)
{
    /// <summary>Largest declared body that is read and discarded before answering 413.</summary>
    private const long MaxDrainBytes = 4 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        long? limit = endpoint?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is { } declared
            ? declared.MaxRequestBodySize
            : options.Value.BodyLimitFor(RouteGroups.Of(endpoint));

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = limit;
        }

        if (limit is not { } max)
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength > max)
        {
            // Mapped routes drain a modestly oversized upload (bounded memory, no temp
            // file) so the client receives the 413 envelope instead of a reset connection
            // racing its upload. A declared length above MaxDrainBytes, and any unmapped
            // path, is rejected without reading a byte: those are the cheapest pre-auth
            // DoS surface.
            if (endpoint is not null && context.Request.ContentLength <= MaxDrainBytes)
            {
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = MaxDrainBytes;
                }
                try
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or BadHttpRequestException)
                {
                    // Client went away mid-upload; still answer 413 if possible.
                }
            }
            await RejectAsync(context, max);
            return;
        }

        var original = context.Request.Body;
        var capped = new CappedReadStream(original, max);
        context.Request.Body = capped;
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await RejectAsync(context, max);
        }
        finally
        {
            context.Request.Body = original;
        }

        // A handler may swallow the read failure and answer 400 on its own; the oversize is the real cause.
        if (capped.Exceeded && !context.Response.HasStarted && context.Response.StatusCode != StatusCodes.Status413PayloadTooLarge)
        {
            await RejectAsync(context, max);
        }
    }

    private static Task RejectAsync(HttpContext context, long max)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return context.Response.WriteAsJsonAsync(
            new { error = "PAYLOAD_TOO_LARGE", detail = $"Request body exceeds the {max}-byte limit for this route." },
            context.RequestAborted);
    }

    /// <summary>Passes reads through and fails once more than <c>limit</c> bytes would be read.</summary>
    private sealed class CappedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Ask for one byte more than is still allowed, so a body of exactly `limit` bytes passes
            // and the first byte beyond it is detected without reading further.
            var allowed = (int)Math.Min(buffer.Length, limit - _read + 1);
            var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            return Count(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var allowed = (int)Math.Min(buffer.Length, limit - _read + 1);
            return Count(inner.Read(buffer[..allowed]));
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
            {
                Exceeded = true;
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public static class RequestLimitServices
{
    /// <summary>Registers the limit options and the per-client rate limiter for the public route groups.</summary>
    public static IServiceCollection AddRequestLimits(this IServiceCollection services, EffectiveConfig config)
    {
        services.Configure<RequestLimitOptions>(limits => limits.Apply(config));
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RequestLimitOptions>>((rate, limits) =>
        {
            rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var group = RouteGroups.Of(context.GetEndpoint());
                var bucket = limits.Value.BucketFor(group);
                if (bucket is null) return RateLimitPartition.GetNoLimiter("ungrouped");
                return RateLimitPartition.GetTokenBucketLimiter(
                    $"{group}|{ClientAddress.Resolve(context)}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = bucket.Burst,
                        TokensPerPeriod = bucket.Refill,
                        ReplenishmentPeriod = bucket.Period,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            rate.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                var seconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await response.WriteAsJsonAsync(
                    new { error = "RATE_LIMITED", detail = $"Too many requests. Retry after {seconds} seconds." },
                    cancellationToken);
            };
        });
        return services;
    }
}
