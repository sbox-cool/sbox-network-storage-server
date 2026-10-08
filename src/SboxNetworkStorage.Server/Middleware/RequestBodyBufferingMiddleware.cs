using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Server.Middleware;

/// <summary>
/// Buffers request bodies for routes that forward them upstream to the legacy
/// Bun compatibility worker (tools pipeline: decimation, material/model
/// conversion, animation conversion).
///
/// Without this, Kestrel's raw body pipe is drained before the tool
/// compatibility controllers read it, so multipart uploads reach the Bun worker
/// empty ("Sent 0 request content bytes" / "FormData parse error missing final
/// boundary"). Buffering here — before the rest of the pipeline touches the
/// body — copies it into a seekable stream that survives to the forwarding
/// controller.
///
/// Buffers to memory up to a threshold then to a temp file, sized to the tools
/// 1 GiB model payload limit.
/// </summary>
public sealed class RequestBodyBufferingMiddleware(
    RequestDelegate next,
    int memoryThreshold,
    long bufferLimit,
    ILogger<RequestBodyBufferingMiddleware> logger)
{
    private const int DiagnosticTailBytes = 64;
    private static readonly PathString[] ProxyPathPrefixes =
    [
        new("/api/decimate"),
        new("/api/tools"),
        new("/tools/"),
        new("/api/model-importer"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            foreach (var prefix in ProxyPathPrefixes)
            {
                if (path.StartsWith(prefix.Value!, StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.EnableBuffering(memoryThreshold, bufferLimit);
                    // FileBufferingReadStream.Length is the number of bytes
                    // buffered so far — 0 before the first read, in every case
                    // (verified empirically, with and without a Content-Length
                    // hint). The forwarding controller hands this stream to
                    // StreamContent, which computes the upstream Content-Length
                    // from Length; an unread buffer therefore declares
                    // Content-Length: 0 and the send fails with "content would
                    // exceed Content-Length" for uploads that arrive without a
                    // Content-Length header (chunked). Drain the body once so
                    // the buffer is complete and Length is the true byte count,
                    // then rewind for the downstream handler.
                    var declaredLength = context.Request.ContentLength ?? 0;
                    long received = 0;
                    byte[] tail = [];
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        while (true)
                        {
                            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted);
                            if (read == 0) break;
                            received += read;
                            tail = CaptureTail(tail, buffer, read);
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }

                    // A client that disconnects mid-upload leaves a clean EOF:
                    // the drain completes early with no exception and the
                    // truncated multipart would be forwarded to the Bun worker
                    // as if complete, failing there with "FormData parse error
                    // missing final boundary" (production incidents 2026-08-19).
                    // Reject the truncated body here instead of forwarding it.
                    if (declaredLength > 0 && received < declaredLength)
                    {
                        logger.LogWarning(
                            "Rejecting truncated upload to {Path}: declared {Declared} bytes, received {Received} (tail: {Tail})",
                            path, declaredLength, received, Hex(tail));
                        await RejectAsync(context,
                            "The upload was cut off before it finished; please try again.");
                        return;
                    }

                    // The Bun worker's formData() rejects a multipart that ends
                    // without its closing boundary with a generic 500. Check
                    // the final boundary here, log the diagnostics, and answer
                    // with a clear error instead (production incidents
                    // 2026-08-19: "FormData parse error missing final
                    // boundary" reached Discord from otherwise normal-looking
                    // uploads).
                    if (received > 0 && TryGetMultipartBoundary(context.Request.ContentType, out var boundary)
                        && !HasClosingBoundary(tail, boundary))
                    {
                        logger.LogWarning(
                            "Rejecting multipart upload to {Path} missing its closing boundary: " +
                            "declared {Declared}, received {Received}, boundary {Boundary}, tail {Tail}",
                            path, declaredLength, received, boundary, Hex(tail));
                        await RejectAsync(context,
                            "The upload's multipart body is incomplete (missing its closing boundary); please try again.");
                        return;
                    }

                    context.Request.Body.Position = 0;
                    break;
                }
            }
        }

        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}",
            context.RequestAborted);
    }

    private static bool TryGetMultipartBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType)
            || !contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!part.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = part["boundary=".Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            if (value.Length > 0)
            {
                boundary = value;
                return true;
            }
        }

        return false;
    }

    private static bool HasClosingBoundary(byte[] tail, string boundary)
    {
        var suffix = Encoding.ASCII.GetBytes($"--{boundary}--");
        if (tail.Length < suffix.Length) return false;
        var window = tail.AsSpan();
        // Multipart bodies end with "--boundary--\r\n" (or "\n"); strip the
        // trailing line ending before matching the closing boundary.
        if (window.EndsWith("\r\n"u8)) window = window[..^2];
        else if (window.EndsWith("\n"u8)) window = window[..^1];
        return window.Length >= suffix.Length
            && window[^suffix.Length..].SequenceEqual(suffix);
    }

    /// <summary>
    /// Keeps a rolling tail of the last <see cref="DiagnosticTailBytes"/> bytes
    /// seen so far, for boundary checks and failure diagnostics without
    /// re-reading the buffered stream.
    /// </summary>
    private static byte[] CaptureTail(byte[] existing, byte[] chunk, int count)
    {
        if (count <= 0) return existing;
        if (existing.Length >= DiagnosticTailBytes)
        {
            var keep = DiagnosticTailBytes - count;
            if (keep <= 0)
            {
                var copy = new byte[DiagnosticTailBytes];
                chunk.AsSpan(count - DiagnosticTailBytes, DiagnosticTailBytes).CopyTo(copy);
                return copy;
            }

            var merged = new byte[DiagnosticTailBytes];
            existing.AsSpan(existing.Length - keep).CopyTo(merged);
            chunk.AsSpan(0, count).CopyTo(merged.AsSpan(keep));
            return merged;
        }

        var combined = new byte[existing.Length + count];
        existing.CopyTo(combined, 0);
        chunk.AsSpan(0, count).CopyTo(combined.AsSpan(existing.Length));
        return combined.Length > DiagnosticTailBytes
            ? combined[^DiagnosticTailBytes..]
            : combined;
    }

    private static string Hex(byte[] bytes)
    {
        if (bytes.Length == 0) return "<empty>";
        return Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, DiagnosticTailBytes));
    }
}
