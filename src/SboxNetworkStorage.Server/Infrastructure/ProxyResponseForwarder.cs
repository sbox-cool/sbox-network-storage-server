using System.Buffers;
using System.Text;

namespace SboxNetworkStorage.Server.Infrastructure;

/// <summary>
/// Shared response-body forwarding for the .NET → hidden Bun proxy boundaries
/// (Network Storage gateway, legacy website compatibility endpoints, and the legacy relay).
///
/// Public routes terminate in ASP.NET Core and proxy to the storage-api / legacy website Bun
/// workers. Those workers archive their own 500s into the Bun-owned rows of <c>internal_errors</c>
/// (<c>storage_backend = 'postgres'</c>). The .NET-rendered <c>/admin/errors</c> view lists every
/// unresolved row regardless of backend, so worker 500s are visible — but only when the failing
/// call actually flows through a .NET proxy boundary. Worker-internal 500s (e.g. a storage-api →
/// storage-api <c>package-sync</c> on 127.0.0.1) never reach .NET, so capturing at this boundary is
/// still required to surface proxied upstream 500s in the .NET Discord alert channel and to attach
/// .NET request context. Capturing here mirrors <c>ToolCompatibilityApiController</c>'s upstream-5xx handling.
/// </summary>
public static class ProxyResponseForwarder
{
    private const int CopyBufferBytes = 64 * 1024;

    // Error bodies are small JSON; only the leading slice is needed to identify the failure, and
    // it bounds how much of an arbitrarily large upstream body is held in memory for the report.
    private const int MaxCaptureSnippetBytes = 4 * 1024;

    /// <summary>
    /// When <paramref name="upstreamResponse"/> is a 5xx, streams its body to the client while
    /// capturing a bounded leading snippet into the .NET error archive + Discord alert via
    /// <paramref name="reporter"/>, then returns <c>true</c>. For sub-500 responses nothing is
    /// written and <c>false</c> is returned so the caller forwards the body with its own strategy.
    /// The status line and headers MUST already be committed by the caller before this is invoked.
    /// </summary>
    public static async Task<bool> TryForwardServerErrorAsync(
        HttpContext context,
        HttpResponseMessage upstreamResponse,
        IProxyErrorReporter reporter,
        string classification,
        CancellationToken cancellationToken)
    {
        var status = (int)upstreamResponse.StatusCode;
        if (status < StatusCodes.Status500InternalServerError)
        {
            return false;
        }

        using var snippet = new MemoryStream(MaxCaptureSnippetBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            await using var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await upstreamBody.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                var room = MaxCaptureSnippetBytes - (int)snippet.Length;
                if (room > 0)
                {
                    snippet.Write(buffer, 0, Math.Min(read, room));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // Capture even when the client aborts mid-stream: an upstream 500 must always reach the
            // .NET admin error log + Discord. ProxyErrorReporter archives/alerts on an uncancellable
            // token, so this records regardless of the request lifetime.
            await reporter.CaptureAsync(context, status, classification, BuildMessage(status, snippet), exception: null);
        }

        return true;
    }

    private static string BuildMessage(int status, MemoryStream snippet)
    {
        var body = Encoding.UTF8.GetString(snippet.GetBuffer(), 0, (int)snippet.Length).Trim();
        return body.Length == 0
            ? $"Upstream worker returned HTTP {status}."
            : $"Upstream worker returned HTTP {status}: {body}";
    }
}
