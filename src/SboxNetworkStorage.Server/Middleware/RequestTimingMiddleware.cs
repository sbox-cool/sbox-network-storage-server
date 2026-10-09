using System.Diagnostics;

namespace SboxNetworkStorage.Server.Middleware;

public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            logger.LogDebug("HTTP {Method} {Path} status={StatusCode} latencyMs={LatencyMs} correlationId={CorrelationId}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, CorrelationContext.Get(context));
        }
    }
}
