using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Infrastructure.Observability;

/// <summary>
/// Fire-and-forget helper for alert-sink calls that must not block the caller
/// (background services, request-critical paths) but whose faults must be
/// observed — not silently dropped as unobserved task exceptions.
///
/// Replaces the bare <c>_ = alertSink.NotifyAsync(...)</c> pattern that discards
/// the task. A faulted delivery is logged here; the underlying archive write
/// inside the sink is the source of truth for whether the error was recorded.
/// </summary>
public static class AlertFireAndForget
{
    /// <summary>
    /// Run <paramref name="task"/> without awaiting, observing any fault via
    /// <paramref name="logger"/>. Never throws.
    /// </summary>
    public static void Run(Task task, ILogger logger, string operationDescription)
    {
        _ = ObserveAsync(task, logger, operationDescription);
    }

    private static async Task ObserveAsync(Task task, ILogger logger, string operationDescription)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fire-and-forget alert task failed: {Operation}", operationDescription);
        }
    }
}

/// <summary>
/// Reports a handler-caught exception (one that will be converted to a 5xx
/// result, so <c>ExceptionHandlingMiddleware</c> never sees it) to the error
/// archive + Discord alert sink. Use from any controller/endpoint catch block
/// that returns <c>StatusCode(500)</c> / <c>Problem()</c> instead of letting the
/// exception propagate.
/// </summary>
public static class HandlerErrorReporter
{
    /// <summary>
    /// Archive + alert a handler-caught exception. Best-effort: never throws,
    /// never masks the original error. Fire-and-forget on the alert so the
    /// response is not delayed by Discord.
    /// </summary>
    public static void Report(
        IErrorArchive? errorArchive,
        IExceptionAlertSink? alertSink,
        ILogger logger,
        string source,
        string method,
        string path,
        Exception exception,
        string? projectId = null,
        string? classification = null)
    {
        var id = Guid.NewGuid().ToString("D");
        var captured = new CapturedErrorDto(
            Id: id,
            Timestamp: DateTimeOffset.UtcNow,
            Source: source,
            Method: method,
            Path: path,
            StatusCode: 500,
            Classification: classification ?? exception.GetType().Name,
            CorrelationId: id,
            Message: exception.Message,
            StackTrace: exception.ToString(),
            ProjectId: projectId,
            Tags: ["handler-caught"]);

        try
        {
            if (errorArchive is not null)
                AlertFireAndForget.Run(errorArchive.CaptureAsync(captured, CancellationToken.None), logger, $"archive for {source}");
        }
        catch { /* best-effort */ }

        try
        {
            if (alertSink is not null)
                AlertFireAndForget.Run(alertSink.NotifyAsync(captured, CancellationToken.None), logger, $"alert for {source}");
        }
        catch { /* best-effort */ }
    }
}
