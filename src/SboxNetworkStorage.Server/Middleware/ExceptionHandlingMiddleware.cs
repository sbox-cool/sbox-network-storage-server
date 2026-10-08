using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Infrastructure.Observability;

namespace SboxNetworkStorage.Server.Middleware;

/// <summary>
/// Converts unhandled exceptions into the same <c>application/problem+json</c>
/// responses the managed service returns: 503 + Retry-After for database
/// outages, 500 otherwise, always carrying the correlation id.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    IErrorArchive errorArchive,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception exception)
        {
            await HandleExceptionAsync(context, exception);
        }
    }

    /// <summary>
    /// Transient database failures (connection refused/dropped, admin shutdown, timeouts,
    /// locked files) are retryable outages. Deterministic errors such as constraint
    /// violations stay 500s so real bugs remain visible, as in the managed service.
    /// </summary>
    public static bool IsDatabaseOutage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { IsTransient: true } or TimeoutException or DependencyUnavailableException
                or System.Net.Sockets.SocketException or EndOfStreamException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = CorrelationContext.Get(context);
        var isOutage = IsDatabaseOutage(exception);
        var statusCode = isOutage ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;

        var captured = new CapturedErrorDto(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "sbox-ns",
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            statusCode,
            exception.GetType().Name,
            correlationId,
            exception.Message,
            exception.ToString());

        try
        {
            await errorArchive.CaptureAsync(captured, CancellationToken.None);
        }
        catch (Exception reportException)
        {
            logger.LogWarning(reportException, "Failed to record unhandled exception correlationId={CorrelationId}", correlationId);
        }

        // Alert the operator through the shared sink (log + Discord/SMTP when
        // configured). Resolved from the request services so this middleware
        // keeps its constructor; fire-and-forget so a slow channel never
        // delays the error response. Never masks the original failure.
        try
        {
            var alertSink = context.RequestServices.GetService<IExceptionAlertSink>();
            if (alertSink is not null)
            {
                AlertFireAndForget.Run(alertSink.NotifyAsync(captured, CancellationToken.None), logger, "alert for unhandled exception");
            }
        }
        catch { /* best-effort */ }

        logger.LogError(exception,
            "Unhandled exception method={Method} path={Path} classification={Classification} correlationId={CorrelationId} dependencyTimings={DependencyTimings}",
            context.Request.Method,
            context.Request.Path.Value,
            captured.Classification,
            correlationId,
            DependencyTimingContext.Snapshot(context));

        if (context.Response.HasStarted)
        {
            throw exception;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        if (isOutage)
        {
            context.Response.Headers["Retry-After"] = "30";
        }

        var problem = new ProblemDetails
        {
            Title = isOutage ? "Database unavailable" : "Internal server error",
            Detail = isOutage
                ? "The storage database is temporarily unreachable. Please retry shortly."
                : "An unexpected error occurred. Reference the correlation ID when reporting the problem.",
            Status = statusCode,
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = correlationId;

        try
        {
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions), context.RequestAborted);
        }
        catch (Exception writeException) when (writeException is OperationCanceledException or IOException)
        {
            logger.LogDebug(writeException, "Failed to write error response correlationId={CorrelationId}", correlationId);
        }
    }
}
