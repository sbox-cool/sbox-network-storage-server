using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Infrastructure;

public interface IProxyErrorReporter
{
    Task<CapturedErrorDto> CaptureAsync(
        HttpContext context,
        int statusCode,
        string classification,
        string message,
        Exception? exception);
}

public sealed class ProxyErrorReporter(
    IErrorArchive errorArchive,
    IExceptionAlertSink alertSink,
    ILogger<ProxyErrorReporter> logger) : IProxyErrorReporter
{
    // Hard cap on how long a proxy request will WAIT for best-effort telemetry.
    // Proxy captures fire on the request hot path when an upstream (Bun
    // storage-api) is unavailable. Archiving and operator alerting (log, Discord, SMTP)
    // must never dominate request latency or pin a worker when those
    // dependencies are slow or unreachable — otherwise a fast 502 degrades into
    // a request-timeout 504/500 and cascades to worker-pool exhaustion. The
    // underlying work keeps running past this budget on an uncancellable token,
    // so a slow-but-reachable dependency still records the report.
    private static readonly TimeSpan ReportWaitBudget = TimeSpan.FromSeconds(3);

    public async Task<CapturedErrorDto> CaptureAsync(
        HttpContext context,
        int statusCode,
        string classification,
        string message,
        Exception? exception)
    {
        var correlationId = CorrelationContext.Get(context);
        var captured = new CapturedErrorDto(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            ".NET compatibility proxy",
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            statusCode,
            classification,
            correlationId,
            message,
            exception?.ToString() ?? message);

        // Reporting runs on CancellationToken.None so a request abort (very
        // common on proxy timeouts/disconnects) does not lose the report, but
        // the request only waits up to ReportWaitBudget for it.
        await AwaitWithBudgetAsync(
            () => errorArchive.CaptureAsync(captured, CancellationToken.None),
            "archive proxied exception",
            correlationId);

        await AwaitWithBudgetAsync(
            () => alertSink.NotifyAsync(captured, CancellationToken.None),
            "send proxied exception alert",
            correlationId);

        return captured;
    }

    private async Task AwaitWithBudgetAsync(Func<Task> workFactory, string action, string? correlationId)
    {
        Task work;
        try
        {
            work = workFactory();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {Action} correlationId={CorrelationId}", action, correlationId);
            return;
        }

        try
        {
            var finished = await Task.WhenAny(work, Task.Delay(ReportWaitBudget)).ConfigureAwait(false);
            if (finished == work)
            {
                await work.ConfigureAwait(false); // observe completion / surface exception
                return;
            }

            logger.LogWarning(
                "Timed out after {Seconds}s waiting to {Action} correlationId={CorrelationId}; continuing in background",
                ReportWaitBudget.TotalSeconds, action, correlationId);
            ObserveInBackground(work, action, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {Action} correlationId={CorrelationId}", action, correlationId);
        }
    }

    // Keep watching the abandoned task so a later fault is logged instead of
    // surfacing as an unobserved TaskScheduler exception.
    private void ObserveInBackground(Task work, string action, string? correlationId)
    {
        _ = work.ContinueWith(
            faulted => logger.LogWarning(
                faulted.Exception,
                "Background {Action} failed correlationId={CorrelationId}",
                action, correlationId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
