using System.Diagnostics;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Server.Activity;

namespace SboxNetworkStorage.Server.Middleware;

/// <summary>
/// Meters Network Storage data-plane requests (<c>/v3</c>, <c>/v1</c>,
/// <c>/api/storage</c>, <c>/api/network-storage</c>, and published
/// <c>/pages</c>/<c>/api/pages</c> responses) into
/// <see cref="NetworkStorageUsageTracker"/>: duration, bytes in/out (counting
/// passthrough response stream with no buffering), status, and semantic annotations
/// set via <see cref="NetworkStorageUsageContext"/>.
///
/// <para>Skips: non-data-plane paths (zero overhead), OPTIONS preflight, 401
/// responses, and every request whose handler did not annotate it after
/// authenticating (unauthenticated traffic is never metered under a
/// caller-chosen project). Metering is strictly best-effort — it never throws
/// into the pipeline.</para>
///
/// <para>The same requests are also written to the project's runtime request log
/// (<see cref="RuntimeActivityLog"/>), plus rejected requests (status 400 or more)
/// that never authenticated, under the project ID in the URL, so wrong keys and
/// failed s&amp;box auth show up in the dashboard.</para>
/// </summary>
public sealed class NetworkStorageUsageMiddleware(
    RequestDelegate next,
    NetworkStorageUsageTracker tracker,
    RuntimeActivityLog activity,
    ILogger<NetworkStorageUsageMiddleware> logger)
{
    // Segment-boundary prefixes: "/api/storage" does NOT match "/api/storage-browse".
    private static readonly PathString[] s_prefixes =
    [
        new("/v3"), new("/v1"), new("/api/storage"), new("/api/network-storage"),
        new("/pages"), new("/api/pages"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsDataPlanePath(context.Request.Path) || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var originalRequestBody = context.Request.Body;
        var originalResponseBody = context.Response.Body;
        var requestCounting = new CountingReadStream(originalRequestBody);
        var responseCounting = new CountingWriteStream(originalResponseBody);
        context.Request.Body = requestCounting;
        context.Response.Body = responseCounting;
        var faulted = true;
        try
        {
            await next(context);
            faulted = false;
        }
        finally
        {
            context.Request.Body = originalRequestBody;
            context.Response.Body = originalResponseBody;
            var bytesIn = Math.Max(context.Request.ContentLength ?? 0, requestCounting.BytesRead);
            // An escaping exception becomes a 500 in ExceptionHandlingMiddleware after this point.
            var status = faulted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            Record(context, status, bytesIn, responseCounting.BytesWritten, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private static bool IsDataPlanePath(PathString path)
    {
        foreach (var prefix in s_prefixes)
        {
            if (path.StartsWithSegments(prefix)) return true;
        }
        return false;
    }

    private void Record(HttpContext context, int status, long bytesIn, long bytesOut, double durationMs)
    {
        try
        {
            var annotation = NetworkStorageUsageContext.Get(context);
            if (annotation is { Suppressed: true }) return;

            var logProjectId = annotation?.ProjectId
                ?? (status >= StatusCodes.Status400BadRequest ? context.GetRouteValue("projectId") as string : null);
            if (logProjectId is not null)
                activity.RecordRequest(logProjectId, context.Request.Method, context.Request.Path.Value ?? "/", status, durationMs);

            if (annotation is null || status == StatusCodes.Status401Unauthorized) return;

            tracker.Track(
                annotation.ProjectId,
                annotation.Kind,
                context.Request.Method,
                bytesIn: bytesIn,
                bytesOut: bytesOut,
                durationMs: durationMs,
                endpointSlug: annotation.EndpointSlug,
                isError: status >= StatusCodes.Status400BadRequest,
                storageDeltaBytes: annotation.StorageDeltaBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Network Storage request could not be metered for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }
    }

    /// <summary>
    /// Byte-counting passthrough response stream. No buffering.
    /// </summary>
    private sealed class CountingWriteStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }
    }

    /// <summary>
    /// Byte-counting passthrough request stream. This captures chunked bodies
    /// whose size is not available through Content-Length.
    /// </summary>
    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
