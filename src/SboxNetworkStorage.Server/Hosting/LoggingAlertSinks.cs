using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>Self-hosted servers report captured exceptions to the server log instead of an external channel.</summary>
public sealed class LoggingExceptionAlertSink(ILogger<LoggingExceptionAlertSink> logger) : IExceptionAlertSink
{
    public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        logger.LogError(
            "Captured error {Classification} {Method} {Path} status={StatusCode} project={ProjectId} correlationId={CorrelationId}: {Message}",
            capturedError.Classification,
            capturedError.Method,
            capturedError.Path,
            capturedError.StatusCode,
            capturedError.ProjectId,
            capturedError.CorrelationId,
            capturedError.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Self-hosted servers report data-plane storage errors to the server log.</summary>
public sealed class LoggingNetworkStorageErrorAlertSink(ILogger<LoggingNetworkStorageErrorAlertSink> logger) : INetworkStorageErrorAlertSink
{
    public Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        logger.LogError(
            "Storage {Operation} failed project={ProjectId} collection={CollectionId} key={RecordKey} code={Code}: {Message}",
            error.Operation,
            error.ProjectId,
            error.CollectionId,
            error.RecordKey,
            error.Code,
            error.Message);
        return Task.CompletedTask;
    }
}
