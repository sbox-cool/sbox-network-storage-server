using System.Diagnostics;
using SboxNetworkStorage.Application.Diagnostics;
using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Routing;

namespace SboxNetworkStorage.Server.Middleware;

public sealed class RequestTimingMiddleware(RequestDelegate next, IRequestStats stats, ILogger<RequestTimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var active = stats.BeginRequest();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var statusCode = context.Response.HasStarted ? context.Response.StatusCode : Math.Max(context.Response.StatusCode, 200);
            stats.CompleteRequest(statusCode);
            var owner = context.GetEndpoint()?.Metadata.GetMetadata<RouteOwnershipMetadata>()?.Owner.ToDisplayName()
                ?? RouteOwnershipResolver.ResolveOwner(context.Request.Path).ToDisplayName();
            logger.LogInformation(
                "HTTP {Method} {Path} owner={RouteOwner} status={StatusCode} latencyMs={LatencyMs} correlationId={CorrelationId} dependencyTimings={DependencyTimings}",
                context.Request.Method,
                context.Request.Path.Value,
                owner,
                statusCode,
                stopwatch.Elapsed.TotalMilliseconds,
                CorrelationContext.Get(context),
                DependencyTimingContext.Snapshot(context));
        }
    }
}
